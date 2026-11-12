using System.Text.Json;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using MonitorRFC.Application;
using MonitorRFC.Domain;
using MonitorRFC.Infrastructure.Persistence;
namespace MonitorRFC.Controllers;

[ApiController]
public sealed class RfcsController(IRfcQueryService queries, SqlRepository repository, IClock clock) : ControllerBase
{
    [HttpGet("api/v1/articulos")]
    public IActionResult Articles() => Ok(SourceCatalog.Articles);
    [HttpGet("health/live")]
    public IActionResult Live() => Ok(new { estado = "online" });
    [HttpGet("health/ready")]
    public async Task<IActionResult> Ready(CancellationToken ct) => await queries.GetHeaderAsync(ct) is null
        ? Problem(statusCode: 503, title: "Sin generación publicada.") : Ok(new { estado = "ready" });
    [HttpGet("api/v1/rfcs")]
    [HttpGet("api/EdosEfos/all69")]
    public Task<IActionResult> All(bool incluirHistorial = false, CancellationToken ct = default) => Query(null, null, null, incluirHistorial, ct);
    [HttpPost("api/v1/rfcs/consultar")]
    public Task<IActionResult> Selected([FromBody] string?[]? rfcs, string? articulo = null, string? listado = null,
        bool incluirHistorial = false, CancellationToken ct = default) => Query(rfcs, articulo, listado, incluirHistorial, ct, true);
    public sealed record LegacyInput(string?[]? Rfcs);
    [HttpPost("api/Rfc/Listado69")]
    public Task<IActionResult> Legacy([FromBody] LegacyInput? request, bool incluirHistorial = false, CancellationToken ct = default) => Query(request?.Rfcs, null, null, incluirHistorial, ct, true);
    [HttpGet("api/v1/articulos/{articulo}/contribuyentes")]
    [HttpGet("api/v1/articulos/{articulo}/listados/{listado}/contribuyentes")]
    public Task<IActionResult> ByArticle(string articulo, string? listado = null, bool incluirHistorial = false, CancellationToken ct = default) => Query(null, articulo, listado, incluirHistorial, ct);
    [HttpGet("api/v1/rfcs/{rfc}/estatus")]
    public Task<IActionResult> CurrentStatus(string rfc, CancellationToken ct = default) => Query([rfc], null, null, false, ct, true);
    [HttpGet("api/v1/rfcs/{rfc}/historial")]
    public Task<IActionResult> History(string rfc, string? articulo = null, string? listado = null, CancellationToken ct = default) =>
        Query([rfc], articulo, listado, true, ct, true);
    [HttpGet("api/v1/articulos/{articulo}/listados")]
    public IActionResult Listings(string articulo)
    {
        if (!SourceCatalog.Articles.Contains(articulo)) return NotFound();
        return Ok(SourceCatalog.Sat.Where(l => l.Article == articulo && !l.Document)
            .Select(l => new { clave = l.Slug, nombre = l.Name, fuente = "SAT" })
            .Append(new { clave = "publicaciones-dof", nombre = "Publicaciones DOF", fuente = "DOF" }));
    }
    [HttpGet("api/v1/fuentes/estado")]
    public async Task<IActionResult> Status(CancellationToken ct) => Ok(await repository.GetOperationalStateAsync(ct));
    [HttpGet("api/v1/articulos/69/documento-tecnico")]
    [HttpGet("api/v1/documentos/69/documento-tecnico")]
    public async Task<IActionResult> Document(CancellationToken ct)
    {
        var header = await queries.GetHeaderAsync(ct);
        if (header is null) return Problem(statusCode: 503, title: "Aún no hay una generación publicada.");
        return Ok(new { generacionId = header.GenerationId, celdas = repository.ReadDocumentAsync(header.GenerationId, ct) });
    }
    private async Task<IActionResult> Query(string?[]? input, string? article, string? listing, bool history, CancellationToken ct, bool filter = false)
    {
        if (Request.Query.Keys.Any(k => new[] { "pagina", "tamanoPagina", "page", "pageSize" }.Contains(k, StringComparer.OrdinalIgnoreCase)))
            return Problem(statusCode: 400, title: "Esta API devuelve todos los resultados y no admite paginación.");
        if (article is not null && !SourceCatalog.Articles.Contains(article) || listing is not null && !SourceCatalog.IsListing(article!, listing)) return NotFound();
        string[]? rfcs = null;
        try { if (filter) rfcs = Rfc.ValidateMany(input); }
        catch (ArgumentException ex) { return Problem(statusCode: 400, title: ex.Message); }
        var header = await queries.GetHeaderAsync(ct);
        if (header is null) return Problem(statusCode: 503, title: "Aún no hay una generación publicada.");
        var selection = new QueryFilter(article, listing, history);
        var operational = await repository.GetOperationalStateAsync(ct);
        return new RfcStreamResult(queries, header, selection, rfcs, operational, clock.Now);
    }
}

