using System.Data;
using System.Globalization;
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace MonitorRFC.Infrastructure.Persistence;

public sealed record LegacyAll69Request(int Page, int PageSize, int? TipoId, string? Articulo, string? Listado, string? Situacion);

/// <summary>
/// Consultas de compatibilidad con el proyecto legado (RfcStatusApi). Usa exclusivamente objetos nuevos
/// (dbo.PA_Legacy_* y dbo.vw_Legacy*), independientes de los procedimientos y vistas de la API v1.
/// </summary>
public sealed class LegacyRepository(string connectionString, ILogger<LegacyRepository>? logger = null)
{
    private static readonly string[] Meses = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    /// <summary>Metadatos de una fuente: ultimaPublicacion, informacionActualizadaAl, encabezado, ultimaConsulta.</summary>
    public sealed record InfoFuente(string? UltimaPublicacion, string? InformacionActualizadaAl, string? Encabezado, string? UltimaConsulta);
    public sealed record InfoConsulta(InfoFuente Base, InfoFuente NoLocalizados, InfoFuente Bis);
    public sealed record EdosEfosRow(string Rfc, string? NombreContribuyente, string? Estatus, string? NumeroFechaOficioSAT,
        string? PublicacionPaginaSAT, string? NumeroFechaOficioGlobal, string? PublicacionDOF, string? Articulo);

