using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MonitorRFC.Application;
using MonitorRFC.Domain;
using MonitorRFC.Infrastructure.Persistence;
using MonitorRFC.Infrastructure.Sources;

namespace MonitorRFC.Controllers;

[ApiController]
public sealed class RfcsController(IRfcQueryService queries, SqlRepository repository, SafeDownloader downloader, IClock clock) : ControllerBase
{
    [HttpGet("api/v1/articulos")]
    public IActionResult Articles() => Ok(SourceCatalog.Articles);

    [HttpGet("health/live")]
    public IActionResult Live() => Ok(new { estado = "online" });

    [HttpGet("health/ready")]
    public async Task<IActionResult> Ready(CancellationToken ct) => await queries.GetHeaderAsync(ct) is null
        ? Problem(statusCode: 503, title: "Sin generación publicada.") : Ok(new { estado = "ready" });

    // ------------------------------------------------------------------------
    // 1. Catálogo de Tipos de Artículos / Listados (1..N) y Consulta por Tipo
    // ------------------------------------------------------------------------

    [HttpGet("api/v1/tipos")]
    public IActionResult GetTypes() => Ok(CatalogoTipos.Todos);

    [HttpGet("api/v1/tipos/{tipoId:int}")]
    public IActionResult GetTypeById(int tipoId) =>
        CatalogoTipos.ObtenerPorId(tipoId) is { } tipo ? Ok(tipo) : NotFound(new { error = $"Tipo {tipoId} no encontrado." });

    [HttpGet("api/v1/tipos/{tipoId:int}/contribuyentes")]
    public Task<IActionResult> ByType(int tipoId, bool incluirHistorial = false, CancellationToken ct = default)
    {
        var tipo = CatalogoTipos.ObtenerPorId(tipoId);
        if (tipo is null) return Task.FromResult<IActionResult>(NotFound(new { error = $"Tipo {tipoId} no encontrado." }));
        return Query(null, tipo.Articulo, tipo.Listado, incluirHistorial, ct);
    }

    // ------------------------------------------------------------------------
    // 2. Listado Global de Contribuyentes con Columnas Relacionales Completas
    // ------------------------------------------------------------------------

    [HttpGet("api/v1/contribuyentes/global")]
    [HttpGet("api/v1/rfcs/global")]
    public IActionResult Global(
        [FromQuery] int? tipoId = null,
        [FromQuery] string? articulo = null,
        [FromQuery] string? listado = null,
        [FromQuery] string? situacion = null)
    {
        if (tipoId.HasValue)
        {
            var tipo = CatalogoTipos.ObtenerPorId(tipoId.Value);
            if (tipo is null) return NotFound(new { error = $"Tipo {tipoId.Value} no encontrado." });
            articulo = tipo.Articulo;
            listado = tipo.Listado;
            if (tipo.SituacionFiltro is not null) situacion = tipo.SituacionFiltro;
        }
        return new GlobalContribuyenteStreamResult(repository, articulo, listado, situacion, clock.Now);
    }

    // ------------------------------------------------------------------------
    // 3. Consulta de Estado Vigente para Arreglo de RFCs
    // ------------------------------------------------------------------------

    [HttpPost("api/v1/rfcs/estado")]
    public async Task<IActionResult> StatusByRfcs(
        [FromBody] JsonElement body,
        [FromQuery] string? articulo = null,
        [FromQuery] string? listado = null,
        CancellationToken ct = default)
    {
        string[] rfcs;
        try { rfcs = ExtractRfcs(body); }
        catch (ArgumentException ex) { return Problem(statusCode: 400, title: ex.Message); }

        var result = await repository.ConsultarEstadoRfcsAsync(rfcs, articulo, listado, ct);
        return Ok(result);
    }

    // ------------------------------------------------------------------------
    // 4. Historial de Movimientos / Progresión para Arreglo de RFCs
    // ------------------------------------------------------------------------

    [HttpPost("api/v1/rfcs/historial")]
    public async Task<IActionResult> HistoryByRfcs(
        [FromBody] JsonElement body,
        [FromQuery] string? articulo = null,
        [FromQuery] string? listado = null,
        CancellationToken ct = default)
    {
        string[] rfcs;
        try { rfcs = ExtractRfcs(body); }
        catch (ArgumentException ex) { return Problem(statusCode: 400, title: ex.Message); }

        var result = await repository.ConsultarHistorialRfcsAsync(rfcs, articulo, listado, ct);
        return Ok(result);
    }