public sealed class RfcStreamResult(IRfcQueryService queries, QueryHeader header, QueryFilter filter, string[]? rfcs,
    JsonElement operational, DateTimeOffset now) : IActionResult
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        http.Response.ContentType = "application/json; charset=utf-8";
        try
        {
            using var writer = new Utf8JsonWriter(http.Response.BodyWriter);
            writer.WriteStartObject();
            writer.WriteString("versionContrato", "1.0"); writer.WriteNumber("generacionId", header.GenerationId);
            writer.WriteString("generadaEn", header.Generated); writer.WriteString("consultadaEn", now);
            writer.WriteString("estadoOperativoConsultadoEn", now);
            writer.WritePropertyName("seleccion");
            JsonSerializer.Serialize(writer, new { articulo = filter.Article, listado = filter.Listing, incluirHistorial = filter.IncludeHistory }, Json);
            writer.WritePropertyName("fuentes"); writer.WriteRawValue(header.SourcesJson);
            writer.WritePropertyName("cobertura"); writer.WriteRawValue(header.CoverageJson);
            writer.WritePropertyName("estadoOperativo"); operational.WriteTo(writer);
            writer.WriteStartArray("data");
            long found = 0, appearances = 0;
            await foreach (var item in Consolidation.ReadAsync(queries.ReadAsync(header.GenerationId, filter, rfcs, ct), rfcs, ct))
            {
                if (item.Encontrado) found++;
                appearances += item.Apariciones.Count;
                writer.WriteStartObject(); writer.WriteString("rfc", item.Rfc); writer.WriteBoolean("encontrado", item.Encontrado);
                writer.WritePropertyName("nombresPublicados"); JsonSerializer.Serialize(writer, item.NombresPublicados, Json);
                writer.WritePropertyName("resumen"); JsonSerializer.Serialize(writer, item.Resumen, Json);
                writer.WriteStartArray("apariciones");
                foreach (var row in item.Apariciones)
                {
                    writer.WriteStartObject();
                    writer.WriteString("aparicionId", row.Id.ToString()); writer.WriteNumber("versionId", row.VersionId);
                    writer.WriteString("fuenteId", row.Origin == "DOF" ? "dof:" + row.Article + ":publicaciones-dof" : row.SourceId);
                    writer.WriteString("origen", row.Origin); writer.WriteString("articulo", row.Article); writer.WriteString("listado", row.Listing);
                    writer.WriteString("nombrePublicado", row.Name); writer.WriteString("estadoOriginal", row.Status);
                    writer.WriteString("estadoNormalizado", NormalizeStatus(row.Status));
                    if (row.PublicationDate is { } publicationDate) writer.WriteString("fechaPublicacion", publicationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    else writer.WriteNull("fechaPublicacion");
                    if (row.Origin == "SAT") writer.WriteBoolean("presenteUltimoListadoSat", !row.Historical);
                    else writer.WriteNull("presenteUltimoListadoSat");
                    writer.WriteBoolean("esHistorica", row.Historical);
                    if (row.NoteCode is { } noteCode) writer.WriteNumber("codNota", noteCode); else writer.WriteNull("codNota");
                    writer.WritePropertyName("evidencia"); JsonSerializer.Serialize(writer, new { url = row.Url, tabla = row.Table, fila = row.Row }, Json);
                    writer.WritePropertyName("detalle"); writer.WriteRawValue(row.DetailJson);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
                if (writer.BytesPending >= 32768) { writer.Flush(); await http.Response.BodyWriter.FlushAsync(ct); }
            }
            writer.WriteEndArray();
            // Compute totals while writing. This avoids dbo.Conteos scanning the same
            // generation a second time solely to obtain COUNT(DISTINCT Rfc).
            writer.WritePropertyName("totales");
            JsonSerializer.Serialize(writer, new { rfcs = rfcs?.LongLength ?? found, encontrados = found,
                noEncontrados = rfcs is null ? 0 : rfcs.LongLength - found, apariciones = appearances }, Json);
            writer.WriteEndObject(); writer.Flush();
            await http.Response.BodyWriter.FlushAsync(ct);
        }
        catch
        {
            if (http.Response.HasStarted) http.Abort();
            throw;
        }
    }
    private static string? NormalizeStatus(string status) => TextKey.Normalize(status) switch
    {
        "PRESUNTO" or "PRESUNTOS" => "presunto", "DEFINITIVO" or "DEFINITIVOS" => "definitivo",
        "DESVIRTUADO" or "DESVIRTUADOS" => "desvirtuado", "SENTENCIAFAVORABLE" or "SENTENCIASFAVORABLES" => "sentencia-favorable",
        "NOLOCALIZADO" or "NOLOCALIZADOS" => "no-localizado", _ => null
    };
}
