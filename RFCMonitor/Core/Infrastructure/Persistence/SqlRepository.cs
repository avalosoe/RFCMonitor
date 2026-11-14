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
using MonitorRFC.Infrastructure.Sources;

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
            // Evita esperas indefinidas por bloqueos de otra sesión: falla con el error 1222 en lugar de colgar la ingesta.
            using (var lockTimeout = Command(c, $"SET LOCK_TIMEOUT {LockTimeoutMs};")) await lockTimeout.ExecuteNonQueryAsync(ct);
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
                // 1) Leer el documento completo y calcular la huella de su CONTENIDO (celdas en orden). El hash del archivo .xls
                //    no sirve para detectar cambios: el contenedor Excel puede variar sin que cambie el texto.
                var cellList = new List<(int Tabla, int Fila, int Columna, string Hoja, string? Valor)>();
                using var contentHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await foreach (var row in rows.WithCancellation(ct))
                {
                    foreach (var cell in row.Original)
                    {
                        var hoja = Convert.ToString(row.Typed.GetValueOrDefault("hoja")) ?? cell.Nombre;
                        cellList.Add((row.Table, row.Row, cell.Columna, hoja, cell.Valor));
                        contentHasher.AppendData(Encoding.UTF8.GetBytes($"{row.Table}|{row.Row}|{cell.Columna}|{hoja}|{cell.Valor}\n"));
                    }
                }
                long docCount = cellList.Count;
                var contentHash = Convert.ToHexString(contentHasher.GetHashAndReset());

                // 2) Comparar contra la huella guardada: si el contenido es idéntico no se recarga ni se reporta como cambio.
                string? previousHash = null;
                using (var findHash = Command(c, "SELECT UltimoHashArchivo FROM dbo.ControlFuenteSAT WITH(UPDLOCK,HOLDLOCK) WHERE FuenteClave=@Source;", transaction))
                {
                    P(findHash, "@Source", SqlDbType.VarChar, document.SourceKey, 100);
                    previousHash = (await findHash.ExecuteScalarAsync(ct)) as string;
                }
                long storedCells;
                using (var countCells = Command(c, "SELECT COUNT_BIG(*) FROM dbo.DocumentoTecnico69;", transaction))
                    storedCells = Convert.ToInt64(await countCells.ExecuteScalarAsync(ct));

                if (docCount > 0 && storedCells == docCount && string.Equals(previousHash?.Trim(), contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogInformation("  [Documento técnico] Contenido idéntico al ya cargado ({Count:N0} celdas, huella {Hash}); sin cambios.", docCount, contentHash[..12]);
                    using var touch = Command(c, """
                        UPDATE dbo.ControlFuenteSAT
                        SET UltimoIntento=SYSDATETIME(), UltimaDescargaExitosa=SYSDATETIME(), Resultado='sin-cambios', UltimoError=NULL,
                            TotalRegistrosVigentes=@Count, TotalNuevos=0, TotalModificados=0, TotalSinCambios=@Count,
                            FechaPublicacionPortal=COALESCE(@Portal, FechaPublicacionPortal)
                        WHERE FuenteClave=@Source;
                        """, transaction);
                    P(touch, "@Count", SqlDbType.Int, (int)Math.Min(docCount, int.MaxValue));
                    P(touch, "@Portal", SqlDbType.Date, document.SourceDate);
                    P(touch, "@Source", SqlDbType.VarChar, document.SourceKey, 100);
                    await touch.ExecuteNonQueryAsync(ct);
                    await transaction.CommitAsync(ct);
                    return new ImportResult(1, false, docCount, docCount, 0, docCount, null, 0, 0, (int)Math.Min(docCount, int.MaxValue));
                }

                // 3) Hubo cambios reales (o primera carga): reemplazar el contenido.
                logger?.LogInformation("  [Documento técnico] Contenido distinto (huella anterior {Prev}, nueva {New}); recargando dbo.DocumentoTecnico69...",
                    previousHash is null ? "ninguna" : previousHash.Trim()[..Math.Min(12, previousHash.Trim().Length)], contentHash[..12]);
                using var clearDoc = Command(c, "DELETE FROM dbo.DocumentoTecnico69;", transaction);
                await WithHeartbeatAsync("DELETE dbo.DocumentoTecnico69", () => clearDoc.ExecuteNonQueryAsync(ct), ct);

                logger?.LogInformation("Insertando matriz de celdas en dbo.DocumentoTecnico69 ({Count:N0} celdas)...", docCount);

                const int batchSize = 100;
                for (int i = 0; i < cellList.Count; i += batchSize)
                {
                    var chunk = cellList.Skip(i).Take(batchSize).ToList();
                    var sb = new StringBuilder("INSERT INTO dbo.DocumentoTecnico69 (HojaOrdinal, FilaOrdinal, ColumnaOrdinal, Hoja, Valor, FechaActualizacion) VALUES");
                    using var insertCmd = c.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandTimeout = 120;

                    for (int j = 0; j < chunk.Count; j++)
                    {
                        if (j > 0) sb.Append(", ");
                        sb.Append($"(@t{j}, @f{j}, @c{j}, @h{j}, @v{j}, SYSDATETIME())");
                        insertCmd.Parameters.Add(new SqlParameter($"@t{j}", SqlDbType.Int) { Value = chunk[j].Tabla });
                        insertCmd.Parameters.Add(new SqlParameter($"@f{j}", SqlDbType.Int) { Value = chunk[j].Fila });
                        insertCmd.Parameters.Add(new SqlParameter($"@c{j}", SqlDbType.Int) { Value = chunk[j].Columna });
                        insertCmd.Parameters.Add(new SqlParameter($"@h{j}", SqlDbType.NVarChar, 300) { Value = chunk[j].Hoja });
                        insertCmd.Parameters.Add(new SqlParameter($"@v{j}", SqlDbType.NVarChar, 4000) { Value = (object?)chunk[j].Valor ?? DBNull.Value });
                    }
                    insertCmd.CommandText = sb.ToString();
                    await insertCmd.ExecuteNonQueryAsync(ct);
                }
                logger?.LogInformation("  [Documento técnico] Carga finalizada ({Count:N0} celdas); actualizando control de fuente y confirmando transacción...", docCount);

                using var updateSourceDoc = Command(c, """
                    UPDATE dbo.ControlFuenteSAT
                    SET UltimaDescargaExitosa=SYSDATETIME(), UltimoIntento=SYSDATETIME(),
                        TotalRegistrosVigentes=@Count, TotalNuevos=0, TotalModificados=@Count, TotalSinCambios=0,
                        Resultado='cambios', UltimoError=NULL, UltimoHashArchivo=@Hash,
                        FechaPublicacionPortal=COALESCE(@Portal, FechaPublicacionPortal)
                    WHERE FuenteClave=@Source;
                    IF @@ROWCOUNT=0
                        INSERT dbo.ControlFuenteSAT(FuenteClave,Articulo,Listado,Nombre,UrlPortal,EsDocumento,Habilitada,TotalRegistrosVigentes,TotalModificados,Resultado,UltimoHashArchivo,FechaPublicacionPortal)
                        VALUES(@Source,'69','documento-tecnico',N'Documento técnico y normativo',@Url,1,1,@Count,@Count,'cambios',@Hash,@Portal);
                    """, transaction);
                P(updateSourceDoc, "@Hash", SqlDbType.Char, contentHash, 64);
                P(updateSourceDoc, "@Portal", SqlDbType.Date, document.SourceDate);
                P(updateSourceDoc, "@Count", SqlDbType.Int, (int)Math.Min(docCount, int.MaxValue));
                P(updateSourceDoc, "@Source", SqlDbType.VarChar, document.SourceKey, 100);
                P(updateSourceDoc, "@Url", SqlDbType.NVarChar, document.Url, 2048);
                await updateSourceDoc.ExecuteNonQueryAsync(ct);

                await transaction.CommitAsync(ct);
                return new ImportResult(1, true, docCount, docCount, 0, docCount);
            }

            // CASO 2: Publicaciones del Diario Oficial de la Federación (DOF)
            // Comparativa masiva: una sola carga bulk a tabla temporal + sentencias set-based (sin SP fila por fila).
            if (document.SourceKey == "dof:publicaciones")
            {
                var staging = CreateDofStagingTable();
                await foreach (var row in rows.WithCancellation(ct))
                    AddDofStagingRow(staging, row, document.NoteCode, document.Url);

                var notes = new List<DofNoteMeta>();
                if (document.NoteCode is { } codNota)
                    notes.Add(new DofNoteMeta(codNota, document.Title ?? "Publicación DOF", document.SourceDate ?? DateOnly.FromDateTime(DateTime.Today),
                        document.Edition, document.Agency, document.Article ?? "69-b", document.Url, document.Hash, document.EvidencePath, staging.Rows.Count));

                logger?.LogInformation("  [DOF nota {Nota}] {Count:N0} fila(s) a comparar masivamente contra dbo.ContribuyenteVigente...", document.NoteCode, staging.Rows.Count);
                var summary = await WithHeartbeatAsync($"Comparativa masiva DOF nota {document.NoteCode}",
                    () => ApplyDofStagingAsync(c, transaction, staging, notes, ct), ct);

                await transaction.CommitAsync(ct);
                var nuevosDof = summary.Sum(s => s.Nuevos);
                var modifDof = summary.Sum(s => s.Actualizados);
                var sinDof = summary.Sum(s => s.SinCambios);
                logger?.LogInformation("  [DOF nota {Nota}] Aplicado: {Nuevos:N0} nuevos, {Modificados:N0} modificados, {Sin:N0} sin cambios.", document.NoteCode, nuevosDof, modifDof, sinDof);
                return new ImportResult(1, nuevosDof + modifDof > 0, staging.Rows.Count, staging.Rows.Count, 0, staging.Rows.Count, null, nuevosDof, modifDof, sinDof);
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

                if (stagingBatch.Rows.Count >= 25000)
                {
                    logger?.LogInformation("Enviando lote de {Count:N0} filas a #ContribuyenteStaging (total procesado: {Total:N0})...", stagingBatch.Rows.Count, totalRead);
                    await WithHeartbeatAsync("Carga bulk a #ContribuyenteStaging", () => FlushAsync(c, transaction, stagingBatch, "#ContribuyenteStaging", ct, logger), ct);
                }
            }

            if (totalRead == 0) throw new InvalidDataException("El archivo del SAT no contiene filas legibles.");

            if (stagingBatch.Rows.Count > 0)
            {
                logger?.LogInformation("Enviando lote remanente de {Count:N0} filas a #ContribuyenteStaging...", stagingBatch.Rows.Count);
                await FlushAsync(c, transaction, stagingBatch, "#ContribuyenteStaging", ct, logger);
            }

            logger?.LogInformation("Volcado a #ContribuyenteStaging completado ({Total:N0} filas). Creando índice y ejecutando comparativa delta (dbo.AplicarCambiosSAT)...", totalRead);
            using var idxStaging = Command(c, "CREATE NONCLUSTERED INDEX IX_ContribuyenteStaging_Hash ON #ContribuyenteStaging (HashFila);", transaction);
            await idxStaging.ExecuteNonQueryAsync(ct);

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

            await WithHeartbeatAsync($"dbo.AplicarCambiosSAT ({document.SourceKey})", () => applyChanges.ExecuteNonQueryAsync(ct), ct);

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

    // ------------------------------------------------------------------------
    // Diagnóstico de pasos lentos / bloqueos
    // ------------------------------------------------------------------------

    private const int LockTimeoutMs = 180_000;
    private int monitoredSpid;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private Task WithHeartbeatAsync(string step, Func<Task> action, CancellationToken ct) =>
        WithHeartbeatAsync<int>(step, async () => { await action(); return 0; }, ct);

    /// <summary>
    /// Ejecuta un paso de base de datos y, mientras no termine, escribe cada 30 s un latido en la bitácora
    /// con el tiempo transcurrido y las sesiones que bloquean (si la cuenta SQL tiene VIEW SERVER STATE).
    /// </summary>
    private async Task<T> WithHeartbeatAsync<T>(string step, Func<Task<T>> action, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var beat = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(HeartbeatInterval, stop.Token);
                    logger?.LogWarning("  [Latido] El paso '{Step}' sigue en ejecución tras {Seconds:N0} s. {Blocking}", step, started.Elapsed.TotalSeconds, await DescribeBlockingAsync());
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            var result = await action();
            logger?.LogInformation("  [Paso] '{Step}' completado en {Seconds:N1} s.", step, started.Elapsed.TotalSeconds);
            return result;
        }
        catch (SqlException ex) when (ex.Number == 1222)
        {
            logger?.LogError("  [Paso] '{Step}' abortado tras {Seconds:N0} s por espera de bloqueo (error 1222). {Blocking}", step, started.Elapsed.TotalSeconds, await DescribeBlockingAsync());
            throw;
        }
        finally { stop.Cancel(); await beat; }
    }

    private async Task<string> DescribeBlockingAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var c = await OpenAsync(timeout.Token);
            using var cmd = Command(c, """
                SELECT TOP (5) r.session_id, r.blocking_session_id, r.wait_type, r.wait_time, s.program_name, s.host_name,
                       LEFT(REPLACE(REPLACE(t.text, CHAR(13), ' '), CHAR(10), ' '), 160)
                FROM sys.dm_exec_requests r
                JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
                OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
                WHERE r.blocking_session_id <> 0 AND r.database_id = DB_ID()
                ORDER BY r.wait_time DESC;
                """);
            cmd.CommandTimeout = 8;
            var sb = new StringBuilder();
            await using var r = await cmd.ExecuteReaderAsync(timeout.Token);
            while (await r.ReadAsync(timeout.Token))
                sb.Append($"sesión {r.GetValue(0)} bloqueada por {r.GetValue(1)} ({(r.IsDBNull(2) ? "-" : r.GetString(2))}, {r.GetValue(3)} ms, app='{(r.IsDBNull(4) ? "" : r.GetString(4))}', host='{(r.IsDBNull(5) ? "" : r.GetString(5))}', sql='{(r.IsDBNull(6) ? "" : r.GetString(6))}'); ");
            await r.CloseAsync();
            if (monitoredSpid > 0)
            {
                // Estado de la propia sesión de ingesta: qué espera y qué sentencia ejecuta.
                using var own = Command(c, """
                    SELECT r.status, r.command, r.wait_type, r.wait_time, r.blocking_session_id, r.cpu_time, r.logical_reads,
                           LEFT(REPLACE(REPLACE(SUBSTRING(t.text, r.statement_start_offset/2+1,
                                (CASE WHEN r.statement_end_offset=-1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END - r.statement_start_offset)/2+1),
                                CHAR(13),' '),CHAR(10),' '),200)
                    FROM sys.dm_exec_requests r OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t WHERE r.session_id=@spid;
                    """);
                own.CommandTimeout = 8;
                P(own, "@spid", SqlDbType.Int, monitoredSpid);
                await using var o = await own.ExecuteReaderAsync(timeout.Token);
                if (await o.ReadAsync(timeout.Token))
                    sb.Append($"Sesión de ingesta {monitoredSpid}: estado={o.GetValue(0)}, comando={o.GetValue(1)}, espera={(o.IsDBNull(2) ? "-" : o.GetString(2))} {o.GetValue(3)} ms, bloqueada por={o.GetValue(4)}, cpu={o.GetValue(5)} ms, lecturas={o.GetValue(6)}, sql='{(o.IsDBNull(7) ? "" : o.GetString(7))}'. ");
                else sb.Append($"Sesión de ingesta {monitoredSpid}: sin solicitud activa en SQL Server (la espera está del lado del cliente/red). ");
            }
            return sb.Length == 0 ? "Sin bloqueos detectados en SQL Server (el paso está trabajando o esperando red/disco)." : "Detalle: " + sb;
        }
        catch (Exception ex) { return "No fue posible consultar bloqueos: " + ex.Message; }
    }

    // ------------------------------------------------------------------------
    // DOF: staging y comparativa masiva (set-based)
    // ------------------------------------------------------------------------

    internal sealed record DofNoteMeta(long CodNota, string Titulo, DateOnly Fecha, string? Edicion, string? Organismo, string Articulo,
        string Url, string? Hash, string? RutaArchivo, int Total);

    internal static DataTable CreateDofStagingTable()
    {
        var t = new DataTable();
        t.Columns.Add("Rfc", typeof(string));
        t.Columns.Add("RazonSocial", typeof(string));
        t.Columns.Add("Articulo", typeof(string));
        t.Columns.Add("Listado", typeof(string));
        t.Columns.Add("Situacion", typeof(string));
        t.Columns.Add("FechaEfectiva", typeof(DateTime));
        t.Columns.Add("CodNotaDOF", typeof(long));
        t.Columns.Add("UrlOrigen", typeof(string));
        t.Columns.Add("NumeroOficioDOF", typeof(string));
        t.Columns.Add("EntidadFederativa", typeof(string));
        t.Columns.Add("Motivo", typeof(string));
        t.Columns.Add("HashFila", typeof(byte[]));
        return t;
    }

    internal static void AddDofStagingRow(DataTable table, SourceRow row, long? codNota, string url)
    {
        var rfc = Rfc.Normalize(row.Rfc);
        if (string.IsNullOrWhiteSpace(rfc)) rfc = !string.IsNullOrWhiteSpace(row.Rfc) ? row.Rfc.Trim() : "(SIN RFC)";
        if (rfc.Length > 100) rfc = rfc[..100];
        var name = row.Name ?? "";
        if (name.Length > 2000) name = name[..2000];
        var status = row.Status ?? "";
        if (status.Length > 2000) status = status[..2000];
        var datos = row.Datos;
        var hash = datos?.ComputeHash() ?? ComputeHashFallback(rfc, name, row.Article, row.Listing, status, row.PublicationDate);

        table.Rows.Add(rfc, name, row.Article, row.Listing, status,
            row.PublicationDate.HasValue ? row.PublicationDate.Value.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
            codNota.HasValue ? codNota.Value : DBNull.Value,
            url,
            (object?)datos?.NumeroOficioDOF ?? DBNull.Value,
            (object?)row.Entity ?? DBNull.Value,
            (object?)datos?.Motivo ?? DBNull.Value,
            hash);
    }

    /// <summary>
    /// Compara de forma masiva (una carga bulk + sentencias set-based) las filas DOF contra el estado vigente,
    /// registra historial idempotente, actualiza/inserta vigentes y deja la metadata de las notas. Devuelve el resumen por tipo.
    /// </summary>
    internal async Task<IReadOnlyList<DofSyncResumenTipo>> ApplyDofStagingAsync(
        SqlConnection c, SqlTransaction tx, DataTable staging, IReadOnlyList<DofNoteMeta> notes, CancellationToken ct)
    {
        var resumen = new List<DofSyncResumenTipo>();
        monitoredSpid = c.ServerProcessId;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Sub(string text) { logger?.LogInformation("    [DOF-SQL spid {Spid}] {Text} (+{Ms:N0} ms)", monitoredSpid, text, sw.ElapsedMilliseconds); sw.Restart(); }
        Sub($"Inicio; {staging.Rows.Count:N0} fila(s) en memoria, {notes.Count} nota(s)");

        using (var create = Command(c, """
            SET LOCK_TIMEOUT 180000;
            IF OBJECT_ID('tempdb..#DofStaging') IS NOT NULL DROP TABLE #DofStaging;
            CREATE TABLE #DofStaging (
                Rfc                 nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                RazonSocial         nvarchar(2000) COLLATE DATABASE_DEFAULT NOT NULL,
                Articulo            varchar(16) COLLATE DATABASE_DEFAULT NOT NULL,
                Listado             varchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                Situacion           nvarchar(2000) COLLATE DATABASE_DEFAULT NOT NULL,
                FechaEfectiva       date NULL,
                CodNotaDOF          bigint NULL,
                UrlOrigen           nvarchar(2048) COLLATE DATABASE_DEFAULT NULL,
                NumeroOficioDOF     nvarchar(200) COLLATE DATABASE_DEFAULT NULL,
                EntidadFederativa   nvarchar(1000) COLLATE DATABASE_DEFAULT NULL,
                Motivo              nvarchar(2000) COLLATE DATABASE_DEFAULT NULL,
                HashFila            binary(32) NOT NULL
            );
            """, tx))
            await create.ExecuteNonQueryAsync(ct);
        Sub("a) Tabla temporal #DofStaging creada");

        if (staging.Rows.Count > 0)
        {
            using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx) { DestinationTableName = "#DofStaging", BulkCopyTimeout = 300 };
            foreach (DataColumn col in staging.Columns) bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(staging, ct);
        }
        Sub("b) Carga bulk a #DofStaging");

        using (var prepare = Command(c, """
            -- Historial idempotente y COMPLETO: una fila por RFC/artículo/listado/nota DOF, ANTES de depurar el staging,
            -- para conservar la trazabilidad (p. ej. presunto en enero y definitivo en agosto del mismo RFC).
            INSERT INTO dbo.ContribuyenteHistorial (
                Rfc, RazonSocial, Articulo, Listado, Situacion, FechaEfectiva,
                TipoEvento, Origen, FechaRegistro, CodNotaDOF, UrlOrigen,
                NumeroOficioSAT, FechaOficioSAT, NumeroOficioDOF, FechaOficioDOF,
                EntidadFederativa, Motivo, HashFila)
            SELECT x.Rfc, x.RazonSocial, x.Articulo, x.Listado, x.Situacion, x.FechaEfectiva,
                   'Publicacion DOF', 'DOF', SYSDATETIME(), x.CodNotaDOF, x.UrlOrigen,
                   NULL, NULL, x.NumeroOficioDOF, x.FechaEfectiva,
                   x.EntidadFederativa, x.Motivo, x.HashFila
            FROM (SELECT s.*, ROW_NUMBER() OVER (PARTITION BY s.Rfc, s.Articulo, s.Listado, s.CodNotaDOF ORDER BY s.FechaEfectiva DESC) AS rn
                  FROM #DofStaging s) x
            WHERE x.rn = 1
              AND NOT EXISTS (SELECT 1 FROM dbo.ContribuyenteHistorial h
                              WHERE h.Rfc = x.Rfc AND h.Articulo = x.Articulo AND h.Listado = x.Listado
                                AND h.Origen = 'DOF' AND h.CodNotaDOF = x.CodNotaDOF);

            -- Para el estado vigente solo cuenta el movimiento más reciente de cada RFC/artículo/listado.
            ;WITH d AS (
                SELECT ROW_NUMBER() OVER (PARTITION BY Rfc, Articulo, Listado ORDER BY FechaEfectiva DESC, CodNotaDOF DESC) AS rn
                FROM #DofStaging)
            DELETE FROM d WHERE rn > 1;
            CREATE UNIQUE CLUSTERED INDEX IX_DofStaging ON #DofStaging (Rfc, Articulo, Listado);
            """, tx))
            await prepare.ExecuteNonQueryAsync(ct);
        Sub("c) Historial completo + depuración e índice de #DofStaging");

        using (var counts = Command(c, """
            SELECT s.Articulo, s.Listado, COUNT(*) AS Encontrados,
                   SUM(CASE WHEN v.ContribuyenteId IS NULL THEN 1 ELSE 0 END) AS Nuevos,
                   SUM(CASE WHEN v.ContribuyenteId IS NOT NULL AND v.HashFila <> s.HashFila
                             AND (v.FechaEfectiva IS NULL OR s.FechaEfectiva >= v.FechaEfectiva) THEN 1 ELSE 0 END) AS Actualizados,
                   SUM(CASE WHEN v.ContribuyenteId IS NOT NULL AND NOT (v.HashFila <> s.HashFila
                             AND (v.FechaEfectiva IS NULL OR s.FechaEfectiva >= v.FechaEfectiva)) THEN 1 ELSE 0 END) AS SinCambios
            FROM #DofStaging s
            LEFT JOIN dbo.ContribuyenteVigente v ON v.Rfc = s.Rfc AND v.Articulo = s.Articulo AND v.Listado = s.Listado
            GROUP BY s.Articulo, s.Listado;
            """, tx))
        await using (var r = await counts.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var art = r.GetString(0);
                var list = r.GetString(1);
                var tipoId = CatalogoTipos.ResolverTipoId(art, list);
                var tipoObj = CatalogoTipos.ObtenerPorId(tipoId);
                resumen.Add(new DofSyncResumenTipo(tipoId, art, list, tipoObj?.Nombre ?? $"{art} {list}",
                    r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5)));
            }
        }

        using (var apply = Command(c, """
            UPDATE v
            SET RazonSocial = s.RazonSocial, Situacion = s.Situacion, FechaEfectiva = s.FechaEfectiva,
                OrigenUltimaModificacion = 'DOF', FechaModificacion = SYSDATETIME(),
                NumeroOficioDOF = COALESCE(s.NumeroOficioDOF, v.NumeroOficioDOF), FechaOficioDOF = s.FechaEfectiva,
                CodNotaDOF = s.CodNotaDOF, UrlOrigenDOF = s.UrlOrigen,
                EntidadFederativa = COALESCE(s.EntidadFederativa, v.EntidadFederativa),
                HashFila = s.HashFila, UltimaDescarga = SYSDATETIME()
            FROM dbo.ContribuyenteVigente v
            JOIN #DofStaging s ON s.Rfc = v.Rfc AND s.Articulo = v.Articulo AND s.Listado = v.Listado
            WHERE v.HashFila <> s.HashFila AND (v.FechaEfectiva IS NULL OR s.FechaEfectiva >= v.FechaEfectiva);

            INSERT INTO dbo.ContribuyenteVigente (
                Rfc, RazonSocial, Articulo, Listado, Situacion, FechaEfectiva,
                OrigenUltimaModificacion, FechaModificacion, TipoPersona, EntidadFederativa,
                NumeroOficioDOF, FechaOficioDOF, CodNotaDOF, UrlOrigenDOF, HashFila, UltimaDescarga)
            SELECT s.Rfc, s.RazonSocial, s.Articulo, s.Listado, s.Situacion, s.FechaEfectiva,
                   'DOF', SYSDATETIME(), NULL, s.EntidadFederativa,
                   s.NumeroOficioDOF, s.FechaEfectiva, s.CodNotaDOF, s.UrlOrigen, s.HashFila, SYSDATETIME()
            FROM #DofStaging s
            WHERE NOT EXISTS (SELECT 1 FROM dbo.ContribuyenteVigente v
                              WHERE v.Rfc = s.Rfc AND v.Articulo = s.Articulo AND v.Listado = s.Listado);
            """, tx))
            await apply.ExecuteNonQueryAsync(ct);
        Sub("e) Actualización + inserción de vigentes");

        if (notes.Count > 0)
        {
            // Metadata de publicaciones: pocas notas por llamada, se resuelve con un upsert parametrizado por nota
            // (sin segunda carga bulk ni tabla temporal adicional).
            foreach (var n in notes.GroupBy(x => x.CodNota).Select(g => g.Last()))
            {
                using var pubCmd = Command(c, """
                    UPDATE dbo.PublicacionDOF WITH (UPDLOCK, HOLDLOCK)
                    SET Titulo = @Titulo, FechaPublicacion = @FechaPublicacion, Edicion = @Edicion, Organismo = @Organismo, Articulo = @Articulo,
                        UrlNota = @UrlNota, HashArchivo = @HashArchivo, RutaArchivo = @RutaArchivo, TotalContribuyentes = @Total, FechaRegistro = SYSDATETIME()
                    WHERE CodNota = @CodNota;
                    IF @@ROWCOUNT = 0
                        INSERT INTO dbo.PublicacionDOF (CodNota, Titulo, FechaPublicacion, Edicion, Organismo, Articulo, UrlNota, HashArchivo, RutaArchivo, TotalContribuyentes, FechaRegistro)
                        VALUES (@CodNota, @Titulo, @FechaPublicacion, @Edicion, @Organismo, @Articulo, @UrlNota, @HashArchivo, @RutaArchivo, @Total, SYSDATETIME());
                    """, tx);
                P(pubCmd, "@CodNota", SqlDbType.BigInt, n.CodNota);
                P(pubCmd, "@Titulo", SqlDbType.NVarChar, n.Titulo.Length > 1000 ? n.Titulo[..1000] : n.Titulo, 1000);
                P(pubCmd, "@FechaPublicacion", SqlDbType.Date, n.Fecha);
                P(pubCmd, "@Edicion", SqlDbType.VarChar, (object?)n.Edicion ?? DBNull.Value, 50);
                P(pubCmd, "@Organismo", SqlDbType.NVarChar, (object?)n.Organismo ?? DBNull.Value, 500);
                P(pubCmd, "@Articulo", SqlDbType.VarChar, n.Articulo, 16);
                P(pubCmd, "@UrlNota", SqlDbType.NVarChar, n.Url, 2048);
                P(pubCmd, "@HashArchivo", SqlDbType.VarChar, (object?)n.Hash ?? DBNull.Value, 64);
                P(pubCmd, "@RutaArchivo", SqlDbType.NVarChar, (object?)n.RutaArchivo ?? DBNull.Value, 2048);
                P(pubCmd, "@Total", SqlDbType.Int, n.Total);
                await pubCmd.ExecuteNonQueryAsync(ct);
            }
            Sub($"f) Metadata de {notes.Count} nota(s) en dbo.PublicacionDOF");
        }

        return resumen;
    }

    /// <summary>
    /// Consulta consolidada (sin historial): una publicación del listado "publicaciones-dof" que ya está cubierta por una
    /// publicación del SAT del mismo artículo con fecha efectiva igual o más reciente es la misma información; se omite
    /// y su nota DOF se agrega como referencia a la fila del SAT. Si el DOF es más reciente que el SAT, se conserva.
    /// </summary>
    private static List<RfcPublicacionVigente> ColapsarDofDuplicado(List<RfcPublicacionVigente> pubs)
    {
        var resultado = new List<RfcPublicacionVigente>(pubs);
        foreach (var dof in pubs.Where(p => p.Listado == "publicaciones-dof" && p.FechaEfectiva.HasValue))
        {
            var sat = resultado.Where(p => p.Listado != "publicaciones-dof" && p.Articulo == dof.Articulo
                                           && p.FechaEfectiva.HasValue && p.FechaEfectiva >= dof.FechaEfectiva)
                               .OrderByDescending(p => p.FechaEfectiva).FirstOrDefault();
            if (sat is null) continue;
            resultado.Remove(dof);
            var i = resultado.IndexOf(sat);
            var mergedDof = sat.Dof ?? dof.Dof;
            resultado[i] = sat with {
                Dof = mergedDof,
                NumeroOficioDOF = sat.NumeroOficioDOF ?? dof.NumeroOficioDOF,
                FechaOficioDOF = sat.FechaOficioDOF ?? dof.FechaOficioDOF
            };
        }
        return resultado;
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
        using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx)
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

    public async IAsyncEnumerable<GlobalContribuyenteDto> ConsultarGlobalVigentesAsync(
        string? articulo, string? listado, string? situacion, [EnumeratorCancellation] CancellationToken ct, bool ordenarPorJerarquia = false)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, "dbo.ConsultarContribuyentesVigentes");
        cmd.CommandType = CommandType.StoredProcedure;
        P(cmd, "@Articulo", SqlDbType.VarChar, articulo, 16);
        P(cmd, "@Listado", SqlDbType.VarChar, listado, 100);
        P(cmd, "@Situacion", SqlDbType.NVarChar, situacion, 200);
        P(cmd, "@FiltrarRfc", SqlDbType.Bit, false);
        P(cmd, "@OrdenarPorJerarquia", SqlDbType.Bit, ordenarPorJerarquia);
        var table = new DataTable();
        table.Columns.Add("Rfc", typeof(string));
        table.Columns.Add("OrdenEntrada", typeof(int));
        var p = cmd.Parameters.Add("@Rfcs", SqlDbType.Structured);
        p.TypeName = "dbo.RfcInput";
        p.Value = table;

        await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await r.ReadAsync(ct))
        {
            var rfc = r.GetString(1);
            var razon = r.GetString(2);
            var art = r.GetString(3);
            var list = r.GetString(4);
            var sit = r.GetString(5);
            var tipoId = CatalogoTipos.ResolverTipoId(art, list, sit);

            var tituloDof = r.FieldCount > 41 ? GetString(r, 41) : null;
            var edicionDof = r.FieldCount > 42 ? GetString(r, 42) : null;
            var codDiarioDof = r.FieldCount > 43 ? GetInt64(r, 43) : null;
            var fechaPubDof = r.FieldCount > 44 ? GetDateOnly(r, 44) : null;

            yield return new GlobalContribuyenteDto(
                ContribuyenteId: Convert.ToInt64(r.GetValue(0)),
                TipoId: tipoId,
                Rfc: rfc,
                RazonSocial: razon,
                Articulo: art,
                Listado: list,
                Situacion: sit,
                FechaEfectiva: GetDateOnly(r, 6),
                OrigenUltimaModificacion: r.GetString(7),
                FechaModificacion: r.GetDateTime(8),
                TipoPersona: GetString(r, 9),
                EntidadFederativa: GetString(r, 10),
                Monto: GetDecimal(r, 11),
                Ejercicio: GetString(r, 12),
                Periodo: GetString(r, 13),
                Motivo: GetString(r, 14),
                FechaCancelacion: GetDateOnly(r, 15),
                FechaAutorizacion: GetDateOnly(r, 16),
                Administracion: GetString(r, 17),
                NumeroOficioSAT: GetString(r, 18),
                FechaOficioSAT: GetDateOnly(r, 19),
                NumeroOficioDOF: GetString(r, 20),
                FechaOficioDOF: GetDateOnly(r, 21),
                PresuncionOficioSAT: GetString(r, 22),
                PresuncionFechaSAT: GetDateOnly(r, 23),
                PresuncionOficioDOF: GetString(r, 24),
                PresuncionFechaDOF: GetDateOnly(r, 25),
                DesvirtuadoOficioSAT: GetString(r, 26),
                DesvirtuadoFechaSAT: GetDateOnly(r, 27),
                DesvirtuadoOficioDOF: GetString(r, 28),
                DesvirtuadoFechaDOF: GetDateOnly(r, 29),
                DefinitivoOficioSAT: GetString(r, 30),
                DefinitivoFechaSAT: GetDateOnly(r, 31),
                DefinitivoOficioDOF: GetString(r, 32),
                DefinitivoFechaDOF: GetDateOnly(r, 33),
                SentenciaOficioSAT: GetString(r, 34),
                SentenciaFechaSAT: GetDateOnly(r, 35),
                SentenciaOficioDOF: GetString(r, 36),
                SentenciaFechaDOF: GetDateOnly(r, 37),
                CodNotaDOF: GetInt64(r, 38),
                UrlOrigenDOF: GetString(r, 39),
                UltimaDescarga: r.IsDBNull(40) ? null : r.GetDateTime(40),
                TituloDOF: tituloDof,
                EdicionDOF: edicionDof,
                CodDiarioDOF: codDiarioDof,
                FechaPublicacionDOF: fechaPubDof
            );
        }
    }

    private static DateOnly? GetDateOnly(SqlDataReader r, int i) => r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i));
    private static decimal? GetDecimal(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
    private static long? GetInt64(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));
    private static string? GetString(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public async Task<RfcEstadoResponse> ConsultarEstadoRfcsAsync(string[] rfcs, string? articulo, string? listado, CancellationToken ct)
    {
        var normalizedRfcs = Rfc.ValidateMany(rfcs);
        var table = new DataTable();
        table.Columns.Add("Rfc", typeof(string));
        table.Columns.Add("OrdenEntrada", typeof(int));
        for (var i = 0; i < normalizedRfcs.Length; i++) table.Rows.Add(normalizedRfcs[i], i);

        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, "dbo.ConsultarContribuyentesVigentes");
        cmd.CommandType = CommandType.StoredProcedure;
        P(cmd, "@Articulo", SqlDbType.VarChar, articulo, 16);
        P(cmd, "@Listado", SqlDbType.VarChar, listado, 100);
        P(cmd, "@Situacion", SqlDbType.NVarChar, null);
        P(cmd, "@FiltrarRfc", SqlDbType.Bit, true);
        P(cmd, "@OrdenarPorJerarquia", SqlDbType.Bit, false);
        var p = cmd.Parameters.Add("@Rfcs", SqlDbType.Structured);
        p.TypeName = "dbo.RfcInput";
        p.Value = table;

        var publicacionesPorRfc = new Dictionary<string, List<RfcPublicacionVigente>>(StringComparer.OrdinalIgnoreCase);
        var razonSocialPorRfc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using (var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
        {
            while (await r.ReadAsync(ct))
            {
                var rfc = r.GetString(1);
                var razon = r.GetString(2);
                if (!razonSocialPorRfc.ContainsKey(rfc)) razonSocialPorRfc[rfc] = razon;

                var art = r.GetString(3);
                var list = r.GetString(4);
                var sit = r.GetString(5);
                var fechaEfectiva = GetDateOnly(r, 6);
                var origen = r.GetString(7);
                var fechaMod = r.GetDateTime(8);
                var tipoPersona = GetString(r, 9);
                var entidad = GetString(r, 10);
                var monto = GetDecimal(r, 11);
                var ejercicio = GetString(r, 12);
                var periodo = GetString(r, 13);
                var motivo = GetString(r, 14);
                var fechaCancel = GetDateOnly(r, 15);
                var fechaAuto = GetDateOnly(r, 16);
                var admin = GetString(r, 17);
                var oficioSat = GetString(r, 18);
                var fechaSat = GetDateOnly(r, 19);
                var oficioDof = GetString(r, 20);
                var fechaDof = GetDateOnly(r, 21);

                var presOficioSat = GetString(r, 22);
                var presFechaSat = GetDateOnly(r, 23);
                var presOficioDof = GetString(r, 24);
                var presFechaDof = GetDateOnly(r, 25);

                var desvOficioSat = GetString(r, 26);
                var desvFechaSat = GetDateOnly(r, 27);
                var desvOficioDof = GetString(r, 28);
                var desvFechaDof = GetDateOnly(r, 29);

                var defOficioSat = GetString(r, 30);
                var defFechaSat = GetDateOnly(r, 31);
                var defOficioDof = GetString(r, 32);
                var defFechaDof = GetDateOnly(r, 33);

                var sentOficioSat = GetString(r, 34);
                var sentFechaSat = GetDateOnly(r, 35);
                var sentOficioDof = GetString(r, 36);
                var sentFechaDof = GetDateOnly(r, 37);

                var codNota = GetInt64(r, 38);
                var urlOrigen = GetString(r, 39);

                var tituloDof = r.FieldCount > 41 ? GetString(r, 41) : null;
                var edicionDof = r.FieldCount > 42 ? GetString(r, 42) : null;
                var codDiarioDof = r.FieldCount > 43 ? GetInt64(r, 43) : null;
                var fechaPubDof = r.FieldCount > 44 ? GetDateOnly(r, 44) : null;

                DetalleDOF? dofInfo = (codNota.HasValue || !string.IsNullOrEmpty(urlOrigen) || !string.IsNullOrEmpty(tituloDof))
                    ? new DetalleDOF(codNota, tituloDof, edicionDof, codDiarioDof, fechaPubDof, urlOrigen)
                    : null;

                var etapas = new RfcEtapas(
                    new EtapaOficio(presOficioSat, presFechaSat, presOficioDof, presFechaDof),
                    new EtapaOficio(desvOficioSat, desvFechaSat, desvOficioDof, desvFechaDof),
                    new EtapaOficio(defOficioSat, defFechaSat, defOficioDof, defFechaDof),
                    new EtapaOficio(sentOficioSat, sentFechaSat, sentOficioDof, sentFechaDof)
                );

                var tipoId = CatalogoTipos.ResolverTipoId(art, list, sit);
                var artId = CatalogoTipos.ObtenerArticuloId(art);
                var listId = CatalogoTipos.ResolverListadoId(art, list);

                var pub = new RfcPublicacionVigente(
                    ArticuloId: artId,
                    Articulo: art,
                    ListadoId: listId,
                    Listado: list,
                    Situacion: sit,
                    FechaEfectiva: fechaEfectiva,
                    Origen: origen,
                    TipoPersona: tipoPersona,
                    EntidadFederativa: entidad,
                    Monto: monto,
                    Ejercicio: ejercicio,
                    Periodo: periodo,
                    Motivo: motivo,
                    FechaCancelacion: fechaCancel,
                    FechaAutorizacion: fechaAuto,
                    Administracion: admin,
                    NumeroOficioSAT: oficioSat,
                    FechaOficioSAT: fechaSat,
                    NumeroOficioDOF: oficioDof,
                    FechaOficioDOF: fechaDof,
                    Etapas: etapas,
                    Dof: dofInfo,
                    FechaModificacion: fechaMod,
                    TipoId: tipoId
                );

                if (!publicacionesPorRfc.TryGetValue(rfc, out var lista))
                {
                    lista = [];
                    publicacionesPorRfc[rfc] = lista;
                }
                lista.Add(pub);
            }
        }

        var contribuyentes = new List<RfcEstadoContribuyente>(normalizedRfcs.Length);
        var encontrados = 0;

        foreach (var rfc in normalizedRfcs)
        {
            if (publicacionesPorRfc.TryGetValue(rfc, out var pubsCrudas) && pubsCrudas.Count > 0)
            {
                var pubs = ColapsarDofDuplicado(pubsCrudas);
                encontrados++;
                var arts = pubs.Select(p => p.Articulo).Distinct().OrderBy(a => a).ToArray();
                var reciente = pubs.Where(p => p.FechaEfectiva.HasValue).MaxBy(p => p.FechaEfectiva);
                var fechaMasReciente = reciente?.FechaEfectiva;
                var origenMasReciente = reciente?.Origen ?? pubs.MaxBy(p => p.FechaModificacion)?.Origen;

                var esDef = pubs.Any(p => p.Articulo == "69-b" && TextKey.Normalize(p.Situacion).Contains("DEFINITIVO"));
                var esPres = pubs.Any(p => p.Articulo == "69-b" && TextKey.Normalize(p.Situacion).Contains("PRESUNTO"));
                var esDesv = pubs.Any(p => p.Articulo == "69-b" && TextKey.Normalize(p.Situacion).Contains("DESVIRTUADO"));
                var esSent = pubs.Any(p => p.Articulo == "69-b" && TextKey.Normalize(p.Situacion).Contains("SENTENCIA"));
                var esCanc = pubs.Any(p => p.Articulo == "69" && p.Listado.StartsWith("cancelados"));
                var esCond = pubs.Any(p => p.Articulo == "69" && p.Listado.StartsWith("condonados"));
                var esExig = pubs.Any(p => p.Articulo == "69" && p.Listado == "exigibles");
                var esFirm = pubs.Any(p => p.Articulo == "69" && p.Listado == "firmes");
                var esNoLoc = pubs.Any(p => p.Articulo == "69" && p.Listado == "no-localizados");

                string riesgo;
                if (esDef || pubs.Any(p => p.Articulo == "69-b-bis")) riesgo = "Critico";
                else if (esPres) riesgo = "Presunto";
                else if (esSent || esDesv) riesgo = "Favorable";
                else if (esCanc || esCond || esExig || esFirm || esNoLoc || pubs.Any(p => p.Articulo == "49-bis")) riesgo = "Informativo";
                else riesgo = "Limpio";

                contribuyentes.Add(new RfcEstadoContribuyente(
                    Rfc: rfc,
                    Encontrado: true,
                    RazonSocial: razonSocialPorRfc.GetValueOrDefault(rfc),
                    DiagnosticoRiesgo: riesgo,
                    Articulos: arts,
                    FechaEfectivaReciente: fechaMasReciente,
                    OrigenMasReciente: origenMasReciente,
                    EsDefinitivo: esDef,
                    EsPresunto: esPres,
                    EsDesvirtuado: esDesv,
                    EsSentenciaFavorable: esSent,
                    EsCancelado: esCanc,
                    EsCondonado: esCond,
                    EsExigible: esExig,
                    EsFirme: esFirm,
                    EsNoLocalizado: esNoLoc,
                    Apariciones: pubs
                ));
            }
            else
            {
                contribuyentes.Add(new RfcEstadoContribuyente(
                    Rfc: rfc,
                    Encontrado: false,
                    RazonSocial: null,
                    DiagnosticoRiesgo: "Limpio",
                    Articulos: [],
                    FechaEfectivaReciente: null,
                    OrigenMasReciente: null,
                    EsDefinitivo: false,
                    EsPresunto: false,
                    EsDesvirtuado: false,
                    EsSentenciaFavorable: false,
                    EsCancelado: false,
                    EsCondonado: false,
                    EsExigible: false,
                    EsFirme: false,
                    EsNoLocalizado: false,
                    Apariciones: []
                ));
            }
        }

        return new RfcEstadoResponse(
            ConsultadaEn: DateTimeOffset.Now,
            TotalSolicitados: normalizedRfcs.Length,
            TotalEncontrados: encontrados,
            TotalNoEncontrados: normalizedRfcs.Length - encontrados,
            Contribuyentes: contribuyentes
        );
    }

    public async Task<RfcHistorialResponse> ConsultarHistorialRfcsAsync(string[] rfcs, string? articulo, string? listado, CancellationToken ct)
    {
        var normalizedRfcs = Rfc.ValidateMany(rfcs);
        var table = new DataTable();
        table.Columns.Add("Rfc", typeof(string));
        table.Columns.Add("OrdenEntrada", typeof(int));
        for (var i = 0; i < normalizedRfcs.Length; i++) table.Rows.Add(normalizedRfcs[i], i);

        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, "dbo.ConsultarHistorialRfcs");
        cmd.CommandType = CommandType.StoredProcedure;
        P(cmd, "@Articulo", SqlDbType.VarChar, articulo, 16);
        P(cmd, "@Listado", SqlDbType.VarChar, listado, 100);
        var p = cmd.Parameters.Add("@Rfcs", SqlDbType.Structured);
        p.TypeName = "dbo.RfcInput";
        p.Value = table;

        var historialPorRfc = new Dictionary<string, List<HistorialEventoDto>>(StringComparer.OrdinalIgnoreCase);
        var razonSocialPorRfc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using (var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
        {
            while (await r.ReadAsync(ct))
            {
                var id = Convert.ToInt64(r.GetValue(0));
                var rfc = r.GetString(1);
                var razon = r.GetString(2);
                if (!razonSocialPorRfc.ContainsKey(rfc)) razonSocialPorRfc[rfc] = razon;

                var art = r.GetString(3);
                var list = r.GetString(4);
                var sit = r.GetString(5);
                var fechaEf = GetDateOnly(r, 6);
                var evento = r.GetString(7);
                var origen = r.GetString(8);
                var fechaReg = r.GetDateTime(9);
                var codNota = GetInt64(r, 10);
                var url = GetString(r, 11);
                var ofSat = GetString(r, 12);
                var fSat = GetDateOnly(r, 13);
                var ofDof = GetString(r, 14);
                var fDof = GetDateOnly(r, 15);
                var ent = GetString(r, 16);
                var mot = GetString(r, 17);

                var titDof = r.FieldCount > 18 ? GetString(r, 18) : null;
                var edDof = r.FieldCount > 19 ? GetString(r, 19) : null;
                var diaDof = r.FieldCount > 20 ? GetInt64(r, 20) : null;
                var fPubDof = r.FieldCount > 21 ? GetDateOnly(r, 21) : null;

                DetalleDOF? dofInfo = (codNota.HasValue || !string.IsNullOrEmpty(url) || !string.IsNullOrEmpty(titDof))
                    ? new DetalleDOF(codNota, titDof, edDof, diaDof, fPubDof, url)
                    : null;

                var tipoId = CatalogoTipos.ResolverTipoId(art, list, sit);
                var artId = CatalogoTipos.ObtenerArticuloId(art);
                var listId = CatalogoTipos.ResolverListadoId(art, list);

                var dto = new HistorialEventoDto(
                    HistorialId: id,
                    ArticuloId: artId,
                    Articulo: art,
                    ListadoId: listId,
                    Listado: list,
                    Situacion: sit,
                    Fecha: fechaEf,
                    Origen: origen,
                    FechaRegistro: fechaReg,
                    TipoEvento: evento,
                    Dof: dofInfo,
                    NumeroOficioSAT: ofSat,
                    FechaOficioSAT: fSat,
                    NumeroOficioDOF: ofDof,
                    FechaOficioDOF: fDof,
                    EntidadFederativa: ent,
                    Motivo: mot
                );

                if (!historialPorRfc.TryGetValue(rfc, out var lista))
                {
                    lista = [];
                    historialPorRfc[rfc] = lista;
                }
                lista.Add(dto);
            }
        }

        var resultado = new List<RfcHistorialContribuyente>(normalizedRfcs.Length);
        foreach (var rfc in normalizedRfcs)
        {
            var eventos = historialPorRfc.GetValueOrDefault(rfc) ?? [];
            var primer = eventos.FirstOrDefault(e => e.Fecha.HasValue)?.Fecha;
            var ultimo = eventos.LastOrDefault(e => e.Fecha.HasValue)?.Fecha;

            resultado.Add(new RfcHistorialContribuyente(
                Rfc: rfc,
                RazonSocial: razonSocialPorRfc.GetValueOrDefault(rfc),
                TotalMovimientos: eventos.Count,
                PrimerMovimiento: primer,
                UltimoMovimiento: ultimo,
                Movimientos: eventos
            ));
        }

        var encontrados = resultado.Count(c => c.TotalMovimientos > 0);
        return new RfcHistorialResponse(
            ConsultadaEn: DateTimeOffset.Now,
            TotalSolicitados: normalizedRfcs.Length,
            TotalEncontrados: encontrados,
            Contribuyentes: resultado
        );
    }

    public async Task<DofSyncResponse> SincronizarDofPeriodoAsync(
        DateOnly inicio, DateOnly fin, string? articuloFiltro,
        SafeDownloader downloader, AgentOptions options, CancellationToken ct,
        Func<int, int, string, Task>? onProgress = null)
    {
        const string baseUrl = "https://sidof.segob.gob.mx/dof/sidof";
        var totalDias = fin.DayNumber - inicio.DayNumber + 1;
        var filtro = string.IsNullOrWhiteSpace(articuloFiltro) || articuloFiltro.Equals("todos", StringComparison.OrdinalIgnoreCase)
            ? null
            : articuloFiltro.Trim().ToLowerInvariant();
        var advertencias = new List<string>();
        var detalleNotas = new List<DofSyncDetalleNota>();
        var notas = new List<DofNoteMeta>();
        var staging = CreateDofStagingTable();
        var diasConError = 0;
        var diaActual = 0;

        logger?.LogInformation("[DOF-API 1/4] Consulta SIDOF {Inicio:yyyy-MM-dd}..{Fin:yyyy-MM-dd} ({Dias} día(s)); artículo={Articulo}; carpeta de evidencia={Dir}",
            inicio, fin, totalDias, filtro ?? "todos", options.DataDirectory);

        for (var fecha = inicio; fecha <= fin; fecha = fecha.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            diaActual++;
            if (onProgress is not null) await onProgress(diaActual, totalDias, $"Consultando SIDOF del {fecha:yyyy-MM-dd}; {staging.Rows.Count:N0} fila(s) acumuladas");
            string content;
            try
            {
                content = await downloader.TextAsync($"{baseUrl}/notas/{fecha:dd-MM-yyyy}", options, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                diasConError++;
                var motivo = SafeError.Describe(ex);
                logger?.LogError(ex, "[DOF-API 1/4] Día {N}/{Total} {Fecha:yyyy-MM-dd}: no se pudo consultar SIDOF.", diaActual, totalDias, fecha);
                advertencias.Add($"{fecha:yyyy-MM-dd}: no se pudo consultar SIDOF ({motivo}).");
                continue;
            }

            using var response = JsonDocument.Parse(content);
            if (DofCollector.String(response.RootElement, "messageCode") != "200")
            {
                diasConError++;
                logger?.LogWarning("[DOF-API 1/4] Día {N}/{Total} {Fecha:yyyy-MM-dd}: SIDOF respondió messageCode distinto de 200.", diaActual, totalDias, fecha);
                advertencias.Add($"{fecha:yyyy-MM-dd}: SIDOF no confirmó una consulta exitosa.");
                continue;
            }

            var seenNotes = new HashSet<long>();
            var candidatas = 0;
            var conRegistros = 0;
            foreach (var group in new[] { "NotasMatutinas", "NotasVespertinas", "NotasExtraordinarias" })
            {
                if (!response.RootElement.TryGetProperty(group, out var notes) || notes.ValueKind != JsonValueKind.Array) continue;
                foreach (var note in notes.EnumerateArray())
                {
                    var agency = DofCollector.String(note, "codOrgaDos");
                    var normAgency = TextKey.Normalize(agency);
                    var normTitle = TextKey.Normalize(DofCollector.String(note, "titulo"));
                    if (!normAgency.Contains("HACIENDA") && !normAgency.Contains("SAT") && !normTitle.Contains("69") && !normTitle.Contains("CONTRIBUYENTE")) continue;
                    if (!long.TryParse(DofCollector.String(note, "codNota"), out var codNota) || !seenNotes.Add(codNota)) continue;
                    candidatas++;

                    try
                    {
                        var download = await downloader.FetchAsync($"{baseUrl}/notas/nota/{codNota}", options, ct);
                        using var detail = JsonDocument.Parse(await File.ReadAllTextAsync(download.Path, ct));
                        if (DofCollector.String(detail.RootElement, "messageCode") != "200" || !detail.RootElement.TryGetProperty("Nota", out var val))
                        {
                            advertencias.Add($"Nota {codNota}: SIDOF no devolvió el detalle.");
                            continue;
                        }

                        var title = DofCollector.String(val, "titulo");
                        var html = DofCollector.String(val, "cadenaContenido");
                        if (!DateOnly.TryParseExact(DofCollector.String(val, "fecha"), "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var pubDate))
                        {
                            advertencias.Add($"Nota {codNota}: fecha de publicación inválida.");
                            continue;
                        }

                        var parsed = DofParser.Parse(title, html, pubDate);
                        if (parsed.Rows.Count == 0)
                        {
                            if (parsed.Relevant) advertencias.Add($"Nota {codNota}: parece relevante pero no se extrajeron filas; requiere revisión.");
                            continue;
                        }

                        var filas = parsed.Rows.Where(r => filtro is null || string.Equals(r.Article, filtro, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (filas.Count == 0) continue;

                        conRegistros++;
                        detalleNotas.Add(new DofSyncDetalleNota(codNota, pubDate, title, filas.Count));
                        var edicion = DofCollector.String(val, "codEdicion");
                        notas.Add(new DofNoteMeta(codNota, string.IsNullOrWhiteSpace(title) ? "Publicación DOF" : title, pubDate,
                            string.IsNullOrWhiteSpace(edicion) ? null : edicion, agency, filas[0].Article, download.Url, download.Hash, download.Path, filas.Count));
                        foreach (var row in filas) AddDofStagingRow(staging, row, codNota, download.Url);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "[DOF-API 1/4] Fallo al procesar la nota DOF {CodNota}", codNota);
                        advertencias.Add($"Nota {codNota}: {SafeError.Describe(ex)}");
                    }
                }
            }
            logger?.LogInformation("[DOF-API 1/4] Día {N}/{Total} {Fecha:yyyy-MM-dd}: {Candidatas} nota(s) candidata(s), {Con} con registros del filtro. Acumulado: {Filas:N0} fila(s).",
                diaActual, totalDias, fecha, candidatas, conRegistros, staging.Rows.Count);
        }

        var resumenList = new List<DofSyncResumenTipo>();
        var totalFilas = staging.Rows.Count;

        if (totalFilas > 0)
        {
            if (onProgress is not null) await onProgress(totalDias, totalDias, $"Comparando {totalFilas:N0} fila(s) en SQL Server");
            logger?.LogInformation("[DOF-API 2/4] Comparativa masiva en SQL Server de {Filas:N0} fila(s) y {Notas} nota(s) (carga bulk + sentencias set-based)...", totalFilas, notas.Count);
            await using var c = await OpenAsync(ct);
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
            // Exclusión mutua en la BD con cualquier otra ingesta (Worker u otra llamada a la API); se libera al confirmar.
            using (var applock = Command(c, "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='MonitorRFC.ingestion',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=90000; SELECT @r;", tx))
            {
                logger?.LogInformation("[DOF-API 2/4] Solicitando bloqueo de ingesta (espera máx. 90 s)...");
                if (Convert.ToInt32(await applock.ExecuteScalarAsync(ct)) < 0)
                    throw new InvalidOperationException("Hay otra ingesta en curso (Worker u otra sincronización); reintente en unos minutos.");
            }
            resumenList.AddRange(await WithHeartbeatAsync("Comparativa masiva DOF (API)", () => ApplyDofStagingAsync(c, tx, staging, notas, ct), ct));
            logger?.LogInformation("[DOF-API 3/4] Confirmando transacción...");
            await tx.CommitAsync(ct);
        }
        logger?.LogInformation("[DOF-API 4/4] Finalizado: {Filas:N0} fila(s), {Notas} nota(s), {Adv} advertencia(s).", totalFilas, notas.Count, advertencias.Count);

        var estado = totalFilas > 0 ? "completado"
            : diasConError >= totalDias ? "error_fuente"
            : advertencias.Count > 0 ? "sin_registros_con_advertencias"
            : "sin_registros";
        var mensaje = estado switch
        {
            "completado" => $"Sincronización finalizada exitosamente: {totalFilas} registros procesados en {detalleNotas.Count} nota(s) fiscal(es).",
            "error_fuente" => "No fue posible consultar SIDOF para ningún día del periodo; revise 'advertencias' y la bitácora de la API.",
            "sin_registros_con_advertencias" => "No se encontraron registros, pero hubo advertencias durante la consulta; revise 'advertencias'.",
            _ => "No se encontraron nuevas notas fiscales ni registros para el periodo especificado."
        };

        return new DofSyncResponse(
            Estado: estado,
            Mensaje: mensaje,
            Periodo: new { fechaInicio = inicio.ToString("yyyy-MM-dd"), fechaFin = fin.ToString("yyyy-MM-dd") },
            FiltroArticulo: articuloFiltro,
            DiasConsultados: totalDias,
            NotasFiscalesEncontradas: detalleNotas.Count,
            TotalRegistrosProcesados: totalFilas,
            ResumenPorTipo: resumenList,
            DetalleNotas: detalleNotas,
            Advertencias: advertencias
        );
    }

    // ------------------------------------------------------------------------
    // Trabajos de sincronización DOF on-demand (estado persistente en dbo.TrabajoDOF)
    // ------------------------------------------------------------------------

    public async Task EnsureJobTableAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TrabajoDOF' AND schema_id = SCHEMA_ID('dbo'))
                CREATE TABLE dbo.TrabajoDOF (
                    TrabajoId uniqueidentifier NOT NULL PRIMARY KEY,
                    FechaInicio date NOT NULL, FechaFin date NOT NULL, Articulo varchar(16) NULL,
                    Estado varchar(20) NOT NULL CHECK (Estado IN ('en_cola', 'en_curso', 'completado', 'error', 'interrumpido')),
                    DiaActual int NOT NULL DEFAULT 0, TotalDias int NOT NULL DEFAULT 0,
                    Mensaje nvarchar(1000) NULL, ResultadoJson nvarchar(max) NULL, Error nvarchar(2000) NULL,
                    FechaCreacion datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
                    FechaInicioProceso datetime2(3) NULL, FechaTermino datetime2(3) NULL,
                    FechaActualizacion datetime2(3) NOT NULL DEFAULT SYSDATETIME());
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Al arrancar la API, lo que haya quedado en cola o en curso se marca como interrumpido (el proceso anterior terminó).</summary>
    public async Task<int> MarkInterruptedJobsAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, """
            UPDATE dbo.TrabajoDOF
            SET Estado = 'interrumpido', FechaTermino = SYSDATETIME(), FechaActualizacion = SYSDATETIME(),
                Mensaje = N'La API se reinició antes de terminar; vuelva a lanzar el mismo rango (los datos ya guardados no se duplican).'
            WHERE Estado IN ('en_cola', 'en_curso');
            """);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CreateJobAsync(Guid id, DateOnly inicio, DateOnly fin, string? articulo, int totalDias, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, """
            INSERT dbo.TrabajoDOF(TrabajoId, FechaInicio, FechaFin, Articulo, Estado, TotalDias, Mensaje)
            VALUES(@Id, @Inicio, @Fin, @Articulo, 'en_cola', @Total, N'En cola.');
            """);
        P(cmd, "@Id", SqlDbType.UniqueIdentifier, id);
        P(cmd, "@Inicio", SqlDbType.Date, inicio);
        P(cmd, "@Fin", SqlDbType.Date, fin);
        P(cmd, "@Articulo", SqlDbType.VarChar, articulo, 16);
        P(cmd, "@Total", SqlDbType.Int, totalDias);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateJobAsync(Guid id, string estado, int dia, int total, string? mensaje, string? resultadoJson, string? error, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, """
            UPDATE dbo.TrabajoDOF
            SET Estado = @Estado, DiaActual = @Dia, TotalDias = @Total, Mensaje = @Mensaje,
                ResultadoJson = COALESCE(@Resultado, ResultadoJson), Error = @Error,
                FechaInicioProceso = CASE WHEN @Estado = 'en_curso' AND FechaInicioProceso IS NULL THEN SYSDATETIME() ELSE FechaInicioProceso END,
                FechaTermino = CASE WHEN @Estado IN ('completado', 'error', 'interrumpido') THEN SYSDATETIME() ELSE FechaTermino END,
                FechaActualizacion = SYSDATETIME()
            WHERE TrabajoId = @Id;
            """);
        P(cmd, "@Id", SqlDbType.UniqueIdentifier, id);
        P(cmd, "@Estado", SqlDbType.VarChar, estado, 20);
        P(cmd, "@Dia", SqlDbType.Int, dia);
        P(cmd, "@Total", SqlDbType.Int, total);
        P(cmd, "@Mensaje", SqlDbType.NVarChar, mensaje is { Length: > 1000 } ? mensaje[..1000] : mensaje, 1000);
        P(cmd, "@Resultado", SqlDbType.NVarChar, resultadoJson, -1);
        P(cmd, "@Error", SqlDbType.NVarChar, error is { Length: > 2000 } ? error[..2000] : error, 2000);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DofJobStatus?> GetJobAsync(Guid id, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, """
            SELECT TrabajoId, Estado, FechaInicio, FechaFin, Articulo, DiaActual, TotalDias, Mensaje, Error, ResultadoJson,
                   FechaCreacion, FechaInicioProceso, FechaTermino
            FROM dbo.TrabajoDOF WHERE TrabajoId = @Id;
            """);
        P(cmd, "@Id", SqlDbType.UniqueIdentifier, id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;

        var estado = r.GetString(1);
        var dia = r.GetInt32(5);
        var total = r.GetInt32(6);
        var porcentaje = estado == "completado" ? 100 : total <= 0 ? 0 : Math.Min(99, (int)Math.Round(100.0 * Math.Max(0, dia - 1) / total));
        DofSyncResponse? resultado = null;
        if (!r.IsDBNull(9))
        {
            try { resultado = JsonSerializer.Deserialize<DofSyncResponse>(r.GetString(9), Json); }
            catch (JsonException ex) { logger?.LogWarning(ex, "No se pudo leer el resultado guardado del trabajo DOF {Id}", id); }
        }
        DateTimeOffset? Local(int i) => r.IsDBNull(i) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Local));
        return new DofJobStatus(r.GetGuid(0), estado, DateOnly.FromDateTime(r.GetDateTime(2)), DateOnly.FromDateTime(r.GetDateTime(3)),
            GetString(r, 4), dia, total, porcentaje, GetString(r, 7), GetString(r, 8),
            Local(10)!.Value, Local(11), Local(12), resultado);
    }
}