    // ------------------------------------------------------------------------
    // 5. Estado Operativo de Fuentes SAT, Portal y DOF
    // ------------------------------------------------------------------------

    [HttpGet("api/v1/fuentes/estado")]
    [HttpGet("api/v1/descargas/estado")]
    public async Task<IActionResult> Status(CancellationToken ct) => Ok(await repository.GetOperationalStateAsync(ct));

    // ------------------------------------------------------------------------
    // 6. Sincronización Manual On-Demand del DOF
    // ------------------------------------------------------------------------

    public sealed record DofSyncInput(string? FechaInicio, string? FechaFin, string? Articulo = null);

    [HttpPost("api/v1/dof/sincronizar")]
    [HttpPost("api/v1/dof/descargar")]
    public async Task<IActionResult> SyncDof(
        [FromBody] DofSyncInput? body,
        [FromQuery] string? fechaInicio = null,
        [FromQuery] string? fechaFin = null,
        [FromQuery] string? articulo = null,
        CancellationToken ct = default)
    {
        var inicioStr = body?.FechaInicio ?? fechaInicio;
        var finStr = body?.FechaFin ?? fechaFin;
        var artStr = body?.Articulo ?? articulo;

        if (string.IsNullOrWhiteSpace(inicioStr) || string.IsNullOrWhiteSpace(finStr))
            return Problem(statusCode: 400, title: "Se requieren 'fechaInicio' y 'fechaFin' en formato yyyy-MM-dd.");

        if (!DateOnly.TryParseExact(inicioStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var inicioDate))
            return Problem(statusCode: 400, title: "Formato de 'fechaInicio' inválido. Debe ser yyyy-MM-dd.");

        if (!DateOnly.TryParseExact(finStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var finDate))
            return Problem(statusCode: 400, title: "Formato de 'fechaFin' inválido. Debe ser yyyy-MM-dd.");

        if (inicioDate > finDate)
            return Problem(statusCode: 400, title: "'fechaInicio' no puede ser posterior a 'fechaFin'.");

        if (!string.IsNullOrWhiteSpace(artStr) && !SourceCatalog.Articles.Contains(artStr))
            return Problem(statusCode: 400, title: $"Artículo '{artStr}' inválido o no reconocido.");

        var options = new Contracts.AgentOptions();
        try { Directory.CreateDirectory(options.DataDirectory); }
        catch { options.DataDirectory = Path.Combine(Path.GetTempPath(), "MonitorRFC"); Directory.CreateDirectory(options.DataDirectory); }

        var response = await repository.SincronizarDofPeriodoAsync(inicioDate, finDate, artStr, downloader, options, ct);
        return Ok(response);
    }

    // ------------------------------------------------------------------------
    // Endpoints Heredados y de Compatibilidad Retroactiva
    // ------------------------------------------------------------------------

    [HttpGet("api/v1/rfcs")]
    [HttpGet("api/EdosEfos/all69")]
    public Task<IActionResult> All(bool incluirHistorial = false, CancellationToken ct = default) => Query(null, null, null, incluirHistorial, ct);

    [HttpPost("api/v1/rfcs/consultar")]
    public Task<IActionResult> Selected(
        [FromBody] JsonElement body,
        string? articulo = null,
        string? listado = null,
        bool incluirHistorial = false,
        CancellationToken ct = default)
    {
        string[] rfcs;
        try { rfcs = ExtractRfcs(body); }
        catch (ArgumentException ex) { return Task.FromResult<IActionResult>(Problem(statusCode: 400, title: ex.Message)); }
        return Query(rfcs, articulo, listado, incluirHistorial, ct, true);
    }

    [HttpPost("api/Rfc/Listado69")]
    public Task<IActionResult> Legacy(
        [FromBody] JsonElement body,
        bool incluirHistorial = false,
        CancellationToken ct = default)
    {
        string[] rfcs;
        try { rfcs = ExtractRfcs(body); }
        catch (ArgumentException ex) { return Task.FromResult<IActionResult>(Problem(statusCode: 400, title: ex.Message)); }
        return Query(rfcs, null, null, incluirHistorial, ct, true);
    }

    [HttpGet("api/v1/articulos/{articulo}/contribuyentes")]
    [HttpGet("api/v1/articulos/{articulo}/listados/{listado}/contribuyentes")]
    public Task<IActionResult> ByArticle(string articulo, string? listado = null, bool incluirHistorial = false, CancellationToken ct = default) =>
        Query(null, articulo, listado, incluirHistorial, ct);

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

