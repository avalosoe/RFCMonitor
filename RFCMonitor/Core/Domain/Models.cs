using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MonitorRFC.Domain;

public sealed record Listing(string Key, string Article, string Slug, string Name, string RelativeUrl, bool Document = false)
{
    public string Url => "https://wu1agsprosta001.blob.core.windows.net/agsc-publicaciones/Datos_abiertos/" + RelativeUrl;
}

public sealed record OriginalCell(int Columna, string Nombre, string? Valor);

/// <summary>
/// Representa los datos normalizados y relacionales de un contribuyente en un artículo y listado fiscal.
/// </summary>
public sealed record ContribuyenteDatos(
    string Rfc,
    string RazonSocial,
    string Articulo,
    string Listado,
    string Situacion,
    DateOnly? FechaEfectiva,
    string? TipoPersona = null,
    string? EntidadFederativa = null,
    decimal? Monto = null,
    string? Ejercicio = null,
    string? Periodo = null,
    string? Motivo = null,
    DateOnly? FechaCancelacion = null,
    DateOnly? FechaAutorizacion = null,
    string? Administracion = null,
    string? NumeroOficioSAT = null,
    DateOnly? FechaOficioSAT = null,
    string? NumeroOficioDOF = null,
    DateOnly? FechaOficioDOF = null,
    string? PresuncionOficioSAT = null,
    DateOnly? PresuncionFechaSAT = null,
    string? PresuncionOficioDOF = null,
    DateOnly? PresuncionFechaDOF = null,
    string? DesvirtuadoOficioSAT = null,
    DateOnly? DesvirtuadoFechaSAT = null,
    string? DesvirtuadoOficioDOF = null,
    DateOnly? DesvirtuadoFechaDOF = null,
    string? DefinitivoOficioSAT = null,
    DateOnly? DefinitivoFechaSAT = null,
    string? DefinitivoOficioDOF = null,
    DateOnly? DefinitivoFechaDOF = null,
    string? SentenciaOficioSAT = null,
    DateOnly? SentenciaFechaSAT = null,
    string? SentenciaOficioDOF = null,
    DateOnly? SentenciaFechaDOF = null
)
{
    /// <summary>
    /// Calcula un hash binario SHA-256 de 32 bytes de los campos representativos para delta ultrarrápido en BD.
    /// </summary>
    public byte[] ComputeHash()
    {
        var raw = string.Concat(
            Rfc.Trim().ToUpperInvariant(), "|",
            RazonSocial.Trim(), "|",
            Articulo, "|",
            Listado, "|",
            Situacion.Trim(), "|",
            FechaEfectiva?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", "|",
            Monto?.ToString("F2", CultureInfo.InvariantCulture) ?? "", "|",
            Ejercicio ?? "", "|",
            Periodo ?? "", "|",
            NumeroOficioSAT ?? "", "|",
            NumeroOficioDOF ?? "", "|",
            PresuncionOficioSAT ?? "", "|",
            DefinitivoOficioSAT ?? ""
        );
        return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }
}

public sealed record SourceRow(
    int Table, int Row, string Rfc, string Name, string Article, string Listing, string Status,
    DateOnly? PublicationDate, string? Entity, Dictionary<string, object?> Typed, IReadOnlyList<OriginalCell> Original,
    int HeaderRow = 0, ContribuyenteDatos? Datos = null);

public sealed record SourceDocument(string SourceKey, string ExternalKey, string Origin, string Article, string Listing,
    string Url, string Hash, string EvidencePath, DateOnly? SourceDate, string DateScope, DateTimeOffset Detected,
    string ParserVersion, long? NoteCode = null, string? Title = null, string? Edition = null, string? Agency = null);

/// <summary>Resultado de la ingesta de un archivo o publicación.</summary>
public sealed record ImportResult(
    long VersionId, bool Changed, long Rows, long ReadRows = 0, long RejectedRows = 0,
    long CertifiedRows = 0, string? ReviewDetail = null, int Nuevos = 0, int Modificados = 0, int SinCambios = 0);

public sealed record QueryFilter(string? Article = null, string? Listing = null, bool IncludeHistory = false);

public sealed record Appearance(long Id, long VersionId, string SourceId, string Origin, string Article, string Listing,
    string Rfc, string Name, string Status, DateOnly? PublicationDate, bool Historical, long? NoteCode, string Url,
    int Table, int Row, string DetailJson);

public sealed record QueryHeader(long GenerationId, DateTimeOffset Generated, string SourcesJson, string CoverageJson);

public static partial class Rfc
{
    [GeneratedRegex(@"^[A-Z&Ñ]{3,4}[0-9]{6}[A-Z0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var result = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormC).Trim().Trim('\uFEFF'))
        {
            if (char.IsWhiteSpace(character) || character is '"' or '\'' or '´' or '`' or '‘' or '’' or '‚' or '‛' or '“' or '”' or '„' or '‟') continue;
            result.Append(character == '¥' ? 'Ñ' : character);
        }
        var text = result.ToString().ToUpperInvariant();
        return text.Length > 100 ? text[..100] : text;
    }

    /// <summary>Valida si la cadena cumple estrictamente con el formato tributario del SAT (12 o 13 caracteres).</summary>
    public static bool IsStrictRfc(string? value) => !string.IsNullOrWhiteSpace(value) && Pattern().IsMatch(Normalize(value));

    /// <summary>Indica si el valor es un identificador no vacío almacenable en el sistema (no rechaza datos atípicos).</summary>
    public static bool IsValid(string? value) => !string.IsNullOrWhiteSpace(value);

    public static string[] ValidateMany(IEnumerable<string?>? values)
    {
        if (values is null) throw new ArgumentException("Se requiere un arreglo de RFC.");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Valor inválido o vacío en posición {index}.");
            result.Add(Normalize(value));
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
