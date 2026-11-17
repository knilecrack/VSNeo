using System;
using System.Collections.Generic;

namespace VSNeo.Seeky;

/// <summary>
/// One row per class, not one per file.
///
/// A partial class is a single type declared in several files, and the
/// engine's index reports the declaration in each of them - the picker listed
/// <c>SeekyEngineClient</c> twice, once for the file and once for its
/// Transport part, and the bigger splits would list up to seven rows for one
/// class. Nothing is wrong with the rows (each jumps to a real file), but a
/// symbol search is about finding the type, not about enumerating its parts.
///
/// Only declarations that say <c>partial</c> are collapsed, and only against
/// another declaration of the same name and kind: two unrelated classes that
/// happen to share a name (Program, Settings) are left exactly as the index
/// reported them. The first of the group survives, which is the best-ranked
/// one, so the row a search would have landed on is the row that stays.
/// </summary>
internal static class SeekySymbolCollapse
{
    public static List<SeekyEngineClient.SymbolHit> PartialClasses(IReadOnlyList<SeekyEngineClient.SymbolHit> hits)
    {
        var kept = new List<SeekyEngineClient.SymbolHit>(hits.Count);
        var seen = new HashSet<(string Kind, string Name)>();
        foreach (SeekyEngineClient.SymbolHit hit in hits)
        {
            if (IsPartial(hit) && !seen.Add((hit.Kind, hit.Name)))
            {
                continue;
            }

            kept.Add(hit);
        }

        return kept;
    }

    private static bool IsPartial(SeekyEngineClient.SymbolHit hit) =>
        hit.Text.Contains("partial ", StringComparison.Ordinal);
}
