using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using VRage.Plugins;

namespace ServerPlugin;

// Puts the companion operations where Quasar.Agent looks for them: a loaded
// plugin that implements Magnetar.Protocol's IQuasarCompanionRequestHandler.
//
// Grid Backups can't implement that interface itself. Magnetar compiles hub
// plugins from source against the game, PluginSdk and a few libraries only
// (Legacy/References.cs in Magnetar), not Magnetar.Protocol, and the plugin
// must load where no Quasar.Agent is. So once Quasar.Agent has loaded
// Magnetar.Protocol, this emits a small adapter type that implements IPlugin
// and the handler interface, forwards the requests to CompanionOperations, and
// adds an instance to the game's plugin list, which Quasar.Agent searches.
public static class QuasarCompanionBridge
{
    private const string ProtocolAssembly = "Magnetar.Protocol";
    private const string HandlerType = "Magnetar.Protocol.Bridge.IQuasarCompanionRequestHandler";
    private const string ResponseType = "Magnetar.Protocol.Model.CompanionPluginResponse";

    private static CompanionOperations operations;
    private static DateTime nextTryUtc;

    public static bool Registered { get; private set; }

    // Game thread. Cheap enough to call every update: looks every 10 seconds
    // until Quasar.Agent's protocol assembly shows up.
    public static void TryRegister(CompanionOperations companion)
    {
        if (Registered || DateTime.UtcNow < nextTryUtc)
            return;
        nextTryUtc = DateTime.UtcNow.AddSeconds(10);

        var handler = AppDomain
            .CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == ProtocolAssembly)
            .Select(a => a.GetType(HandlerType, false))
            .FirstOrDefault(t => t != null);
        if (handler == null)
            return;

        operations = companion;
        var adapter = (IPlugin)Activator.CreateInstance(EmitAdapter(handler));
        var plugins =
            (List<IPlugin>)
                typeof(MyPlugins)
                    .GetField("m_plugins", BindingFlags.NonPublic | BindingFlags.Static)
                    .GetValue(null);
        // Not now: the game is iterating the plugin list to update this plugin.
        // Invoked actions run before the plugins' updates.
        MySandboxGame.Static.Invoke(() => plugins.Add(adapter), "GridBackups companion bridge");
        Registered = true;
    }

    // Called by the adapter. Returns Task<CompanionPluginResponse> as object.
    public static object Dispatch(object request, CancellationToken cancellationToken)
    {
        var requestType = request.GetType();
        string Read(string name) => requestType.GetProperty(name)?.GetValue(request)?.ToString();
        var schemaVersion = (int)requestType.GetProperty("SchemaVersion").GetValue(request);
        var correlationId = Read("CorrelationId");

        var responseType = requestType.Assembly.GetType(ResponseType);
        var completionType = typeof(TaskCompletionSource<>).MakeGenericType(responseType);
        var completion = Activator.CreateInstance(completionType);
        var setResult = completionType.GetMethod("SetResult");

        operations
            .HandleAsync(Read("Operation"), Read("PayloadJson"), cancellationToken)
            .ContinueWith(
                task =>
                {
                    var reply =
                        task.Status == TaskStatus.RanToCompletion
                            ? task.Result
                            : new CompanionReply
                            {
                                Error = task.Exception?.GetBaseException().Message ?? "Canceled.",
                            };
                    var response = Activator.CreateInstance(responseType);
                    void Set(string name, object value) =>
                        responseType.GetProperty(name).SetValue(response, value);
                    Set("SchemaVersion", schemaVersion);
                    Set("CorrelationId", correlationId ?? string.Empty);
                    Set("Success", reply.Success);
                    Set("PayloadJson", reply.PayloadJson ?? string.Empty);
                    Set("Error", reply.Error ?? string.Empty);
                    setResult.Invoke(completion, new[] { response });
                },
                TaskScheduler.Default
            );

        return completionType.GetProperty("Task").GetValue(completion);
    }

    // class GridBackupsQuasarCompanion : IPlugin, IQuasarCompanionRequestHandler
    // {
    //     PluginId => GridBackupService.PluginId;
    //     HandleQuasarCompanionRequestAsync(r, c) => (Task<...>)Dispatch(r, c);
    //     Init, Update, Dispose: nothing
    // }
    private static Type EmitAdapter(Type handler)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("GridBackups.QuasarCompanion"),
            AssemblyBuilderAccess.Run
        );
        var type = assembly
            .DefineDynamicModule("GridBackups.QuasarCompanion")
            .DefineType(
                "GridBackups.QuasarCompanion",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                typeof(object),
                new[] { typeof(IPlugin), handler }
            );

        var interfaces = new[] { typeof(IPlugin), handler }
            .SelectMany(i => i.GetInterfaces().Prepend(i))
            .Distinct();
        foreach (var method in interfaces.SelectMany(i => i.GetMethods()))
        {
            var parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();
            var implementation = type.DefineMethod(
                method.DeclaringType.Name + "." + method.Name,
                MethodAttributes.Private
                    | MethodAttributes.Virtual
                    | MethodAttributes.Final
                    | MethodAttributes.HideBySig
                    | MethodAttributes.NewSlot,
                method.ReturnType,
                parameters
            );
            var il = implementation.GetILGenerator();
            switch (method.Name)
            {
                case "get_PluginId":
                    il.Emit(OpCodes.Ldstr, GridBackupService.PluginId);
                    break;
                case "HandleQuasarCompanionRequestAsync":
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(
                        OpCodes.Call,
                        typeof(QuasarCompanionBridge).GetMethod(nameof(Dispatch))
                    );
                    il.Emit(OpCodes.Castclass, method.ReturnType);
                    break;
                default:
                    if (method.ReturnType != typeof(void))
                        throw new InvalidOperationException(
                            $"Unexpected companion interface member {method.Name}"
                        );
                    break;
            }

            il.Emit(OpCodes.Ret);
            type.DefineMethodOverride(implementation, method);
        }

        return type.CreateTypeInfo();
    }
}