    private static readonly InfoFuente Vacia = new(null, null, null, null);

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqlConnection(connectionString);
        try { await c.OpenAsync(ct); return c; }
        catch { await c.DisposeAsync(); throw; }
    }

    // Formato datetime2(7): "2026-11-13 12:56:34.8600000" y "2026-09-30 00:00:00.0000000".
    private static string? Dt7(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    // Formato "2026-11-13T13:09:11.737": milisegundos sin ceros finales, como datetime2(3).
    private static string? Iso(DateTime? d)
    {
        if (d is null) return null;
        var v = d.Value;
        var ms = v.Millisecond == 0 ? "" : "." + v.Millisecond.ToString("D3", CultureInfo.InvariantCulture).TrimEnd('0');
        return v.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + ms;
    }

    private static string? Encabezado(DateTime? d, bool punto) =>
        d is null ? null : $"Información actualizada al {d.Value.Day:00} de {Meses[d.Value.Month - 1]} de {d.Value.Year}" + (punto ? "." : "");

    private static InfoFuente Fuente(DateTime? portal, DateTime? descarga, DateTime? intento, bool punto) =>
        new(Dt7(descarga), Dt7(portal?.Date), Encabezado(portal, punto), Iso(intento));

    private static DateTime? Fecha(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);
    private static string? Texto(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // Primer resultado de los tres procedimientos: 9 columnas (69-b, no localizados y 69-b-bis).
    private static async Task<InfoConsulta> LeerInfoAsync(SqlDataReader r, CancellationToken ct)
    {
        if (!await r.ReadAsync(ct)) return new(Vacia, Vacia, Vacia);
        var b = Fuente(Fecha(r, 0), Fecha(r, 1), Fecha(r, 2), true);
        var nl = Fuente(Fecha(r, 3), Fecha(r, 4), Fecha(r, 5), false);
        var bis = Fuente(Fecha(r, 6), Fecha(r, 7), Fecha(r, 8), true);
        return new(b, nl, bis);
    }

    /// <summary>POST /api/Rfc/Edos-Efos: un registro por RFC, con los artículos concatenados.</summary>
    public async Task<(InfoConsulta Info, List<EdosEfosRow> Data)> ConsultarEdosEfosAsync(IReadOnlyList<string> rfcs, CancellationToken ct)
    {
        var table = new DataTable();
        table.Columns.Add("Rfc", typeof(string));
        table.Columns.Add("OrdenEntrada", typeof(int));
        for (var i = 0; i < rfcs.Count; i++) table.Rows.Add(rfcs[i], i);

        await using var c = await OpenAsync(ct);
        using var cmd = new SqlCommand("dbo.PA_Legacy_EdosEfos_Consultar", c) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
        var p = cmd.Parameters.Add("@Rfcs", SqlDbType.Structured);
        p.TypeName = "dbo.RfcInput";
        p.Value = table;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        var info = await LeerInfoAsync(r, ct);
        var data = new List<EdosEfosRow>();
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            data.Add(new EdosEfosRow(r.GetString(0), Texto(r, 1), Texto(r, 2), Texto(r, 3), Texto(r, 4), Texto(r, 5), Texto(r, 6), Texto(r, 7)));
        return (info, data);
    }

    /// <summary>GET /api/EdosEfos/all: respuesta heredada completa, en streaming.</summary>
    public async Task WriteAllAsync(PipeWriter output, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = new SqlCommand("dbo.PA_Legacy_EdosEfos_All", c) { CommandType = CommandType.StoredProcedure, CommandTimeout = 900 };
        await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var info = await LeerInfoAsync(r, ct);
        await r.NextResultAsync(ct);
        long total = 0;
        if (await r.ReadAsync(ct)) total = r.GetInt64(0);
        await r.NextResultAsync(ct);

        using var w = new Utf8JsonWriter(output, new JsonWriterOptions { SkipValidation = true });
        w.WriteStartObject();
        w.WriteBoolean("success", total > 0);
        w.WriteString("message", total > 0 ? $"Se obtuvieron {total} registros." : "No se encontraron resultados.");
        w.WriteStartObject("resultado");
        w.WriteStartObject("informacionConsulta");
        WriteFuente("", info.Base);
        w.WriteEndObject();
        w.WriteStartArray("data");
        var escritos = 0L;
        while (await r.ReadAsync(ct))
        {
            WriteRow(w, r, false);
            if (++escritos % 2000 == 0) { w.Flush(); await output.FlushAsync(ct); }
        }
        w.WriteEndArray();
        w.WriteStartObject("paginacion");
        w.WriteNumber("pagina", 1); w.WriteNumber("tamanoPagina", escritos);
        w.WriteNumber("totalRegistros", total); w.WriteNumber("totalPaginas", total > 0 ? 1 : 0);
        w.WriteEndObject();
        w.WriteEndObject(); w.WriteEndObject(); w.Flush();
        await output.FlushAsync(ct);
        logger?.LogInformation("[Legacy] all: {Total} registros enviados.", escritos);

        void WriteFuente(string sufijo, InfoFuente f)
        {
            WriteNullable("ultimaPublicacion" + sufijo, f.UltimaPublicacion);
            WriteNullable("informacionActualizadaAl" + sufijo, f.InformacionActualizadaAl);
            WriteNullable("encabezado" + sufijo, f.Encabezado);
            WriteNullable("ultimaConsulta" + sufijo, f.UltimaConsulta);
        }
        void WriteNullable(string name, string? value)
        {
            if (value is null) w.WriteNull(name); else w.WriteString(name, value);
        }
        void WriteRow(Utf8JsonWriter writer, SqlDataReader reader, bool ignored)
        {
            string[] etapas =
            [
                "folioPresuncionSAT", "publicacionSATPresuncion", "folioPresuncionDOF", "publicacionDOFPresuncion",
                "folioDesvirtuadosSAT", "publicacionSATDesvirtuados", "folioDesvirtuadosDOF", "publicacionDOFDesvirtuados",
                "folioDefinitivosSAT", "publicacionSATDefinitivos", "folioDefinitivosDOF", "publicacionDOFDefinitivos",
                "folioFavorablesSAT", "publicacionSATFavorables", "folioFavorablesDOF", "publicacionDOFFavorables"
            ];
            writer.WriteStartObject();
            writer.WriteString("RFC", reader.GetString(0));
            if (Texto(reader, 1) is { } nombre) writer.WriteString("nombreC", nombre); else writer.WriteNull("nombreC");
            if (Texto(reader, 2) is { } situacion) writer.WriteString("situacionC", situacion); else writer.WriteNull("situacionC");
            for (var i = 0; i < etapas.Length; i++)
                if (Texto(reader, 3 + i) is { } valor) writer.WriteString(etapas[i], valor); else writer.WriteNull(etapas[i]);
            writer.WriteEndObject();
        }
    }

    /// <summary>GET /api/EdosEfos/all69: una página de todos los artículos, opcionalmente filtrada por tipo.</summary>
    public async Task WriteAll69Async(PipeWriter output, LegacyAll69Request request, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = new SqlCommand("dbo.PA_Legacy_EdosEfos_All69", c)
        { CommandType = CommandType.StoredProcedure, CommandTimeout = 900 };
        cmd.Parameters.Add("@Pagina", SqlDbType.Int).Value = request.Page;
        cmd.Parameters.Add("@TamanoPagina", SqlDbType.Int).Value = request.PageSize;
        cmd.Parameters.Add("@Articulo", SqlDbType.VarChar, 16).Value = (object?)request.Articulo ?? DBNull.Value;
        cmd.Parameters.Add("@Listado", SqlDbType.VarChar, 100).Value = (object?)request.Listado ?? DBNull.Value;
        cmd.Parameters.Add("@Situacion", SqlDbType.NVarChar, 200).Value = (object?)request.Situacion ?? DBNull.Value;
        await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);

        var info = await LeerInfoAsync(r, ct);               // 1) Metadatos
        await r.NextResultAsync(ct);                          // 2) Total del universo filtrado
        long total = 0;
        if (await r.ReadAsync(ct)) total = r.GetInt64(0);
        await r.NextResultAsync(ct);                          // 3) Datos de la página

        using var w = new Utf8JsonWriter(output, new JsonWriterOptions { SkipValidation = true });
        w.WriteStartObject();
        w.WriteBoolean("success", total > 0);
        w.WriteString("message", total > 0 ? "Resultados obtenidos correctamente." : "No se encontraron resultados.");
        w.WriteStartObject("resultado");

        w.WriteStartObject("informacionConsulta69");
        WriteFuente("", info.Base);
        WriteFuente("NL", info.NoLocalizados);
        WriteFuente("Bis", info.Bis);
        w.WriteEndObject();

        w.WriteStartObject("filtro");
        if (request.TipoId.HasValue) w.WriteNumber("tipoId", request.TipoId.Value); else w.WriteNull("tipoId");
        if (request.Articulo is not null) w.WriteString("articulo", request.Articulo); else w.WriteNull("articulo");
        if (request.Listado is not null) w.WriteString("listado", request.Listado); else w.WriteNull("listado");
        if (request.Situacion is not null) w.WriteString("situacion", request.Situacion); else w.WriteNull("situacion");
        w.WriteEndObject();

        string[] etapas =
        [
            "folioPresuncionSAT", "publicacionSATPresuncion", "folioPresuncionDOF", "publicacionDOFPresuncion",
            "folioDesvirtuadosSAT", "publicacionSATDesvirtuados", "folioDesvirtuadosDOF", "publicacionDOFDesvirtuados",
            "folioDefinitivosSAT", "publicacionSATDefinitivos", "folioDefinitivosDOF", "publicacionDOFDefinitivos",
            "folioFavorablesSAT", "publicacionSATFavorables", "folioFavorablesDOF", "publicacionDOFFavorables"
        ];

        w.WriteStartArray("data");
        var escritos = 0;
        while (await r.ReadAsync(ct))
        {
            w.WriteStartObject();
            w.WriteString("RFC", r.GetString(0));
            WriteNullable("nombreC", Texto(r, 1));
            WriteNullable("situacionC", Texto(r, 2));
            for (var i = 0; i < etapas.Length; i++) WriteNullable(etapas[i], Texto(r, 3 + i));
            WriteNullable("fechaPrimeraPublicacion_nl", Texto(r, 19));
            WriteNullable("entidadFederativa_nl", Texto(r, 20));
            w.WriteBoolean("EnListado69NL", !r.IsDBNull(21) && r.GetBoolean(21));
            WriteNullable("articulo", Texto(r, 22));
            w.WriteEndObject();
            if (++escritos % 2000 == 0)
            {
                w.Flush();
                await output.FlushAsync(ct);
            }
        }
        w.WriteEndArray();

        w.WriteStartObject("paginacion");
        var inicio = total == 0 ? 0 : ((long)request.Page - 1) * request.PageSize + 1;
        var final = inicio == 0 ? 0 : inicio + escritos - 1;
        var totalPaginas = total == 0 ? 0 : (total + request.PageSize - 1) / request.PageSize;
        w.WriteNumber("pagina", request.Page);
        w.WriteNumber("registroInicial", inicio);
        w.WriteNumber("registroFinal", final);
        w.WriteNumber("totalRegistros", total);
        w.WriteNumber("totalPaginas", totalPaginas);
        if (request.Page < totalPaginas) w.WriteNumber("siguientePagina", request.Page + 1); else w.WriteNull("siguientePagina");
        w.WriteEndObject();

        w.WriteEndObject(); // resultado
        w.WriteEndObject();
        w.Flush();
        await output.FlushAsync(ct);
        logger?.LogInformation("[Legacy] all69: página {Page}, registros {Start}-{End} de {Total}.",
            request.Page, inicio, final, total);

        void WriteNullable(string name, string? value)
        {
            if (value is null) w.WriteNull(name); else w.WriteString(name, value);
        }
        void WriteFuente(string sufijo, InfoFuente f)
        {
            WriteNullable("ultimaPublicacion" + sufijo, f.UltimaPublicacion);
            WriteNullable("informacionActualizadaAl" + sufijo, f.InformacionActualizadaAl);
            WriteNullable("encabezado" + sufijo, f.Encabezado);
            WriteNullable("ultimaConsulta" + sufijo, f.UltimaConsulta);
        }
    }
}
