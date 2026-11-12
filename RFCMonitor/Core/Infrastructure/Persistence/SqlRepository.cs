using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MonitorRFC.Application;
using MonitorRFC.Contracts;
using MonitorRFC.Domain;
using MonitorRFC.Infrastructure.Configuration;

namespace MonitorRFC.Infrastructure.Persistence;

/// <summary>
/// Repositorio relacional de alto rendimiento para almacenamiento, delta e historial
/// de contribuyentes en SQL Server (RFCMonitor).
/// </summary>
public sealed class SqlRepository : IPublicationRepository, IRfcQueryService
{
    private readonly Func<string> connectionString;
    private readonly ILogger? logger;

    public SqlRepository(AgentConfiguration configuration, ILogger? logger = null)
    {
        this.connectionString = () => AgentConfiguration.GetConnection(configuration.Load());
        this.logger = logger;
    }

    public SqlRepository(string connectionString, ILogger? logger = null)
    {
        this.connectionString = () => connectionString;
        this.logger = logger;
    }

    private SqlConnection? leaseConnection;
    private Guid owner;
    private long token;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString());
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static SqlCommand Command(SqlConnection connection, string text, SqlTransaction? transaction = null) =>
        new(text, connection, transaction) { CommandTimeout = 600 };

    private static void P(SqlCommand cmd, string name, SqlDbType type, object? value, int size = 0)
    {
        var p = size == 0 ? cmd.Parameters.Add(name, type) : cmd.Parameters.Add(name, type, size);
        p.Value = value switch
        {
            null => DBNull.Value,
            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
            DateTimeOffset d => d.LocalDateTime,
            _ => value
        };
    }

    private SqlConnection Writer => leaseConnection is { State: ConnectionState.Open } c ? c :
        throw new InvalidOperationException($"La operación de ingesta perdió la conexión exclusiva (Estado: {leaseConnection?.State.ToString() ?? "Nula"}). Se requiere readquirir el bloqueo.");

    public async Task<IAsyncDisposable?> AcquireLeaseAsync(CancellationToken ct)
    {
        if (leaseConnection is not null) throw new InvalidOperationException("Bloqueo ya adquirido en esta instancia.");
        var c = await OpenAsync(ct);
        try
        {
            using var get = Command(c, "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='MonitorRFC.ingestion',@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0; SELECT @r;");
            if (Convert.ToInt32(await get.ExecuteScalarAsync(ct)) < 0) { await c.DisposeAsync(); return null; }
            owner = Guid.NewGuid();
            using var fence = Command(c, """
                SET XACT_ABORT ON; BEGIN TRANSACTION;
                UPDATE dbo.BloqueoServicio WITH(UPDLOCK,HOLDLOCK) 
                SET Propietario=@Owner, FencingToken=FencingToken+1, Expira=CONVERT(datetime2,'9999-12-31') 
                WHERE Recurso='ingestion';
                IF @@ROWCOUNT=0 
                    INSERT dbo.BloqueoServicio(Recurso,Propietario,FencingToken,Expira) 
                    VALUES('ingestion',@Owner,1,CONVERT(datetime2,'9999-12-31'));
                SELECT FencingToken FROM dbo.BloqueoServicio WHERE Recurso='ingestion'; 
                COMMIT;
                """);
            P(fence, "@Owner", SqlDbType.UniqueIdentifier, owner);
            token = Convert.ToInt64(await fence.ExecuteScalarAsync(ct));
            leaseConnection = c;
            return new SessionLease(this, c);
        }
        catch { await c.DisposeAsync(); throw; }
    }

    private sealed class SessionLease(SqlRepository repository, SqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (repository.leaseConnection != connection) return;
            repository.leaseConnection = null;
            try
            {
                if (connection.State == ConnectionState.Open)
                {
                    using var cmd = Command(connection, "UPDATE dbo.BloqueoServicio SET Expira=SYSDATETIME() WHERE Recurso='ingestion' AND Propietario=@Owner; EXEC sys.sp_releaseapplock @Resource='MonitorRFC.ingestion',@LockOwner='Session';");
                    P(cmd, "@Owner", SqlDbType.UniqueIdentifier, repository.owner);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            finally { await connection.DisposeAsync(); }
        }
    }

    private async Task AssertLeaseAsync(SqlTransaction transaction, CancellationToken ct)
    {
        using var cmd = Command(Writer, """
            IF APPLOCK_MODE('public','MonitorRFC.ingestion','Session')<>'Exclusive'
             OR NOT EXISTS(SELECT 1 FROM dbo.BloqueoServicio WITH(UPDLOCK,HOLDLOCK) WHERE Recurso='ingestion' AND Propietario=@Owner AND FencingToken=@Token)
             THROW 51000,'Bloqueo de ingesta perdido.',1;
            """, transaction);
        P(cmd, "@Owner", SqlDbType.UniqueIdentifier, owner);
        P(cmd, "@Token", SqlDbType.BigInt, token);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ImportResult?> CheckUnchangedByDateAsync(string sourceKey, string externalKey, DateOnly? sourceDate, string url, CancellationToken ct)
    {
        if (sourceDate is null) return null;
        var c = Writer;
        await using var transaction = (SqlTransaction)await c.BeginTransactionAsync(ct);
        await AssertLeaseAsync(transaction, ct);

        using var find = Command(c, """
            SELECT FechaPublicacionPortal, UltimoHashArchivo, TotalRegistrosVigentes
            FROM dbo.ControlFuenteSAT WITH(UPDLOCK,HOLDLOCK)
            WHERE FuenteClave = @Source;
            """, transaction);
        P(find, "@Source", SqlDbType.VarChar, sourceKey, 100);

        await using var r = await find.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            DateOnly? portalDate = r.IsDBNull(0) ? null : DateOnly.FromDateTime(r.GetDateTime(0));
            var totalVigentes = Convert.ToInt32(r.GetValue(2));
            await r.DisposeAsync();

            // Si la fecha registrada en BD es igual o más reciente que la del portal y ya tiene registros vigentes,
            // no es necesario descargar el archivo.
            if (portalDate is not null && portalDate.Value.Year > 1900 && sourceDate.Value <= portalDate.Value && totalVigentes > 0)
            {
                using var updateTime = Command(c, "UPDATE dbo.ControlFuenteSAT SET UltimoIntento=SYSDATETIME() WHERE FuenteClave=@Source;", transaction);
                P(updateTime, "@Source", SqlDbType.VarChar, sourceKey, 100);
                await updateTime.ExecuteNonQueryAsync(ct);

                await transaction.CommitAsync(ct);
                return new ImportResult(1, false, totalVigentes, totalVigentes, 0, totalVigentes);
            }
        }
        else
        {
            await r.DisposeAsync();
        }

        await transaction.RollbackAsync(ct);
        return null;
    }

    public async Task<ImportResult> ImportAsync(SourceDocument document, IAsyncEnumerable<SourceRow> rows, CancellationToken ct)
    {
        var listing = SourceCatalog.Sat.SingleOrDefault(x => x.Key == document.SourceKey);
        if (listing is null && document.SourceKey != "dof:publicaciones") throw new ArgumentException("Fuente desconocida.");

        var c = Writer;
        await using var transaction = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            await AssertLeaseAsync(transaction, ct);

            // CASO 1: Documento técnico normativo en Excel (Causas y Motivos Art. 69)
            if (listing?.Document == true)
            {
                using var clearDoc = Command(c, "DELETE FROM dbo.DocumentoTecnico69;", transaction);
                await clearDoc.ExecuteNonQueryAsync(ct);

                using var docTable = CreateDocumentBatch();
                long docCount = 0;
                await foreach (var row in rows.WithCancellation(ct))
                {
                    foreach (var cell in row.Original)
                    {
                        var hoja = Convert.ToString(row.Typed.GetValueOrDefault("hoja")) ?? cell.Nombre;
                        docTable.Rows.Add(row.Table, row.Row, cell.Columna, hoja, (object?)cell.Valor ?? DBNull.Value, DateTime.Now);
                        docCount++;
                    }
                }

                logger?.LogInformation("Insertando matriz de celdas en dbo.DocumentoTecnico69 ({Count:N0} celdas)...", docCount);
                await FlushAsync(c, transaction, docTable, "dbo.DocumentoTecnico69", ct, logger);

                using var updateSourceDoc = Command(c, """
                    UPDATE dbo.ControlFuenteSAT
                    SET UltimaDescargaExitosa=SYSDATETIME(), UltimoIntento=SYSDATETIME(),
                        TotalRegistrosVigentes=@Count, TotalSinCambios=@Count, Resultado='sin-cambios', UltimoError=NULL
                    WHERE FuenteClave=@Source;
                    IF @@ROWCOUNT=0
                        INSERT dbo.ControlFuenteSAT(FuenteClave,Articulo,Listado,Nombre,UrlPortal,EsDocumento,Habilitada,TotalRegistrosVigentes,TotalSinCambios,Resultado)
                        VALUES(@Source,'69','documento-tecnico',N'Documento técnico y normativo',@Url,1,1,@Count,@Count,'sin-cambios');
                    """, transaction);
                P(updateSourceDoc, "@Count", SqlDbType.Int, (int)Math.Min(docCount, int.MaxValue));
                P(updateSourceDoc, "@Source", SqlDbType.VarChar, document.SourceKey, 100);
                P(updateSourceDoc, "@Url", SqlDbType.NVarChar, document.Url, 2048);
                await updateSourceDoc.ExecuteNonQueryAsync(ct);

                await transaction.CommitAsync(ct);
                return new ImportResult(1, true, docCount, docCount, 0, docCount);
            }

            // CASO 2: Publicaciones del Diario Oficial de la Federación (DOF)
            if (document.SourceKey == "dof:publicaciones")
            {
                long dofCount = 0;
                await foreach (var row in rows.WithCancellation(ct))
                {
                    dofCount++;
                    var datos = row.Datos;
                    var rfc = Rfc.Normalize(row.Rfc);
                    if (string.IsNullOrWhiteSpace(rfc)) rfc = !string.IsNullOrWhiteSpace(row.Rfc) ? row.Rfc.Trim() : "(SIN RFC)";
                    if (rfc.Length > 100) rfc = rfc[..100];

                    var name = row.Name ?? "";
                    if (name.Length > 2000) name = name[..2000];
                    var status = row.Status ?? "";
                    if (status.Length > 2000) status = status[..2000];

                    var hash = datos?.ComputeHash() ?? ComputeHashFallback(rfc, name, row.Article, row.Listing, status, row.PublicationDate);

                    using var dofCmd = Command(c, "dbo.AplicarPublicacionDOF", transaction);
                    dofCmd.CommandType = CommandType.StoredProcedure;
                    P(dofCmd, "@Rfc", SqlDbType.NVarChar, rfc, 100);
                    P(dofCmd, "@RazonSocial", SqlDbType.NVarChar, name, 2000);
                    P(dofCmd, "@Articulo", SqlDbType.VarChar, row.Article, 16);
                    P(dofCmd, "@Listado", SqlDbType.VarChar, row.Listing, 100);
                    P(dofCmd, "@Situacion", SqlDbType.NVarChar, status, 2000);
                    P(dofCmd, "@FechaEfectiva", SqlDbType.Date, row.PublicationDate);
                    P(dofCmd, "@CodNotaDOF", SqlDbType.BigInt, document.NoteCode);
                    P(dofCmd, "@UrlOrigen", SqlDbType.NVarChar, document.Url, 2048);
                    P(dofCmd, "@NumeroOficioDOF", SqlDbType.NVarChar, datos?.NumeroOficioDOF, 200);
                    P(dofCmd, "@EntidadFederativa", SqlDbType.NVarChar, row.Entity, 1000);
                    P(dofCmd, "@Motivo", SqlDbType.NVarChar, datos?.Motivo, 2000);
                    P(dofCmd, "@HashFila", SqlDbType.VarBinary, hash, 32);

                    await dofCmd.ExecuteNonQueryAsync(ct);
                }

                // Registrar metadatos de la publicación/decreto en dbo.PublicacionDOF para trazabilidad forense
                if (document.NoteCode is { } codNota)
                {
                    using var pubCmd = Command(c, """
                        IF NOT EXISTS (SELECT 1 FROM dbo.PublicacionDOF WITH (UPDLOCK, HOLDLOCK) WHERE CodNota = @CodNota)
                        BEGIN
                            INSERT INTO dbo.PublicacionDOF (
                                CodNota, Titulo, FechaPublicacion, Edicion, Organismo, Articulo, UrlNota, HashArchivo, RutaArchivo, TotalContribuyentes, FechaRegistro
                            ) VALUES (
                                @CodNota, @Titulo, @FechaPublicacion, @Edicion, @Organismo, @Articulo, @UrlNota, @HashArchivo, @RutaArchivo, @Total, SYSDATETIME()
                            );
                        END
                        ELSE
                        BEGIN
                            UPDATE dbo.PublicacionDOF
                            SET Titulo = @Titulo,
                                FechaPublicacion = @FechaPublicacion,
                                Edicion = @Edicion,
                                Organismo = @Organismo,
                                Articulo = @Articulo,
                                UrlNota = @UrlNota,
                                HashArchivo = @HashArchivo,
                                RutaArchivo = @RutaArchivo,
                                TotalContribuyentes = @Total,
                                FechaRegistro = SYSDATETIME()
                            WHERE CodNota = @CodNota;
                        END
                        """, transaction);
                    P(pubCmd, "@CodNota", SqlDbType.BigInt, codNota);
                    P(pubCmd, "@Titulo", SqlDbType.NVarChar, (object?)document.Title ?? "Publicación DOF", 1000);
                    P(pubCmd, "@FechaPublicacion", SqlDbType.Date, document.SourceDate is { } sd ? sd.ToDateTime(TimeOnly.MinValue) : (object)DateTime.Today);
                    P(pubCmd, "@Edicion", SqlDbType.VarChar, (object?)document.Edition ?? DBNull.Value, 50);
                    P(pubCmd, "@Organismo", SqlDbType.NVarChar, (object?)document.Agency ?? DBNull.Value, 500);
                    P(pubCmd, "@Articulo", SqlDbType.VarChar, document.Article ?? "69-b", 16);
                    P(pubCmd, "@UrlNota", SqlDbType.NVarChar, document.Url, 2048);
                    P(pubCmd, "@HashArchivo", SqlDbType.VarChar, (object?)document.Hash ?? DBNull.Value, 64);
                    P(pubCmd, "@RutaArchivo", SqlDbType.NVarChar, (object?)document.EvidencePath ?? DBNull.Value, 2048);
                    P(pubCmd, "@Total", SqlDbType.Int, (int)dofCount);
                    await pubCmd.ExecuteNonQueryAsync(ct);
                }

                await transaction.CommitAsync(ct);
                return new ImportResult(1, true, dofCount, dofCount, 0, dofCount);
            }

            // CASO 3: Listados descargados del SAT (CSV masivos: Art. 69, 69-B, 69-B Bis)
            // Crear tabla de Staging temporal en la sesión
            using var createStaging = Command(c, """
                IF OBJECT_ID('tempdb..#ContribuyenteStaging') IS NOT NULL DROP TABLE #ContribuyenteStaging;
                CREATE TABLE #ContribuyenteStaging (
                    Rfc                     nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                    RazonSocial             nvarchar(2000) NOT NULL,
                    Situacion               nvarchar(2000) NOT NULL,
                    FechaEfectiva           date NULL,
                    TipoPersona             varchar(20) NULL,
                    EntidadFederativa       nvarchar(1000) NULL,
                    Monto                   decimal(18,2) NULL,
                    Ejercicio               varchar(50) NULL,
                    Periodo                 varchar(100) NULL,
                    Motivo                  nvarchar(2000) NULL,
                    FechaCancelacion        date NULL,
                    FechaAutorizacion       date NULL,
                    Administracion          nvarchar(500) NULL,
                    NumeroOficioSAT         nvarchar(200) NULL,
                    FechaOficioSAT          date NULL,
                    NumeroOficioDOF         nvarchar(200) NULL,
                    FechaOficioDOF          date NULL,
                    PresuncionOficioSAT     nvarchar(200) NULL,
                    PresuncionFechaSAT      date NULL,
                    PresuncionOficioDOF     nvarchar(200) NULL,
                    PresuncionFechaDOF      date NULL,
                    DesvirtuadoOficioSAT    nvarchar(200) NULL,
                    DesvirtuadoFechaSAT     date NULL,
                    DesvirtuadoOficioDOF    nvarchar(200) NULL,
                    DesvirtuadoFechaDOF     date NULL,
                    DefinitivoOficioSAT     nvarchar(200) NULL,
                    DefinitivoFechaSAT      date NULL,
                    DefinitivoOficioDOF     nvarchar(200) NULL,
                    DefinitivoFechaDOF      date NULL,
                    SentenciaOficioSAT      nvarchar(200) NULL,
                    SentenciaFechaSAT       date NULL,
                    SentenciaOficioDOF      nvarchar(200) NULL,
                    SentenciaFechaDOF       date NULL,
                    HashFila                binary(32) NOT NULL
                );
                """, transaction);
            await createStaging.ExecuteNonQueryAsync(ct);

            using var stagingBatch = CreateStagingBatch();
            long totalRead = 0;

            await foreach (var row in rows.WithCancellation(ct))
            {
                totalRead++;
                var rfc = Rfc.Normalize(row.Rfc);
                if (string.IsNullOrWhiteSpace(rfc)) rfc = !string.IsNullOrWhiteSpace(row.Rfc) ? row.Rfc.Trim() : "(SIN RFC)";
                if (rfc.Length > 100) rfc = rfc[..100];

                var name = row.Name ?? "";
                if (name.Length > 2000) name = name[..2000];
                var status = row.Status ?? "";
                if (status.Length > 2000) status = status[..2000];

                var d = row.Datos;
                var hash = d?.ComputeHash() ?? ComputeHashFallback(rfc, name, row.Article, row.Listing, status, row.PublicationDate, d?.Monto, d?.Motivo);

                stagingBatch.Rows.Add(
                    rfc,
                    name,
                    status,
                    row.PublicationDate is { } pubDate ? pubDate.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                    (object?)d?.TipoPersona ?? DBNull.Value,
                    (object?)row.Entity ?? DBNull.Value,
                    (object?)d?.Monto ?? DBNull.Value,
                    (object?)d?.Ejercicio ?? DBNull.Value,
                    (object?)d?.Periodo ?? DBNull.Value,
                    (object?)d?.Motivo ?? DBNull.Value,
                    d?.FechaCancelacion is { } fc ? fc.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                    d?.FechaAutorizacion is { } fa ? fa.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                    (object?)d?.Administracion ?? DBNull.Value,
                    (object?)d?.NumeroOficioSAT ?? DBNull.Value,
                    d?.FechaOficioSAT is { } fos ? fos.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                    (object?)d?.NumeroOficioDOF ?? DBNull.Value,
                    d?.FechaOficioDOF is { } fod ? fod.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                    (object?)d?.PresuncionOficioSAT ?? DBNull.Value,
                    d?.PresuncionFechaSAT is { } pfs ? pfs.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                    (object?)d?.PresuncionOficioDOF ?? DBNull.Value,
                    d?.PresuncionFechaDOF is { } pfd ? pfd.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                    (object?)d?.DesvirtuadoOficioSAT ?? DBNull.Value,
                    d?.DesvirtuadoFechaSAT is { } dfs ? dfs.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                    (object?)d?.DesvirtuadoOficioDOF ?? DBNull.Value,
                    d?.DesvirtuadoFechaDOF is { } dfd ? dfd.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                    (object?)d?.DefinitivoOficioSAT ?? DBNull.Value,
                    d?.DefinitivoFechaSAT is { } defs ? defs.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                    (object?)d?.DefinitivoOficioDOF ?? DBNull.Value,
                    d?.DefinitivoFechaDOF is { } defd ? defd.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                    (object?)d?.SentenciaOficioSAT ?? DBNull.Value,
                    d?.SentenciaFechaSAT is { } sfs ? sfs.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                    (object?)d?.SentenciaOficioDOF ?? DBNull.Value,
                    d?.SentenciaFechaDOF is { } sfd ? sfd.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                    hash
                );
            }

            if (totalRead == 0) throw new InvalidDataException("El archivo del SAT no contiene filas legibles.");

            logger?.LogInformation("Iniciando volcado masivo en #ContribuyenteStaging ({Count:N0} filas)...", stagingBatch.Rows.Count);
            await FlushAsync(c, transaction, stagingBatch, "#ContribuyenteStaging", ct, logger);
            logger?.LogInformation("Volcado completado. Ejecutando comparativa delta (dbo.AplicarCambiosSAT)...");

            using var applyChanges = Command(c, "dbo.AplicarCambiosSAT", transaction);
            applyChanges.CommandType = CommandType.StoredProcedure;
            P(applyChanges, "@FuenteClave", SqlDbType.VarChar, document.SourceKey, 100);
            P(applyChanges, "@Articulo", SqlDbType.VarChar, document.Article, 16);
            P(applyChanges, "@Listado", SqlDbType.VarChar, document.Listing, 100);
            P(applyChanges, "@UrlPortal", SqlDbType.NVarChar, document.Url, 2048);
            P(applyChanges, "@FechaPortal", SqlDbType.Date, document.SourceDate);
            P(applyChanges, "@HashArchivo", SqlDbType.Char, document.Hash, 64);

            var pNuevos = applyChanges.Parameters.Add("@TotalNuevos", SqlDbType.Int); pNuevos.Direction = ParameterDirection.Output;
            var pModificados = applyChanges.Parameters.Add("@TotalModificados", SqlDbType.Int); pModificados.Direction = ParameterDirection.Output;
            var pSinCambios = applyChanges.Parameters.Add("@TotalSinCambios", SqlDbType.Int); pSinCambios.Direction = ParameterDirection.Output;
            var pTotalVigentes = applyChanges.Parameters.Add("@TotalVigentes", SqlDbType.Int); pTotalVigentes.Direction = ParameterDirection.Output;

            await applyChanges.ExecuteNonQueryAsync(ct);

            var nuevos = Convert.ToInt32(pNuevos.Value);
            var modificados = Convert.ToInt32(pModificados.Value);
            var sinCambios = Convert.ToInt32(pSinCambios.Value);
            var totalVigentes = Convert.ToInt32(pTotalVigentes.Value);

            using var dropStaging = Command(c, "DROP TABLE #ContribuyenteStaging;", transaction);
            await dropStaging.ExecuteNonQueryAsync(ct);

            await transaction.CommitAsync(ct);

            logger?.LogInformation("Listado SAT {Key} sincronizado en BD: {Vigentes:N0} registros vigentes ({Nuevos:N0} nuevos, {Modificados:N0} modificados, {SinCambios:N0} sin cambios).",
                document.SourceKey, totalVigentes, nuevos, modificados, sinCambios);

            var changed = nuevos > 0 || modificados > 0;
            return new ImportResult(1, changed, totalVigentes, totalRead, 0, totalVigentes, null, nuevos, modificados, sinCambios);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch (SqlException) { }
            throw;
        }
    }

    private static byte[] ComputeHashFallback(string rfc, string name, string article, string listing, string status, DateOnly? date, decimal? monto = null, string? motivo = null)
    {
        var raw = $"{rfc.Trim().ToUpperInvariant()}|{name.Trim()}|{article}|{listing}|{status.Trim()}|{date:yyyy-MM-dd}|{monto?.ToString("F2", CultureInfo.InvariantCulture)}|{motivo}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }

    private static DataTable CreateDocumentBatch()
    {
        var t = new DataTable();
        t.Columns.Add("HojaOrdinal", typeof(int));
        t.Columns.Add("FilaOrdinal", typeof(int));
        t.Columns.Add("ColumnaOrdinal", typeof(int));
        t.Columns.Add("Hoja", typeof(string));
        t.Columns.Add("Valor", typeof(string));
        t.Columns.Add("FechaActualizacion", typeof(DateTime));
        return t;
    }

    private static DataTable CreateStagingBatch()
    {
        var t = new DataTable();
        t.Columns.Add("Rfc", typeof(string));
        t.Columns.Add("RazonSocial", typeof(string));
        t.Columns.Add("Situacion", typeof(string));
        t.Columns.Add("FechaEfectiva", typeof(DateTime));
        t.Columns.Add("TipoPersona", typeof(string));
        t.Columns.Add("EntidadFederativa", typeof(string));
        t.Columns.Add("Monto", typeof(decimal));
        t.Columns.Add("Ejercicio", typeof(string));
        t.Columns.Add("Periodo", typeof(string));
        t.Columns.Add("Motivo", typeof(string));
        t.Columns.Add("FechaCancelacion", typeof(DateTime));
        t.Columns.Add("FechaAutorizacion", typeof(DateTime));
        t.Columns.Add("Administracion", typeof(string));
        t.Columns.Add("NumeroOficioSAT", typeof(string));
        t.Columns.Add("FechaOficioSAT", typeof(DateTime));
        t.Columns.Add("NumeroOficioDOF", typeof(string));
        t.Columns.Add("FechaOficioDOF", typeof(DateTime));
        t.Columns.Add("PresuncionOficioSAT", typeof(string));
        t.Columns.Add("PresuncionFechaSAT", typeof(DateTime));
        t.Columns.Add("PresuncionOficioDOF", typeof(string));
        t.Columns.Add("PresuncionFechaDOF", typeof(DateTime));
        t.Columns.Add("DesvirtuadoOficioSAT", typeof(string));
        t.Columns.Add("DesvirtuadoFechaSAT", typeof(DateTime));
        t.Columns.Add("DesvirtuadoOficioDOF", typeof(string));
        t.Columns.Add("DesvirtuadoFechaDOF", typeof(DateTime));
        t.Columns.Add("DefinitivoOficioSAT", typeof(string));
        t.Columns.Add("DefinitivoFechaSAT", typeof(DateTime));
        t.Columns.Add("DefinitivoOficioDOF", typeof(string));
        t.Columns.Add("DefinitivoFechaDOF", typeof(DateTime));
        t.Columns.Add("SentenciaOficioSAT", typeof(string));
        t.Columns.Add("SentenciaFechaSAT", typeof(DateTime));
        t.Columns.Add("SentenciaOficioDOF", typeof(string));
        t.Columns.Add("SentenciaFechaDOF", typeof(DateTime));
        t.Columns.Add("HashFila", typeof(byte[]));
        return t;
    }

    private static async Task FlushAsync(SqlConnection c, SqlTransaction tx, DataTable batch, string table, CancellationToken ct, ILogger? logger = null)
    {
        if (batch.Rows.Count == 0) return;
        var totalRows = batch.Rows.Count;
        using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.TableLock, tx)
        {
            DestinationTableName = table,
            BatchSize = 25000,
            BulkCopyTimeout = 600,
            NotifyAfter = 25000,
            EnableStreaming = true
        };
        if (logger is not null)
        {
            bulk.SqlRowsCopied += (s, e) => logger.LogInformation("  [BulkCopy] {Table}: {Rows:N0} / {Total:N0} filas transferidas...", table, e.RowsCopied, totalRows);
        }
        foreach (DataColumn col in batch.Columns) bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(batch, ct);
        batch.Clear();
    }

    public async Task<long> PublishGenerationAsync(CancellationToken ct)
    {
        using var cmd = Command(Writer, "dbo.PublicarGeneracion");
        cmd.CommandType = CommandType.StoredProcedure;
        P(cmd, "@Propietario", SqlDbType.UniqueIdentifier, owner);
        P(cmd, "@Token", SqlDbType.BigInt, token);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<QueryHeader?> GetHeaderAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, "dbo.Encabezado");
        cmd.CommandType = CommandType.StoredProcedure;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(Convert.ToInt64(r.GetValue(0)), new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Local)), r.GetString(2), r.GetString(3)) : null;
    }

    public async Task<JsonElement> GetOperationalStateAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, "dbo.ConsultarEstatusGeneral");
        cmd.CommandType = CommandType.StoredProcedure;
        var scalar = await cmd.ExecuteScalarAsync(ct);
        var jsonStr = scalar is null or DBNull ? "{}" : (string)scalar;
        return JsonSerializer.Deserialize<JsonElement>(jsonStr);
    }

    public async IAsyncEnumerable<object> ReadDocumentAsync(long generation, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, "dbo.DocumentoTecnico");
        cmd.CommandType = CommandType.StoredProcedure;
        P(cmd, "@GeneracionId", SqlDbType.BigInt, generation);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            yield return new
            {
                versionId = Convert.ToInt64(reader.GetValue(0)),
                hojaOrdinal = Convert.ToInt32(reader.GetValue(1)),
                fila = Convert.ToInt32(reader.GetValue(2)),
                columna = Convert.ToInt32(reader.GetValue(3)),
                hoja = reader.GetString(4),
                valor = reader.IsDBNull(5) ? null : reader.GetString(5)
            };
        }
    }

    public async Task EnsureCatalogAsync(CancellationToken ct, AgentOptions? settings = null)
    {
        foreach (var listing in SourceCatalog.Sat)
        {
            using var cmd = Command(Writer, """
                IF NOT EXISTS (SELECT 1 FROM dbo.ControlFuenteSAT WHERE FuenteClave=@Key)
                INSERT dbo.ControlFuenteSAT(FuenteClave,Articulo,Listado,Nombre,UrlPortal,EsDocumento,Habilitada) 
                VALUES(@Key,@Article,@Listing,@Name,@Url,@Document,1);
                """);
            P(cmd, "@Key", SqlDbType.VarChar, listing.Key, 100);
            P(cmd, "@Article", SqlDbType.VarChar, listing.Article, 16);
            P(cmd, "@Listing", SqlDbType.VarChar, listing.Slug, 100);
            P(cmd, "@Name", SqlDbType.NVarChar, listing.Name, 500);
            P(cmd, "@Url", SqlDbType.NVarChar, listing.Url, 2048);
            P(cmd, "@Document", SqlDbType.Bit, listing.Document);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (settings is not null)
        {
            foreach (var listing in SourceCatalog.Sat)
            {
                using var enabled = Command(Writer, "UPDATE dbo.ControlFuenteSAT SET Habilitada=@Enabled WHERE FuenteClave=@Key;");
                P(enabled, "@Enabled", SqlDbType.Bit, settings.SatEnabled && (settings.EnabledSatSources.Length == 0 || settings.EnabledSatSources.Contains(listing.Key)));
                P(enabled, "@Key", SqlDbType.VarChar, listing.Key, 100);
                await enabled.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static SqlCommand Query(SqlConnection c, string procedure, long generation, QueryFilter filter, string[]? rfcs)
    {
        if (filter.Article is not null && !SourceCatalog.Articles.Contains(filter.Article)) throw new ArgumentException("Artículo desconocido.");
        var cmd = Command(c, procedure);
        cmd.CommandType = CommandType.StoredProcedure;
        P(cmd, "@GeneracionId", SqlDbType.BigInt, generation);
        P(cmd, "@Articulo", SqlDbType.VarChar, filter.Article, 16);
        P(cmd, "@Listado", SqlDbType.VarChar, filter.Listing, 100);
        P(cmd, "@Historico", SqlDbType.Bit, filter.IncludeHistory);
        P(cmd, "@FiltrarRfc", SqlDbType.Bit, rfcs is not null);
        var table = new DataTable();
        table.Columns.Add("Rfc", typeof(string));
        table.Columns.Add("OrdenEntrada", typeof(int));
        if (rfcs is not null)
        {
            var normalized = Rfc.ValidateMany(rfcs);
            for (var i = 0; i < normalized.Length; i++) table.Rows.Add(normalized[i], i);
        }
        var p = cmd.Parameters.Add("@Rfcs", SqlDbType.Structured);
        p.TypeName = "dbo.RfcInput";
        p.Value = table;
        return cmd;
    }

    public async IAsyncEnumerable<Appearance> ReadAsync(long generation, QueryFilter filter, string[]? rfcs, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = Query(c, "dbo.Apariciones", generation, filter, rfcs);
        await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await r.ReadAsync(ct))
        {
            yield return new(
                Convert.ToInt64(r.GetValue(0)),
                Convert.ToInt64(r.GetValue(1)),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.GetString(5),
                r.GetString(6),
                r.GetString(7),
                r.GetString(8),
                r.IsDBNull(9) ? null : DateOnly.FromDateTime(r.GetDateTime(9)),
                r.GetBoolean(10),
                r.IsDBNull(11) ? null : Convert.ToInt64(r.GetValue(11)),
                r.GetString(12),
                Convert.ToInt32(r.GetValue(13)),
                Convert.ToInt32(r.GetValue(14)),
                r.GetString(15)
            );
        }
    }

    public async Task SaveExecutionAsync(ExecutionReport report, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);

        using var cmd = Command(c, """
            IF NOT EXISTS (SELECT 1 FROM dbo.BitacoraEjecucion WITH(UPDLOCK,HOLDLOCK) WHERE EjecucionId=@Id)
            INSERT dbo.BitacoraEjecucion(EjecucionId,Inicio,Fin,Resultado,Disparador,ResumenJson) 
            VALUES(@Id,@Start,@End,@Result,@Trigger,@Json);
            """, tx);
        P(cmd, "@Id", SqlDbType.UniqueIdentifier, report.EjecucionId);
        P(cmd, "@Start", SqlDbType.DateTime2, report.Inicio);
        P(cmd, "@End", SqlDbType.DateTime2, report.Fin);
        P(cmd, "@Result", SqlDbType.VarChar, report.Resultado, 30);
        P(cmd, "@Trigger", SqlDbType.VarChar, report.Disparador, 50);
        P(cmd, "@Json", SqlDbType.NVarChar, JsonSerializer.Serialize(report, Json), -1);
        await cmd.ExecuteNonQueryAsync(ct);

        foreach (var source in report.Fuentes.Where(s => s.Fuente != "sistema" && !s.Fuente.StartsWith("dof:")))
        {
            using var state = Command(c, """
                UPDATE dbo.ControlFuenteSAT WITH(UPDLOCK,HOLDLOCK) 
                SET UltimoIntento=@Date, 
                    UltimaDescargaExitosa=CASE WHEN @Ok=1 THEN @Date ELSE UltimaDescargaExitosa END,
                    Resultado=@Result, 
                    UltimoError=@Error 
                WHERE FuenteClave=@Source;
                """, tx);
            P(state, "@Date", SqlDbType.DateTime2, report.Fin);
            P(state, "@Ok", SqlDbType.Bit, source.Resultado is "cambios" or "sin-cambios");
            P(state, "@Result", SqlDbType.VarChar, source.Resultado, 30);
            P(state, "@Error", SqlDbType.NVarChar, source.Error, 2000);
            P(state, "@Source", SqlDbType.VarChar, source.Fuente, 100);
            await state.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task CompleteDofDayAsync(DateOnly date, bool complete, string? detail, CancellationToken ct)
    {
        using var cmd = Command(Writer, """
            UPDATE dbo.ControlCoberturaDOF 
            SET Completa=@Complete, FechaComprobacion=SYSDATETIME(), Detalle=@Detail 
            WHERE Fecha=@Date;
            IF @@ROWCOUNT=0 
                INSERT dbo.ControlCoberturaDOF(Fecha,Completa,FechaComprobacion,Detalle) 
                VALUES(@Date,@Complete,SYSDATETIME(),@Detail);
            """);
        P(cmd, "@Complete", SqlDbType.Bit, complete);
        P(cmd, "@Date", SqlDbType.Date, date);
        P(cmd, "@Detail", SqlDbType.NVarChar, detail, 2000);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DateOnly?> LastCompleteDofDateAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, "SELECT Fecha,Completa FROM dbo.ControlCoberturaDOF ORDER BY Fecha;");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        DateOnly? last = null;
        while (await r.ReadAsync(ct))
        {
            var day = DateOnly.FromDateTime(r.GetDateTime(0));
            if (!r.GetBoolean(1) || (last is not null && day != last.Value.AddDays(1))) break;
            last = day;
        }
        return last;
    }

    public async Task<IReadOnlyList<DateOnly>> PendingDofDaysAsync(DateOnly startDate, DateOnly today, int lookback, int max, CancellationToken ct)
    {
        if (max < 1 || lookback < 1) throw new ArgumentOutOfRangeException(nameof(max));
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, "SELECT Fecha,Completa,FechaComprobacion FROM dbo.ControlCoberturaDOF WHERE Fecha BETWEEN @Start AND @End;");
        P(cmd, "@Start", SqlDbType.Date, startDate);
        P(cmd, "@End", SqlDbType.Date, today);
        var complete = new Dictionary<DateOnly, DateTime>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                if (r.GetBoolean(1)) complete[DateOnly.FromDateTime(r.GetDateTime(0))] = r.GetDateTime(2);
            }
        }
        var result = new List<DateOnly>();
        for (var day = startDate; day <= today && result.Count < max; day = day.AddDays(1))
        {
            if (!complete.ContainsKey(day)) result.Add(day);
        }
        var cutoff = today.AddDays(1 - lookback);
        foreach (var day in complete.Where(x => x.Key >= cutoff).OrderBy(x => x.Value).ThenBy(x => x.Key).Select(x => x.Key).Take(max - result.Count))
        {
            result.Add(day);
        }
        return result;
    }

    public async Task SaveDofPendingAsync(long code, DateOnly date, string reason, CancellationToken ct)
    {
        using var cmd = Command(Writer, """
            UPDATE dbo.NotaPendienteDOF 
            SET Fecha=@Date, Estado='pendiente', Motivo=@Reason, UltimoIntento=SYSDATETIME() 
            WHERE CodNota=@Code;
            IF @@ROWCOUNT=0 
                INSERT dbo.NotaPendienteDOF(CodNota,Fecha,Estado,Motivo,UltimoIntento) 
                VALUES(@Code,@Date,'pendiente',@Reason,SYSDATETIME());
            """);
        P(cmd, "@Code", SqlDbType.BigInt, code);
        P(cmd, "@Date", SqlDbType.Date, date);
        P(cmd, "@Reason", SqlDbType.NVarChar, reason, 2000);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearDofPendingAsync(long code, CancellationToken ct)
    {
        using var cmd = Command(Writer, "DELETE FROM dbo.NotaPendienteDOF WHERE CodNota=@Code;");
        P(cmd, "@Code", SqlDbType.BigInt, code);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordAdjunctAsync(long code, string url, string state, string? path, string? reason, CancellationToken ct)
    {
        using var cmd = Command(Writer, """
            UPDATE dbo.AdjuntoDOF 
            SET Estado=@State, RutaArchivo=@Path, Motivo=@Reason 
            WHERE CodNota=@Code AND Url=@Url;
            IF @@ROWCOUNT=0 
                INSERT dbo.AdjuntoDOF(CodNota,Url,Estado,RutaArchivo,Motivo) 
                VALUES(@Code,@Url,@State,@Path,@Reason);
            """);
        P(cmd, "@Code", SqlDbType.BigInt, code);
        P(cmd, "@Url", SqlDbType.NVarChar, url, 2048);
        P(cmd, "@State", SqlDbType.VarChar, state, 30);
        P(cmd, "@Path", SqlDbType.NVarChar, path, 2048);
        P(cmd, "@Reason", SqlDbType.NVarChar, reason, 2000);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