    private static string[] ExtractRfcs(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }
            if (list.Count == 0) throw new ArgumentException("El arreglo de RFC no puede estar vacío.");
            return list.ToArray();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propName in new[] { "rfcs", "Rfcs", "rfc", "Rfc" })
            {
                if (element.TryGetProperty(propName, out var arrayProp) && arrayProp.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var item in arrayProp.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                            list.Add(s);
                    }
                    if (list.Count == 0) throw new ArgumentException("El arreglo de RFC no puede estar vacío.");
                    return list.ToArray();
                }
            }
        }

        throw new ArgumentException("El cuerpo debe ser un arreglo de RFCs [\"RFC1\", ...] o un objeto con propiedad 'rfcs': [\"RFC1\", ...].");
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

public sealed class GlobalContribuyenteStreamResult(
    SqlRepository repository, string? articulo, string? listado, string? situacion, DateTimeOffset now) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        http.Response.ContentType = "application/json; charset=utf-8";
        try
        {
            using var writer = new Utf8JsonWriter(http.Response.BodyWriter);
            writer.WriteStartObject();
            writer.WriteString("versionContrato", "2.0");
            writer.WriteString("consultadaEn", now);
            writer.WritePropertyName("filtro");
            writer.WriteStartObject();
            if (articulo is not null) writer.WriteString("articulo", articulo); else writer.WriteNull("articulo");
            if (listado is not null) writer.WriteString("listado", listado); else writer.WriteNull("listado");
            if (situacion is not null) writer.WriteString("situacion", situacion); else writer.WriteNull("situacion");
            writer.WriteEndObject();

            writer.WriteStartArray("data");
            long total = 0;
            await foreach (var c in repository.ConsultarGlobalVigentesAsync(articulo, listado, situacion, ct))
            {
                total++;
                writer.WriteStartObject();
                writer.WriteNumber("contribuyenteId", c.ContribuyenteId);
                writer.WriteNumber("tipoId", c.TipoId);
                writer.WriteString("rfc", c.Rfc);
                writer.WriteString("razonSocial", c.RazonSocial);
                writer.WriteString("articulo", c.Articulo);
                writer.WriteString("listado", c.Listado);
                writer.WriteString("situacion", c.Situacion);
                if (c.FechaEfectiva.HasValue) writer.WriteString("fechaEfectiva", c.FechaEfectiva.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                else writer.WriteNull("fechaEfectiva");
                writer.WriteString("origenUltimaModificacion", c.OrigenUltimaModificacion);
                writer.WriteString("fechaModificacion", c.FechaModificacion.ToString("s", CultureInfo.InvariantCulture));
                if (c.TipoPersona is not null) writer.WriteString("tipoPersona", c.TipoPersona); else writer.WriteNull("tipoPersona");
                if (c.EntidadFederativa is not null) writer.WriteString("entidadFederativa", c.EntidadFederativa); else writer.WriteNull("entidadFederativa");
                if (c.Monto.HasValue) writer.WriteNumber("monto", c.Monto.Value); else writer.WriteNull("monto");
                if (c.Ejercicio is not null) writer.WriteString("ejercicio", c.Ejercicio); else writer.WriteNull("ejercicio");
                if (c.Periodo is not null) writer.WriteString("periodo", c.Periodo); else writer.WriteNull("periodo");
                if (c.Motivo is not null) writer.WriteString("motivo", c.Motivo); else writer.WriteNull("motivo");
                if (c.FechaCancelacion.HasValue) writer.WriteString("fechaCancelacion", c.FechaCancelacion.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                else writer.WriteNull("fechaCancelacion");
                if (c.FechaAutorizacion.HasValue) writer.WriteString("fechaAutorizacion", c.FechaAutorizacion.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                else writer.WriteNull("fechaAutorizacion");
                if (c.Administracion is not null) writer.WriteString("administracion", c.Administracion); else writer.WriteNull("administracion");
                if (c.NumeroOficioSAT is not null) writer.WriteString("numeroOficioSAT", c.NumeroOficioSAT); else writer.WriteNull("numeroOficioSAT");
                if (c.FechaOficioSAT.HasValue) writer.WriteString("fechaOficioSAT", c.FechaOficioSAT.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                else writer.WriteNull("fechaOficioSAT");
                if (c.NumeroOficioDOF is not null) writer.WriteString("numeroOficioDOF", c.NumeroOficioDOF); else writer.WriteNull("numeroOficioDOF");
                if (c.FechaOficioDOF.HasValue) writer.WriteString("fechaOficioDOF", c.FechaOficioDOF.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                else writer.WriteNull("fechaOficioDOF");

                if (c.PresuncionOficioSAT is not null) writer.WriteString("presuncionOficioSAT", c.PresuncionOficioSAT); else writer.WriteNull("presuncionOficioSAT");
                if (c.PresuncionFechaSAT.HasValue) writer.WriteString("presuncionFechaSAT", c.PresuncionFechaSAT.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                else writer.WriteNull("presuncionFechaSAT");
                if (c.PresuncionOficioDOF is not null) writer.WriteString("presuncionOficioDOF", c.PresuncionOficioDOF); else writer.WriteNull("presuncionOficioDOF");
                if (c.PresuncionFechaDOF.HasValue) writer.WriteString("presuncionFechaDOF", c.PresuncionFechaDOF.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                else writer.WriteNull("presuncionFechaDOF");

                if (c.DesvirtuadoOficioSAT is not null) writer.WriteString("desvirtuadoOficioSAT", c.DesvirtuadoOficioSAT); else writer.WriteNull("desvirtuadoOficioSAT");
                if (c.DesvirtuadoFechaSAT.HasValue) writer.WriteString("desvirtuadoFechaSAT", c.DesvirtuadoFechaSAT.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                else writer.WriteNull("desvirtuadoFechaSAT");
                if (c.DesvirtuadoOficioDOF is not null) writer.WriteString("desvirtuadoOficioDOF", c.DesvirtuadoOficioDOF); else writer.WriteNull("desvirtuadoOficioDOF");
                if (c.DesvirtuadoFechaDOF.HasValue) writer.WriteString("desvirtuadoFechaDOF", c.DesvirtuadoFechaDOF.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                else writer.WriteNull("desvirtuadoFechaDOF");

                if (c.DefinitivoOficioSAT is not null) writer.WriteString("definitivoOficioSAT", c.DefinitivoOficioSAT); else writer.WriteNull("definitivoOficioSAT");
                if (c.DefinitivoFechaSAT.HasValue) writer.WriteString("definitivoFechaSAT", c.DefinitivoFechaSAT.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                else writer.WriteNull("definitivoFechaSAT");
                if (c.DefinitivoOficioDOF is not null) writer.WriteString("definitivoOficioDOF", c.DefinitivoOficioDOF); else writer.WriteNull("definitivoOficioDOF");
                if (c.DefinitivoFechaDOF.HasValue) writer.WriteString("definitivoFechaDOF", c.DefinitivoFechaDOF.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                else writer.WriteNull("definitivoFechaDOF");

                if (c.SentenciaOficioSAT is not null) writer.WriteString("sentenciaOficioSAT", c.SentenciaOficioSAT); else writer.WriteNull("sentenciaOficioSAT");
                if (c.SentenciaFechaSAT.HasValue) writer.WriteString("sentenciaFechaSAT", c.SentenciaFechaSAT.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                else writer.WriteNull("sentenciaFechaSAT");
                if (c.SentenciaOficioDOF is not null) writer.WriteString("sentenciaOficioDOF", c.SentenciaOficioDOF); else writer.WriteNull("sentenciaOficioDOF");
                if (c.SentenciaFechaDOF.HasValue) writer.WriteString("sentenciaFechaDOF", c.SentenciaFechaDOF.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                else writer.WriteNull("sentenciaFechaDOF");

                if (c.CodNotaDOF.HasValue) writer.WriteNumber("codNotaDOF", c.CodNotaDOF.Value); else writer.WriteNull("codNotaDOF");
                if (c.UrlOrigenDOF is not null) writer.WriteString("urlOrigenDOF", c.UrlOrigenDOF); else writer.WriteNull("urlOrigenDOF");
                if (c.UltimaDescarga.HasValue) writer.WriteString("ultimaDescarga", c.UltimaDescarga.Value.ToString("s", CultureInfo.InvariantCulture));
                else writer.WriteNull("ultimaDescarga");

                writer.WriteEndObject();

                if (writer.BytesPending >= 32768)
                {
                    writer.Flush();
                    await http.Response.BodyWriter.FlushAsync(ct);
                }
            }
            writer.WriteEndArray();
            writer.WriteNumber("totalRegistros", total);
            writer.WriteEndObject();
            writer.Flush();
            await http.Response.BodyWriter.FlushAsync(ct);
        }
        catch
        {
            if (http.Response.HasStarted) http.Abort();
            throw;
        }
    }
}
