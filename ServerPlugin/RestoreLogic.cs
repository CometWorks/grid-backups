using System;
using System.Collections.Generic;
using System.Linq;
using VRageMath;

namespace ServerPlugin;

// The decisions of the companion operations that need no game: who the big
// owners are, which backup a restore takes, why a grid closed and where a
// restored grid may go. Unit tested in tests/unit.
public static class RestoreLogic
{
    // How long after a block's destruction the grid's close still counts as
    // caused by it. The game closes an empty grid in the next update.
    public static readonly TimeSpan RemovalWindow = TimeSpan.FromSeconds(5);

    // The game's rule (MyCubeGridOwnershipManager): the owners of the most
    // owned blocks, several on a tie, none when no block has an owner
    public static List<long> BigOwners(IEnumerable<long> blockOwners)
    {
        var counts = blockOwners
            .Where(owner => owner != 0)
            .GroupBy(owner => owner)
            .Select(g => (Owner: g.Key, Count: g.Count()))
            .ToList();
        if (counts.Count == 0)
            return new List<long>();

        var max = counts.Max(c => c.Count);
        return counts.Where(c => c.Count == max).Select(c => c.Owner).OrderBy(o => o).ToList();
    }

    // R8: the newest backup taken before the grid was removed, or the newest
    // of all when its removal wasn't logged; never one taken later
    public static T ChooseBackup<T>(
        IEnumerable<T> backups,
        Func<T, DateTime> takenUtc,
        DateTime? removedUtc
    )
        where T : class =>
        backups
            .Where(b => removedUtc == null || takenUtc(b) < removedUtc.Value)
            .OrderByDescending(takenUtc)
            .FirstOrDefault();

    // Why a grid closed, from what was known when it did: a merge moving its
    // blocks out, a removal path running (trash removal, an admin's delete),
    // a Hangar frame on the stack, or its last block destroyed or ground down
    // just before. Merges first: an admin may delete a grid mid-merge, but
    // the merge empties it either way.
    public static string Classify(
        bool blocksMovedOut,
        string removalScope,
        bool hangarOnStack,
        string lastBlockRemoval,
        DateTime? lastBlockRemovalUtc,
        DateTime nowUtc
    )
    {
        if (blocksMovedOut)
            return LossCause.Merged;
        if (removalScope != null)
            return removalScope;
        if (hangarOnStack)
            return LossCause.Hangar;
        if (
            lastBlockRemoval != null
            && lastBlockRemovalUtc.HasValue
            && nowUtc - lastBlockRemovalUtc.Value <= RemovalWindow
        )
            return lastBlockRemoval;
        return LossCause.Unknown;
    }

    // Searches outward from start for a spot that is free and at least
    // exclusionMeters from every foreign grid: start first, then shells of
    // 26 directions every step meters, up to maxMeters
    public static Vector3D? FindSpot(
        Vector3D start,
        double step,
        double maxMeters,
        IReadOnlyList<Vector3D> foreignGrids,
        double exclusionMeters,
        Func<Vector3D, bool> isFree
    )
    {
        foreach (var candidate in Candidates(start, step, maxMeters))
        {
            if (IsExcluded(candidate, foreignGrids, exclusionMeters))
                continue;
            if (isFree(candidate))
                return candidate;
        }

        return null;
    }

    public static bool IsExcluded(
        Vector3D position,
        IReadOnlyList<Vector3D> foreignGrids,
        double exclusionMeters
    )
    {
        var limit = exclusionMeters * exclusionMeters;
        return foreignGrids.Any(g => Vector3D.DistanceSquared(g, position) < limit);
    }

    private static readonly Vector3D[] Directions = MakeDirections();

    private static Vector3D[] MakeDirections()
    {
        var directions = new List<Vector3D>();
        for (var x = -1; x <= 1; x++)
        for (var y = -1; y <= 1; y++)
        for (var z = -1; z <= 1; z++)
            if (x != 0 || y != 0 || z != 0)
                directions.Add(Vector3D.Normalize(new Vector3D(x, y, z)));
        return directions.ToArray();
    }

    private static IEnumerable<Vector3D> Candidates(Vector3D start, double step, double maxMeters)
    {
        yield return start;
        if (step <= 0)
            yield break;

        for (var radius = step; radius <= maxMeters; radius += step)
            foreach (var direction in Directions)
                yield return start + direction * radius;
    }
}

public static class LossCause
{
    public const string Destroyed = "destroyed";
    public const string Deleted = "deleted";
    public const string Cleanup = "cleanup";
    public const string Ground = "ground";
    public const string Merged = "merged";
    public const string Hangar = "hangar";
    public const string Unknown = "unknown";
}
