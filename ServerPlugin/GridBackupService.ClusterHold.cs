using System;
using System.Collections.Generic;
using System.Linq;
using PluginSdk.Clustering;

namespace ServerPlugin;

// In a cluster, a node backs up only the grids it owns and simulates, and only
// while nothing is moving under them. It writes none while the node starts,
// stops or is fenced, or in its first ClusterSettleSeconds of serving. A drain
// freezes each partition before its Final save, which LocateEntity reports as
// Frozen, so a draining node skips each partition from then on. A grid
// frozen for a handover or still being loaded is skipped. So is a grid in the
// first ClusterSettleSeconds after its partition came to this node (a load or
// a handover) or after it arrived here. The cluster takes partition saves on
// the game thread, where the backups take their object builders too, so the
// two never interleave. Everything here runs on the game thread.
//
// Nodes that only share a storage root (CLUSTER_SHARED_ROOT without a Gateway,
// as the tests' nodes.py starts them) have no ownership to go by. They all back
// up every grid they see and coordinate through the grid locks, as before.
public sealed partial class GridBackupService
{
    public static readonly string NodeId = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID");

    // A cluster runtime with a Gateway: ownership and node state are known
    private static readonly bool ClusterRuntime = !string.IsNullOrEmpty(
        Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")
    );
    private PluginClusterClient cluster;
    private bool stopping;
    private DateTime servingSinceUtc = DateTime.MaxValue;
    private string nodeHold;

    // Owned partition -> its generation and since when this node has it
    private Dictionary<ulong, (long Generation, DateTime Since)> partitions = new();
    private readonly Dictionary<long, DateTime> arrivedUtc = new();

    private TimeSpan Settle => TimeSpan.FromSeconds(Math.Max(0, Config.ClusterSettleSeconds));

    // No new backups from here on: the server is shutting down
    public void Stop()
    {
        if (stopping)
            return;
        stopping = true;
        NodeHold(DateTime.UtcNow);
    }

    // A grid that arrived by handover settles before its first backup here
    public void Arrived(long gridId) => arrivedUtc[gridId] = DateTime.UtcNow;

    // Why this node backs up nothing now, or null. Logs each change.
    private string NodeHold(DateTime now)
    {
        var hold = ComputeNodeHold(now);
        if (hold != nodeHold)
        {
            if (hold == null)
                plugin.Log.Info("Backups resumed", new { node = NodeId });
            else
                plugin.Log.Info("Backups on hold", new { node = NodeId, reason = hold });
            nodeHold = hold;
        }
        return hold;
    }

    private string ComputeNodeHold(DateTime now)
    {
        if (stopping)
            return "shutdown";
        if (!ClusterRuntime)
            return null;

        cluster ??= PluginCluster.ForPlugin(PluginId);
        var context = cluster.Context;
        if (context == null || !context.Available)
        {
            servingSinceUtc = DateTime.MaxValue;
            partitions.Clear();
            return "node not serving";
        }

        if (servingSinceUtc == DateTime.MaxValue)
            servingSinceUtc = now;

        partitions = context.OwnedPartitions.ToDictionary(
            pair => pair.Key,
            pair =>
                partitions.TryGetValue(pair.Key, out var known) && known.Generation == pair.Value
                    ? known
                    : (pair.Value, now)
        );

        foreach (var grid in arrivedUtc.Where(pair => now - pair.Value >= Settle).ToList())
            arrivedUtc.Remove(grid.Key);

        return now - servingSinceUtc < Settle ? "node starting" : null;
    }

    // Why this node must not back up the grid group of entityId now, or null.
    // NodeHold has to run first in the same update.
    private string GridHold(long entityId, DateTime now)
    {
        if (nodeHold != null || !ClusterRuntime)
            return nodeHold;

        var placement = cluster.LocateEntity(entityId);
        if (placement == null)
            return "no cluster views";
        if (placement.Residence != PluginEntityResidence.Local)
            return placement.Residence.ToString();
        if (
            !partitions.TryGetValue(placement.Partition, out var partition)
            || now - partition.Since < Settle
        )
            return "partition settling";
        if (arrivedUtc.TryGetValue(placement.EntityId, out var arrived) && now - arrived < Settle)
            return "grid arrived";
        return null;
    }
}
