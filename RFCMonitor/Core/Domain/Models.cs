using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MonitorRFC.Domain;

public sealed record Listing(string Key, string Article, string Slug, string Name, string RelativeUrl, string Table, bool Document = false)
{
    public string Url => "https://wu1agsprosta001.blob.core.windows.net/agsc-publicaciones/Datos_abiertos/" + RelativeUrl;
}
public sealed record OriginalCell(int Columna, string Nombre, string? Valor);
public sealed record SourceRow(int Table, int Row, string Rfc, string Name, string Article, string Listing, string Status,
    DateOnly? PublicationDate, string? Entity, Dictionary<string, object?> Typed, IReadOnlyList<OriginalCell> Original);
public sealed record SourceDocument(string SourceKey, string ExternalKey, string Origin, string Article, string Listing,
    string Url, string Hash, string EvidencePath, DateOnly? SourceDate, string DateScope, DateTimeOffset DetectedUtc,
    string ParserVersion, long? NoteCode = null, string? Title = null, string? Edition = null, string? Agency = null);
public sealed record ImportResult(long VersionId, bool Changed, long Rows);
public sealed record QueryFilter(string? Article = null, string? Listing = null, bool IncludeHistory = false);
public sealed record Appearance(long Id, long VersionId, string SourceId, string Origin, string Article, string Listing,
    string Rfc, string Name, string Status, DateOnly? PublicationDate, bool Historical, long? NoteCode, string Url,
    int Table, int Row, string DetailJson);
public sealed record QueryHeader(long GenerationId, DateTimeOffset GeneratedUtc, string SourcesJson, string CoverageJson);

public static partial class Rfc
{
    [GeneratedRegex(@"^[A-Z&Ñ]{3,4}[0-9]{6}[A-Z0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
    public static string Normalize(string value) => value.Trim().ToUpperInvariant();
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(Normalize(value));
    public static string[] ValidateMany(IEnumerable<string?>? values)
    {
        if (values is null) throw new ArgumentException("Se requiere un arreglo de RFC.");
        var result = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var value in values)
        {
            if (!IsValid(value)) throw new ArgumentException($"RFC inválido en posición {index}.");
            result.Add(Normalize(value!));
            index++;
        }
        if (result.Count == 0) throw new ArgumentException("El arreglo de RFC no puede estar vacío.");
        return result.Order(StringComparer.Ordinal).ToArray();
    }
}
public static class TextKey
{
    public static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))).ToUpperInvariant();
}

