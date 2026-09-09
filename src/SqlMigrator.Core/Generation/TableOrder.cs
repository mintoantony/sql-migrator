using SqlMigrator.Model.Schema;

namespace SqlMigrator.Core.Generation;

/// <summary>Orders target tables so that a parent is always inserted before its children.</summary>
public static class TableOrder
{
    public static bool TrySort(
        IReadOnlyList<string> tableFullNames,
        IReadOnlyList<ForeignKeyInfo> foreignKeys,
        out IReadOnlyList<string> ordered,
        out IReadOnlyList<string> cycle)
    {
        var names = new HashSet<string>(tableFullNames, StringComparer.OrdinalIgnoreCase);

        // parents[child] = the tables it must follow
        var parents = tableFullNames.ToDictionary(
            n => n,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        foreach (var fk in foreignKeys)
        {
            if (!names.Contains(fk.ParentFullName) || !names.Contains(fk.ReferencedFullName)) continue;
            if (string.Equals(fk.ParentFullName, fk.ReferencedFullName, StringComparison.OrdinalIgnoreCase)) continue;
            parents[fk.ParentFullName].Add(fk.ReferencedFullName);
        }

        var result = new List<string>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = tableFullNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        while (remaining.Count > 0)
        {
            var ready = remaining.Where(t => parents[t].All(placed.Contains)).ToList();
            if (ready.Count == 0)
            {
                ordered = [];
                cycle = PruneToCycle(remaining, parents);
                return false;
            }

            foreach (var table in ready)
            {
                result.Add(table);
                placed.Add(table);
                remaining.Remove(table);
            }
        }

        ordered = result;
        cycle = [];
        return true;
    }

    /// <summary>
    /// Narrows a stalled remaining set down to the tables genuinely on a cycle. A table that no
    /// other remaining table depends on cannot sit on a cycle — it's merely downstream of one —
    /// so repeatedly strip such tables until a full pass removes nothing.
    /// </summary>
    private static IReadOnlyList<string> PruneToCycle(
        IReadOnlyList<string> remaining,
        IReadOnlyDictionary<string, HashSet<string>> parents)
    {
        var survivors = new HashSet<string>(remaining, StringComparer.OrdinalIgnoreCase);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var table in survivors.ToList())
            {
                var dependedOn = survivors.Any(other =>
                    !string.Equals(other, table, StringComparison.OrdinalIgnoreCase) &&
                    parents[other].Contains(table));

                if (!dependedOn)
                {
                    survivors.Remove(table);
                    changed = true;
                }
            }
        }

        return survivors.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
