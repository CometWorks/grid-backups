using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using PluginSdk.Commands;
using PluginSdk.Config;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Plugins;
using VRageMath;

namespace GridBackupsProbe;

// Test hooks of the Grid Backups suite: the backup service's state, the plugin
// config swapped at runtime, grid contents for comparing restores, and a console
// that runs chat commands from a file. Reaches Grid Backups by reflection, so it
// needs no build dependency on it, and loads without it: the console then still
// works, which is what a multi-node fixture of another plugin needs.
public class Probe : IPlugin
{
    private int tick;

    public void Init(object gameInstance) =>
        ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(ProbeCommands));

    public void Update()
    {
        if (++tick % 10 == 0)
            FileConsole.Poll();
    }

    public void Dispose() { }
}

// Runs the lines of probe-cmd.txt as the server console, "<id>\t<command>" each,
// and appends the replies to probe-out.txt as "<id>\t<reply>", then "<id>\t." when
// the command is done. The folder is GB_PROBE_DIR, or the DS data folder. The
// driver writes probe-cmd.tmp and renames it, so the file is complete here.
internal static class FileConsole
{
    private static string Dir =>
        Environment.GetEnvironmentVariable("GB_PROBE_DIR") ?? MyFileSystem.UserDataPath;

    public static void Poll()
    {
        string cmd = Path.Combine(Dir, "probe-cmd.txt");
        if (!File.Exists(cmd))
            return;

        string taken = cmd + ".taken";
        try
        {
            File.Delete(taken);
            File.Move(cmd, taken);
        }
        catch (IOException)
        {
            return;
        }

        foreach (string line in File.ReadAllLines(taken).Where(l => l.Contains('\t')))
        {
            string[] parts = line.Split(new[] { '\t' }, 2);
            var output = new List<string>();
            try
            {
                var caller = new CommandCaller(
                    0,
                    0,
                    "GridBackupsProbe",
                    MyPromoteLevel.Owner,
                    isConsole: true
                );
                if (!Dispatcher.Handle(parts[1], caller, new Responder(output)))
                    output.Add("Probe console: no such command");
            }
            catch (Exception ex)
            {
                output.Add("Probe console: " + ex.GetType().Name + ": " + ex.Message);
            }

            output.Add(".");
            File.AppendAllLines(
                Path.Combine(Dir, "probe-out.txt"),
                output.Select(o => parts[0] + "\t" + o.Replace("\n", "|"))
            );
        }
    }

    // The dispatcher Magnetar's chat hook uses. Newer PluginSdk versions have
    // ServerCommands.Execute for this; the field keeps the probe working on older ones.
    private static CommandDispatcher Dispatcher =>
        (CommandDispatcher)
            ServerCommands
                .Registrar.GetType()
                .GetField("dispatcher", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(ServerCommands.Registrar);

    private sealed class Responder : ICommandResponder
    {
        private readonly List<string> output;

        public Responder(List<string> output) => this.output = output;

        public void Send(in CommandReply reply, in CommandCaller caller) => output.Add(reply.Text);
    }
}

[CommandRoot("gbprobe", "GridBackupsProbe", "Grid Backups test hooks")]
public sealed class ProbeCommands : CommandModule
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static Assembly GridBackups =>
        AppDomain
            .CurrentDomain.GetAssemblies()
            .First(a => a.GetType("ServerPlugin.GridBackupService") != null);

    private static object Plugin =>
        GridBackups.GetType("ServerPlugin.Plugin").GetProperty("Instance").GetValue(null);

    private static object Service => Get(Plugin, "Backups");

    private static object Get(object target, string name) =>
        target.GetType().GetProperty(name, Any).GetValue(target);

    private static FieldInfo Field(object target, string name) =>
        target.GetType().GetField(name, Any);

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string V(Vector3D v) => $"{F(v.X)},{F(v.Y)},{F(v.Z)}";

    private static MyCubeGrid FindGrid(long id) =>
        MyEntities.GetEntityById(id) is MyCubeGrid grid && !grid.MarkedForClose ? grid : null;

