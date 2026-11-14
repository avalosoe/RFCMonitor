using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MonitorRFC.Domain;
using MonitorRFC.Infrastructure.Persistence;

namespace MonitorRFC.Controllers;

/// <summary>
/// Endpoints de compatibilidad con el proyecto legado (RfcStatusApi). Respuestas con la forma exacta del legado,
/// respaldadas por objetos SQL nuevos (dbo.PA_Legacy_* / dbo.vw_Legacy*).
/// </summary>
[ApiController]
public sealed class LegacyController(LegacyRepository repository, ILogger<LegacyController> logger) : ControllerBase
{
    private const int MaxRfcs = 10_000;
    private const int DefaultPageSize = 10_000;
    private const int MaxPageSize = 10_000;

    /// <summary>Consulta por RFC (69-B, 69, 49 Bis…): un solo registro por RFC con los artículos concatenados.</summary>
    [HttpPost("api/Rfc/Edos-Efos")]
    public async Task<IActionResult> EdosEfos([FromBody] JsonElement body, CancellationToken ct)
    {
        var rfcs = ExtractRfcs(body);
        if (rfcs.Count == 0)
            return BadRequest(new { success = false, message = "Debe proporcionar al menos un RFC.", data = Array.Empty<object>() });
        if (rfcs.Count > MaxRfcs)
            return BadRequest(new { success = false, message = $"Máximo {MaxRfcs} RFC por consulta.", data = Array.Empty<object>() });

        var (info, data) = await repository.ConsultarEdosEfosAsync(rfcs, ct);
        var resultado = new
        {
            informacionConsulta = new
            {
                ultimaPublicacion = info.Base.UltimaPublicacion,
                informacionActualizadaAl = info.Base.InformacionActualizadaAl,
                encabezado = info.Base.Encabezado,
                ultimaConsulta = info.Base.UltimaConsulta
            },
            data = data.Select(d => new
            {
                rfc = d.Rfc,
                nombreContribuyente = d.NombreContribuyente,
                estatus = d.Estatus,
                numeroFechaOficioSAT = d.NumeroFechaOficioSAT,
                publicacionPaginaSAT = d.PublicacionPaginaSAT,
                numeroFechaOficioGlobal = d.NumeroFechaOficioGlobal,
                publicacionDOF = d.PublicacionDOF,
                articulo = d.Articulo
            })
        };
        return data.Count == 0
            ? Ok(new { success = false, message = "No se encontraron resultados.", resultado })
            : Ok(new { success = true, message = "Resultados obtenidos correctamente.", resultado });
    }

    /// <summary>Todo lo correspondiente al 69-B (y 69-B Bis), sin paginación.</summary>
    [HttpGet("api/EdosEfos/all")]
    public IActionResult All() => new LegacyAllResult(repository, logger);

    /// <summary>Todos los RFC de todos los artículos, paginados y opcionalmente filtrados por tipo del catálogo.</summary>
    [HttpGet("api/EdosEfos/all69")]
    public IActionResult All69([FromQuery] int pagina = 1, [FromQuery] int tamanoPagina = DefaultPageSize, [FromQuery] int? tipoId = null)
    {
        if (pagina < 1) return Problem(statusCode: 400, title: "'pagina' debe ser mayor o igual a 1.");
        if (tamanoPagina is < 1 or > MaxPageSize)
            return Problem(statusCode: 400, title: $"'tamanoPagina' debe estar entre 1 y {MaxPageSize:N0}.");

        var tipo = tipoId.HasValue ? CatalogoTipos.ObtenerPorId(tipoId.Value) : null;
        if (tipoId.HasValue && tipo is null)
            return Problem(statusCode: 404, title: $"Tipo fiscal {tipoId.Value} no encontrado. Consulte /api/v1/tipos.");

        return new LegacyAll69Result(repository, new LegacyAll69Request(
            pagina, tamanoPagina, tipoId, tipo?.Articulo, tipo?.Listado, tipo?.SituacionFiltro), logger);
    }

    private static List<string> ExtractRfcs(JsonElement body)
    {
        var items = new List<string>();
        JsonElement array = default;
        if (body.ValueKind == JsonValueKind.Array) array = body;
        else if (body.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "rfcs", "Rfcs", "rfc", "Rfc" })
                if (body.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array) { array = p; break; }
        if (array.ValueKind != JsonValueKind.Array) return items;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            // Igual que el legado: se eliminan guiones y espacios; el RFC se compara en mayúsculas.
            var rfc = (item.GetString() ?? "").Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
            if (rfc.Length > 0 && seen.Add(rfc)) items.Add(rfc);
        }
        return items;
    }
}

public sealed class LegacyAllResult(LegacyRepository repository, ILogger logger) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        http.Response.ContentType = "application/json; charset=utf-8";
        try
        {
            await repository.WriteAllAsync(http.Response.BodyWriter, http.RequestAborted);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fallo en el endpoint legado all.");
            if (!http.Response.HasStarted)
            {
                http.Response.StatusCode = 500;
                await http.Response.WriteAsync("{\"success\":false,\"message\":\"Error al ejecutar el procedimiento.\"}");
            }
            else http.Abort();
        }
    }
}

public sealed class LegacyAll69Result(LegacyRepository repository, LegacyAll69Request request, ILogger logger) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        http.Response.ContentType = "application/json; charset=utf-8";
        try
        {
            await repository.WriteAll69Async(http.Response.BodyWriter, request, http.RequestAborted);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fallo en el endpoint legado all69.");
            if (!http.Response.HasStarted)
            {
                http.Response.StatusCode = 500;
                await http.Response.WriteAsync("{\"success\":false,\"message\":\"Error al ejecutar el procedimiento.\"}");
            }
            else http.Abort();
        }
    }
}
