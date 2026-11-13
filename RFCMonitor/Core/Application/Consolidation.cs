using System.Runtime.CompilerServices;
using MonitorRFC.Domain;
namespace MonitorRFC.Application;

public sealed record ConsolidatedRfc(string Rfc, bool Encontrado, string[] NombresPublicados,
    RfcSummary Resumen, IReadOnlyList<Appearance> Apariciones);
public sealed record RfcSummary(string[] Articulos, string[] Listados, string[] Estados, int NumeroApariciones, bool EnMultiplesListados, bool TieneDiscrepancias);
public static class Consolidation
{
    public static async IAsyncEnumerable<ConsolidatedRfc> ReadAsync(IAsyncEnumerable<Appearance> source,
        string[]? requested, [EnumeratorCancellation] CancellationToken ct)
    {
        var index = 0;
        string? current = null;
        var rows = new List<Appearance>();
        await foreach (var row in source.WithCancellation(ct))
        {
            if (current != row.Rfc)
            {
                if (current is not null)
                {
                    yield return Build(current, rows);
                    if (requested is not null) index++;
                }
                while (requested is not null && index < requested.Length && string.CompareOrdinal(requested[index], row.Rfc) < 0)
                    yield return Build(requested[index++], []);
                current = row.Rfc;
                rows = [];
            }
            rows.Add(row);
        }
        if (current is not null)
        {
            yield return Build(current, rows);
            if (requested is not null) index++;
        }
        while (requested is not null && index < requested.Length) yield return Build(requested[index++], []);
    }
    /// <summary>Omite la aparición vigente de "publicaciones-dof" ya cubierta por un listado del SAT del mismo artículo con fecha igual o más reciente.</summary>
    private static List<Appearance> CollapseDof(List<Appearance> rows) =>
        rows.Where(r => r.Historical || r.Listing != "publicaciones-dof" || !r.PublicationDate.HasValue
            || !rows.Any(o => !o.Historical && o.Listing != "publicaciones-dof" && o.Article == r.Article && o.PublicationDate >= r.PublicationDate)).ToList();

    private static ConsolidatedRfc Build(string rfc, List<Appearance> rows)
    {
        rows = CollapseDof(rows);
        var listings = rows.Select(r => r.Article + "/" + r.Listing).Distinct().ToArray();
        return new(rfc, rows.Count > 0, rows.Select(r => r.Name).Distinct().ToArray(),
            new(rows.Select(r => r.Article).Distinct().ToArray(), listings,
                rows.Select(r => r.Status).Distinct().ToArray(), rows.Count, listings.Length > 1,
                rows.Where(r => !r.Historical).GroupBy(r => (r.Article, r.Listing, r.PublicationDate))
                    .Any(g => g.Select(r => TextKey.Normalize(r.Status)).Distinct().Count() > 1)), rows);
    }
}