    private static List<MyCubeGrid> Group(MyCubeGrid grid) =>
        MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid).OrderBy(g => g.EntityId).ToList();

    // --- config and service state --------------------------------------------

    [Command("reload", "Loads GridBackups.cfg into the running plugin.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Reload()
    {
        object live = Get(Plugin, "Config");
        Type type = live.GetType();
        string path = Path.Combine((string)Get(Plugin, "PluginDataPath"), "GridBackups.cfg");
        object loaded = typeof(ConfigStorage)
            .GetMethod(nameof(ConfigStorage.LoadXml))
            .MakeGenericMethod(type)
            .Invoke(null, new object[] { path });

        foreach (
            PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
            )
        )
        {
            if (property.CanRead && property.CanWrite)
                property.SetValue(live, property.GetValue(loaded));
        }

        return "Probe: config reloaded";
    }

    [Command("forget", "Forgets every grid's backup state and the last age cleanup.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Forget()
    {
        object service = Service;
        object stateLock = Field(service, "stateLock").GetValue(service);
        lock (stateLock)
            ((IDictionary)Field(service, "states").GetValue(service)).Clear();
        Field(service, "lastCleanupUtc").SetValue(service, DateTime.MinValue);
        return "Probe forget: done";
    }

    [Command(
        "scan",
        "Schedules the next backup scan this many seconds from now, 0 for the next update."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Scan(double seconds)
    {
        object service = Service;
        Field(service, "nextScanUtc").SetValue(service, DateTime.UtcNow.AddSeconds(seconds));
        return "Probe scan: scheduled";
    }

    [Command("idle", "How many backups are running or queued.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Idle()
    {
        object service = Service;
        int running = (int)Field(service, "runningBackups").GetValue(service);
        int queued = ((ICollection)Field(service, "queuedGrids").GetValue(service)).Count;
        return $"Probe idle: running={running} queued={queued}";
    }

    private static object State(long gridId)
    {
        var states = (IDictionary)Field(Service, "states").GetValue(Service);
        return states.Contains(gridId) ? states[gridId] : null;
    }

    [Command("state", "The backup state of a grid group, by its biggest grid.")]
    [Permission(MyPromoteLevel.Admin)]
    public string StateCommand(long gridId)
    {
        object state = State(gridId);
        if (state == null)
            return "Probe state: none";

        DateTime now = DateTime.UtcNow;
        var backup = (DateTime)Get(state, "LastBackupUtc");
        var changed = (DateTime)Get(state, "LastChangedUtc");
        string ago = backup == DateTime.MinValue ? "never" : F((now - backup).TotalSeconds);
        return $"Probe state: backup={ago} changed={F((now - changed).TotalSeconds)} "
            + $"observed={Get(state, "LastObservedBlockCount")} backupblocks={Get(state, "LastBackupBlockCount")}";
    }

    [Command("age", "Moves a grid's last backup and last change this many seconds back.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Age(long gridId, double seconds)
    {
        object state = State(gridId);
        if (state == null)
            return "Probe age: none";

        object stateLock = Field(Service, "stateLock").GetValue(Service);
        lock (stateLock)
        {
            foreach (string name in new[] { "LastBackupUtc", "LastChangedUtc" })
            {
                PropertyInfo property = state.GetType().GetProperty(name);
                var value = (DateTime)property.GetValue(state);
                if (value != DateTime.MinValue)
                    property.SetValue(state, value.AddSeconds(-seconds));
            }
        }

        return "Probe age: done";
    }

    [Command("flush", "Writes out the game log, which the game buffers.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Flush()
    {
        VRage.Utils.MyLog.Default.Flush();
        return "Probe flush: done";
    }

    // --- grids ----------------------------------------------------------------

    [Command("grid", "A grid on the server: name, blocks, owner, position and items.")]
    [Permission(MyPromoteLevel.Admin)]
    public string GridCommand(long gridId)
    {
        MyCubeGrid grid = FindGrid(gridId);
        if (grid == null)
            return "Probe grid: none";

        long owner = grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
        var items = new SortedDictionary<string, double>();
        var owners = new SortedSet<long>();
        foreach (MyCubeBlock block in grid.GetFatBlocks())
        {
            owners.Add(block.OwnerId);
            for (int i = 0; i < block.InventoryCount; i++)
                foreach (IMyInventoryItem item in ((IMyEntity)block).GetInventory(i).GetItems())
                {
                    string key =
                        item.Content.TypeId.ToString().Replace("MyObjectBuilder_", "")
                        + "/"
                        + item.Content.SubtypeName;
                    items[key] =
                        (items.TryGetValue(key, out double sum) ? sum : 0) + (double)item.Amount;
                }
        }

        string itemList =
            items.Count == 0 ? "-" : string.Join(",", items.Select(p => $"{p.Key}:{F(p.Value)}"));
        string name = grid.DisplayName.Replace(' ', '_');
        return $"Probe grid: name={name} blocks={grid.BlocksCount} owner={owner} "
            + $"owners={string.Join(",", owners)} pos={V(grid.PositionComp.GetPosition())} "
            + $"static={(grid.IsStatic ? 1 : 0)} group={string.Join(",", Group(grid).Select(g => g.EntityId))} "
            + $"items={itemList}";
    }

    [Command("named", "Entity ids of the grids with this name, spaces as _.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Named(string name)
    {
        IEnumerable<long> ids = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(g => !g.MarkedForClose && g.DisplayName.Replace(' ', '_') == name)
            .Select(g => g.EntityId)
            .OrderBy(i => i);
        return $"Probe named: {string.Join(" ", ids)}".TrimEnd();
    }

    [Command("prune", "Removes every grid not listed, comma separated, nor joined to one listed.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Prune(string keepIds)
    {
        var keep = new HashSet<long>(keepIds.Split(',').Select(long.Parse));
        List<MyCubeGrid> remove = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(g =>
                !g.MarkedForClose && !g.IsPreview && !Group(g).Any(m => keep.Contains(m.EntityId))
            )
            .ToList();
        foreach (MyCubeGrid grid in remove)
            grid.Close();
        return $"Probe prune: {remove.Count}";
    }

    [Command("place", "Puts a grid at a position, at rest.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Place(long gridId, double x, double y, double z)
    {
        MyCubeGrid grid = FindGrid(gridId);
        if (grid == null)
            return "Probe place: none";

        grid.Teleport(MatrixD.CreateWorld(new Vector3D(x, y, z), Vector3D.Forward, Vector3D.Up));
        if (grid.Physics != null)
        {
            grid.Physics.LinearVelocity = Vector3.Zero;
            grid.Physics.AngularVelocity = Vector3.Zero;
        }

        return "Probe place: done";
    }

    [Command("raze", "Removes this many armor blocks from a grid, highest x first.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Raze(long gridId, int count)
    {
        MyCubeGrid grid = FindGrid(gridId);
        if (grid == null)
            return "Probe raze: none";

        List<Vector3I> cells = grid.GetBlocks()
            .Where(b => b.FatBlock == null)
            .OrderByDescending(b => b.Position.X)
            .ThenByDescending(b => b.Position.Z)
            .Take(count)
            .Select(b => b.Position)
            .ToList();
        grid.RazeBlocks(cells);
        return $"Probe raze: blocks={grid.BlocksCount}";
    }

    [Command("give", "Puts items into a block's first inventory: type subtype amount.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Give(long blockId, string type, string subtype, double amount)
    {
        if (!(MyEntities.GetEntityById(blockId) is MyCubeBlock block) || block.InventoryCount == 0)
            return "Probe give: none";

        var content = (MyObjectBuilder_PhysicalObject)
            MyObjectBuilderSerializer.CreateNewObject(
                MyObjectBuilderType.Parse("MyObjectBuilder_" + type),
                subtype
            );
        ((IMyEntity)block).GetInventory(0).AddItems((MyFixedPoint)amount, content);
        return "Probe give: done";
    }

    [Command("clear", "Empties a block's inventories.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Clear(long blockId)
    {
        if (!(MyEntities.GetEntityById(blockId) is MyCubeBlock block))
            return "Probe clear: none";

        for (int i = 0; i < block.InventoryCount; i++)
            ((IMyEntity)block).GetInventory(i).Clear();
        return "Probe clear: done";
    }

    [Command("attach", "Builds the top part of a rotor or piston base that has none.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Attach(long blockId)
    {
        if (!(MyEntities.GetEntityById(blockId) is MyMechanicalConnectionBlockBase stator))
            return "Probe attach: none";

        if (stator.TopGrid == null)
            typeof(MyMechanicalConnectionBlockBase)
                .GetMethod("RecreateTop", Any)
                .Invoke(
                    stator,
                    new object[]
                    {
                        stator.OwnerId,
                        MyMechanicalConnectionBlockBase.MyTopBlockSize.Normal,
                        true,
                    }
                );

        return $"Probe attach: top={stator.TopGrid?.EntityId ?? 0}";
    }

    // --- the public entry points, called the way another plugin would ---------

    private static string CallEntryPoint(string method, long gridId)
    {
        MyCubeGrid grid = FindGrid(gridId);
        if (grid == null)
            return "none";

        List<MyObjectBuilder_CubeGrid> builders = Group(grid)
            .Select(g => (MyObjectBuilder_CubeGrid)g.GetObjectBuilder())
            .ToList();
        long owner = grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
        object plugin = Plugin;
        return plugin
            .GetType()
            .GetMethod(method)
            .Invoke(plugin, new object[] { builders, owner })
            .ToString();
    }

    [Command("conceal", "Calls BackupBeforeConcealment with a grid's group, as Concealment would.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Conceal(long gridId) =>
        "Probe conceal: " + CallEntryPoint("BackupBeforeConcealment", gridId);

    [Command(
        "external",
        "Calls BackupGridsManuallyWithBuilders with a grid's group, `times` times in a row."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string External(long gridId, int times = 1) =>
        "Probe external: "
        + string.Join(
            " ",
            Enumerable
                .Range(0, times)
                .Select(_ => CallEntryPoint("BackupGridsManuallyWithBuilders", gridId))
        );

    // --- players --------------------------------------------------------------

    [Command(
        "promote",
        "Sets a player's promote level: None, Scripter, Moderator, SpaceMaster, Admin."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Promote(ulong steamId, string level)
    {
        MySession.Static.SetUserPromoteLevel(
            steamId,
            (MyPromoteLevel)Enum.Parse(typeof(MyPromoteLevel), level)
        );
        return $"Probe promote: {MySession.Static.GetUserPromoteLevel(steamId)}";
    }

    [Command("player", "A player's character position and facing.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Player(long identityId)
    {
        var character = MySession.Static.Players.TryGetIdentity(identityId)?.Character;
        if (character == null)
            return "Probe player: none";

        return $"Probe player: pos={V(character.PositionComp.GetPosition())} forward={V(character.WorldMatrix.Forward)}";
    }
}
