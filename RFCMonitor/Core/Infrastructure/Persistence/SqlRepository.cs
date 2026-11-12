using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using MonitorRFC.Application;
using MonitorRFC.Contracts;
using MonitorRFC.Domain;
using MonitorRFC.Infrastructure.Configuration;

namespace MonitorRFC.Infrastructure.Persistence;

/// <summary>Immutable publication versions and generations. No generation is deleted while clients may read it.</summary>
public sealed class SqlRepository : IPublicationRepository, IRfcQueryService
{
    private readonly Func<string> connectionString;
    private readonly ILogger? logger;
    public SqlRepository(AgentConfiguration configuration, ILogger? logger = null) { this.connectionString = () => AgentConfiguration.GetConnection(configuration.Load()); this.logger = logger; }
    public SqlRepository(string connectionString, ILogger? logger = null) { this.connectionString = () => connectionString; this.logger = logger; }
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
        new(text, connection, transaction) { CommandTimeout = 300 };
    private static void P(SqlCommand cmd, string name, SqlDbType type, object? value, int size = 0)
    {
        var p = size == 0 ? cmd.Parameters.Add(name, type) : cmd.Parameters.Add(name, type, size);
        p.Value = value switch { null => DBNull.Value, DateOnly d => d.ToDateTime(TimeOnly.MinValue), DateTimeOffset d => d.LocalDateTime, _ => value };
    }
    private SqlConnection Writer => leaseConnection is { State: ConnectionState.Open } c ? c :
        throw new InvalidOperationException("La operación de ingesta requiere el lease exclusivo activo.");
    public async Task<IAsyncDisposable?> AcquireLeaseAsync(CancellationToken ct)
    {
        if (leaseConnection is not null) throw new InvalidOperationException("Lease ya adquirido en esta instancia.");
        var c = await OpenAsync(ct);
        try
        {
            using var get = Command(c, "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='MonitorRFC.ingestion',@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0; SELECT @r;");
            if (Convert.ToInt32(await get.ExecuteScalarAsync(ct)) < 0) { await c.DisposeAsync(); return null; }
            owner = Guid.NewGuid();
            using var fence = Command(c, """
                SET XACT_ABORT ON; BEGIN TRANSACTION;
                UPDATE dbo.Lease WITH(UPDLOCK,HOLDLOCK) SET Propietario=@Owner,FencingToken=FencingToken+1,Expira=CONVERT(datetime2,'9999-12-31') WHERE Recurso='ingestion';
                IF @@ROWCOUNT=0 INSERT dbo.Lease(Recurso,Propietario,FencingToken,Expira) VALUES('ingestion',@Owner,1,CONVERT(datetime2,'9999-12-31'));
                SELECT FencingToken FROM dbo.Lease WHERE Recurso='ingestion'; COMMIT;
                """);
            P(fence, "@Owner", SqlDbType.UniqueIdentifier, owner);
            token = Convert.ToInt64(await fence.ExecuteScalarAsync(ct));
            leaseConnection = c;
            return new SessionLease(this, c);
        }
        catch { await c.DisposeAsync(); throw; }
    }
    // The SQL session owns the lock. A disconnected session loses it immediately; no timer-based lease can overlap.
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
                    using var cmd = Command(connection,"UPDATE dbo.Lease SET Expira=SYSDATETIME() WHERE Recurso='ingestion' AND Propietario=@Owner; EXEC sys.sp_releaseapplock @Resource='MonitorRFC.ingestion',@LockOwner='Session';");
                    P(cmd,"@Owner",SqlDbType.UniqueIdentifier,repository.owner);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            finally { await connection.DisposeAsync(); }
        }
    }
    private async Task AssertLeaseAsync(SqlTransaction transaction, CancellationToken ct)
    {
        using var cmd = Command(Writer,"""
            IF APPLOCK_MODE('public','MonitorRFC.ingestion','Session')<>'Exclusive'
             OR NOT EXISTS(SELECT 1 FROM dbo.Lease WITH(UPDLOCK,HOLDLOCK) WHERE Recurso='ingestion' AND Propietario=@Owner AND FencingToken=@Token)
             THROW 51000,'Lease de ingesta perdido.',1;
            """,transaction);
        P(cmd,"@Owner",SqlDbType.UniqueIdentifier,owner); P(cmd,"@Token",SqlDbType.BigInt,token);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<ImportResult> ImportAsync(SourceDocument document, IAsyncEnumerable<SourceRow> rows, CancellationToken ct)
    {
        var listing = SourceCatalog.Sat.SingleOrDefault(x => x.Key == document.SourceKey);
        if (listing is null && document.SourceKey != "dof:publicaciones") throw new ArgumentException("Fuente desconocida.");
        if (document.Hash.Length != 64 || document.Hash.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Hash inválido.");
        var c = Writer;
        await using var transaction = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            await AssertLeaseAsync(transaction,ct);
            using var find = Command(c,"""
                SELECT p.PublicacionId,v.VersionId,v.HashContenido,v.ParserVersion,v.Filas,v.FilasLeidas,v.FilasRechazadas
                FROM dbo.Publicacion p WITH(UPDLOCK,HOLDLOCK)
                LEFT JOIN dbo.PublicacionVersion v ON v.VersionId=p.VersionActivaId
                WHERE p.FuenteKey=@Source AND p.ClaveExterna=@External;
                """,transaction);
            P(find,"@Source",SqlDbType.NVarChar,document.SourceKey,100); P(find,"@External",SqlDbType.NVarChar,document.ExternalKey,100);
            long publication = 0;
            await using (var r = await find.ExecuteReaderAsync(ct))
            {
                if(await r.ReadAsync(ct))
                {
                    publication = r.GetInt64(0);
                    // Compare only the active version: A -> B -> A creates a new immutable revision.
                    if(!r.IsDBNull(1) && r.GetString(2)==document.Hash && r.GetString(3)==document.ParserVersion)
                    {
                        await r.DisposeAsync();
                        var unchanged = new ImportResult(r.GetInt64(1),false,r.GetInt64(4),r.GetInt64(5),r.GetInt64(6),
                            await CertifiedRowsAsync(c,transaction,publication,ct));
                        await ObserveAsync(c,transaction,unchanged.VersionId,document,true,ct);
                        await transaction.CommitAsync(ct); return unchanged;
                    }
                }
            }
            if(publication == 0)
            {
                using var insert=Command(c,"INSERT dbo.Publicacion(FuenteKey,ClaveExterna,PrimeraDeteccion) OUTPUT INSERTED.PublicacionId VALUES(@Source,@External,@Detected);",transaction);
                P(insert,"@Source",SqlDbType.NVarChar,document.SourceKey,100); P(insert,"@External",SqlDbType.NVarChar,document.ExternalKey,100); P(insert,"@Detected",SqlDbType.DateTime2,document.Detected);
                publication=Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
            }
            using var create=Command(c,"""
                INSERT dbo.PublicacionVersion(PublicacionId,HashContenido,ParserVersion,Url,EvidenciaRuta,FechaFuente,AlcanceFecha,Detectada,CodNota,Titulo,Edicion,Organismo)
                OUTPUT INSERTED.VersionId VALUES(@Publication,@Hash,@Parser,@Url,@Evidence,@Date,@Scope,@Detected,@Note,@Title,@Edition,@Agency);
                """,transaction);
            P(create,"@Publication",SqlDbType.BigInt,publication); P(create,"@Hash",SqlDbType.Char,document.Hash,64); P(create,"@Parser",SqlDbType.VarChar,document.ParserVersion,40);
            P(create,"@Url",SqlDbType.NVarChar,document.Url,2048); P(create,"@Evidence",SqlDbType.NVarChar,document.EvidencePath,2048);
            P(create,"@Date",SqlDbType.Date,document.SourceDate); P(create,"@Scope",SqlDbType.VarChar,document.DateScope,30); P(create,"@Detected",SqlDbType.DateTime2,document.Detected);
            P(create,"@Note",SqlDbType.BigInt,document.NoteCode); P(create,"@Title",SqlDbType.NVarChar,document.Title,-1); P(create,"@Edition",SqlDbType.NVarChar,document.Edition,40); P(create,"@Agency",SqlDbType.NVarChar,document.Agency,500);
            var version=Convert.ToInt64(await create.ExecuteScalarAsync(ct));
            var isDocument=listing?.Document == true;
            using var batch=CreateBatch(isDocument);
            using var rawRows=CreateRawRowsBatch();
            long count=0, read=0, rejected=0;
            string? reviewDetail=null;
            await foreach(var row in rows.WithCancellation(ct))
            {
                if(isDocument)
                {
                    foreach(var cell in row.Original)
                    {
                        batch.Rows.Add(version,row.Table,row.Row,cell.Columna,Convert.ToString(row.Typed.GetValueOrDefault("hoja"))??cell.Nombre,(object?)cell.Valor??DBNull.Value);
                        count++;
                    }
                }
                else
                {
                    read++;
                    var rawRfc = row.Original.FirstOrDefault(cell => TextKey.Normalize(cell.Nombre) == "RFC")?.Valor ?? row.Rfc;
                    var rfc = Rfc.Normalize(string.IsNullOrWhiteSpace(row.Rfc) ? rawRfc : row.Rfc);
                    if (string.IsNullOrWhiteSpace(rfc)) rfc = !string.IsNullOrWhiteSpace(rawRfc) ? rawRfc.Trim() : "(SIN RFC)";
                    if (rfc.Length > 100) rfc = rfc[..100];

                    var sourceValid = SourceCatalog.Articles.Contains(row.Article) && (listing is null || (row.Article == listing.Article && row.Listing == listing.Slug));
                    rawRows.Rows.Add(version, row.Table, row.Row, (object?)rawRfc ?? DBNull.Value, true, DBNull.Value,
                        JsonSerializer.Serialize(row.Original, Json));
                    if (!sourceValid) throw new InvalidDataException($"Fila física {row.Row}: fila fuera de su fuente.");

                    var name = row.Name ?? "";
                    if (name.Length > 2000) name = name[..2000];
                    var status = row.Status ?? "";
                    if (status.Length > 2000) status = status[..2000];
                    var entity = row.Entity;
                    if (entity is not null && entity.Length > 1000) entity = entity[..1000];

                    var detail = JsonSerializer.Serialize(new { tipo = document.Origin + ":" + row.Article + ":" + row.Listing, tipado = row.Typed, camposOriginales = row.Original }, Json);
                    batch.Rows.Add(version, row.Table, row.Row, rfc, name, row.Article, row.Listing, status,
                        row.PublicationDate is { } date ? date.ToDateTime(TimeOnly.MinValue) : DBNull.Value, (object?)entity ?? DBNull.Value, detail);
                    count++;
                }
            }
            logger?.LogInformation("Iniciando inserción masiva en {Table} ({Count:N0} filas)...", isDocument ? "dbo.DocumentoCelda" : "dbo.RegistroFuente", count);
            await FlushAsync(c,transaction,batch,isDocument?"dbo.DocumentoCelda":"dbo.RegistroFuente",ct,logger);
            logger?.LogInformation("Inserción completada en {Table}.", isDocument ? "dbo.DocumentoCelda" : "dbo.RegistroFuente");

            logger?.LogInformation("Iniciando inserción masiva en dbo.FilaFuente ({Count:N0} filas)...", rawRows.Rows.Count);
            await FlushAsync(c,transaction,rawRows,"dbo.FilaFuente",ct,logger);
            logger?.LogInformation("Inserción completada en dbo.FilaFuente.");
            if((isDocument ? count : read)==0) throw new InvalidDataException("La publicación no contiene filas verificables.");
            // FilaFuente preserves every source row, including rejected rows.
            // RegistroFuente is the consolidated RFC projection for valid rows.
            using var promote=Command(c,"""
                UPDATE dbo.PublicacionVersion SET Estado='publicada',Filas=@Rows,FilasLeidas=@Read,FilasRechazadas=@Rejected,
                    Certificacion='certificada',
                    Incorporada=SYSDATETIME() WHERE VersionId=@Version;
                UPDATE dbo.Publicacion SET VersionActivaId=@Version WHERE PublicacionId=@Publication;
                """,transaction);
            P(promote,"@Rows",SqlDbType.BigInt,count); P(promote,"@Read",SqlDbType.BigInt,isDocument?count:read); P(promote,"@Rejected",SqlDbType.BigInt,rejected);
            P(promote,"@Version",SqlDbType.BigInt,version); P(promote,"@Publication",SqlDbType.BigInt,publication);
            await promote.ExecuteNonQueryAsync(ct);
            await ObserveAsync(c,transaction,version,document,false,ct);
            var certifiedRows=await CertifiedRowsAsync(c,transaction,publication,ct);
            await transaction.CommitAsync(ct);
            return new(version,true,count,isDocument?count:read,rejected,certifiedRows,reviewDetail);
        }
        catch { try { await transaction.RollbackAsync(CancellationToken.None); } catch(SqlException) { } throw; }
    }
    private static DataTable CreateBatch(bool document)
    {
        var t=new DataTable(); t.Columns.Add("VersionId",typeof(long));
        if(document)
        {
            t.Columns.Add("HojaOrdinal",typeof(int)); t.Columns.Add("FilaOrdinal",typeof(int)); t.Columns.Add("ColumnaOrdinal",typeof(int)); t.Columns.Add("Hoja",typeof(string)); t.Columns.Add("Valor",typeof(string));
        }
        else
        {
            t.Columns.Add("TablaOrdinal",typeof(int)); t.Columns.Add("FilaOrdinal",typeof(int));
            foreach(var name in new[]{"Rfc","NombreOriginal","Articulo","Listado","EstadoOriginal"})t.Columns.Add(name,typeof(string));
            t.Columns.Add("FechaPublicacion",typeof(DateTime)); t.Columns.Add("EntidadOriginal",typeof(string)); t.Columns.Add("DetalleJson",typeof(string));
        }
        return t;
    }
    private static async Task<long> CertifiedRowsAsync(SqlConnection c, SqlTransaction transaction, long publication, CancellationToken ct)
    {
        using var cmd=Command(c,"""
            SELECT TOP(1) Filas FROM dbo.PublicacionVersion
            WHERE PublicacionId=@Publication AND Certificacion='certificada' AND Estado='publicada'
            ORDER BY VersionId DESC;
            """,transaction);
        P(cmd,"@Publication",SqlDbType.BigInt,publication);
        var value=await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }
    private static DataTable CreateRawRowsBatch()
    {
        var t=new DataTable();
        t.Columns.Add("VersionId",typeof(long)); t.Columns.Add("TablaOrdinal",typeof(int)); t.Columns.Add("FilaOrdinal",typeof(int));
        t.Columns.Add("RfcOriginal",typeof(string)); t.Columns.Add("EsValida",typeof(bool)); t.Columns.Add("MotivoRechazo",typeof(string)); t.Columns.Add("DatosOriginalesJson",typeof(string));
        return t;
    }
    private static async Task ObserveAsync(SqlConnection c, SqlTransaction tx, long version, SourceDocument document, bool unchanged, CancellationToken ct)
    {
        using var cmd=Command(c,"""
            INSERT dbo.ObservacionPublicacion(VersionId,Observada,FechaFuente,AlcanceFecha,Url,SinCambios)
            VALUES(@Version,@Date,@SourceDate,@Scope,@Url,@Unchanged);
            """,tx);
        P(cmd,"@Version",SqlDbType.BigInt,version);P(cmd,"@Date",SqlDbType.DateTime2,document.Detected);
        P(cmd,"@SourceDate",SqlDbType.Date,document.SourceDate);P(cmd,"@Scope",SqlDbType.VarChar,document.DateScope,30);
        P(cmd,"@Url",SqlDbType.NVarChar,document.Url,2048);P(cmd,"@Unchanged",SqlDbType.Bit,unchanged);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task FlushAsync(SqlConnection c,SqlTransaction tx,DataTable batch,string table,CancellationToken ct, ILogger? logger = null)
    {
        if(batch.Rows.Count==0)return;
        var totalRows = batch.Rows.Count;
        using var bulk=new SqlBulkCopy(c,SqlBulkCopyOptions.TableLock,tx) 
        { 
            DestinationTableName=table,
            BatchSize=25000,
            BulkCopyTimeout=600,
            NotifyAfter=25000,
            EnableStreaming=true 
        };
        if (logger is not null)
        {
            bulk.SqlRowsCopied += (s, e) => logger.LogInformation("  [BulkCopy] {Table}: {Rows:N0} / {Total:N0} filas insertadas en BD...", table, e.RowsCopied, totalRows);
        }
        foreach(DataColumn col in batch.Columns) bulk.ColumnMappings.Add(col.ColumnName,col.ColumnName);
        await bulk.WriteToServerAsync(batch,ct); 
        batch.Clear();
    }
    public async Task<ImportResult?> CheckUnchangedByDateAsync(string sourceKey, string externalKey, DateOnly? sourceDate, string url, CancellationToken ct)
    {
        if (sourceDate is null) return null;
        var c = Writer;
        await using var transaction = (SqlTransaction)await c.BeginTransactionAsync(ct);
        await AssertLeaseAsync(transaction, ct);
        using var find = Command(c, """
            SELECT p.PublicacionId, v.VersionId, v.FechaFuente, v.Filas, v.FilasLeidas, v.FilasRechazadas
            FROM dbo.Publicacion p WITH(UPDLOCK,HOLDLOCK)
            JOIN dbo.PublicacionVersion v ON v.VersionId = p.VersionActivaId
            WHERE p.FuenteKey = @Source AND p.ClaveExterna = @External;
            """, transaction);
        P(find, "@Source", SqlDbType.NVarChar, sourceKey, 100);
        P(find, "@External", SqlDbType.NVarChar, externalKey, 100);
        await using var r = await find.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            var publicationId = r.GetInt64(0);
            var versionId = r.GetInt64(1);
            DateOnly? activeDate = r.IsDBNull(2) ? null : DateOnly.FromDateTime(r.GetDateTime(2));
            var filas = r.GetInt64(3);
            var filasLeidas = r.GetInt64(4);
            var filasRechazadas = r.GetInt64(5);
            await r.DisposeAsync();

            // Si la fecha activa existe, es posterior a 1900 y la fecha del portal no es superior (es menor o igual),
            // no se requiere descargar ni reprocesar el archivo: se registra la observación y se retorna sin cambios.
            if (activeDate is not null && activeDate.Value.Year > 1900 && sourceDate.Value <= activeDate.Value)
            {
                var certified = await CertifiedRowsAsync(c, transaction, publicationId, ct);
                using var cmd = Command(c, """
                    INSERT dbo.ObservacionPublicacion(VersionId,Observada,FechaFuente,AlcanceFecha,Url,SinCambios)
                    VALUES(@Version,SYSDATETIME(),@SourceDate,'seccion',@Url,1);
                    """, transaction);
                P(cmd, "@Version", SqlDbType.BigInt, versionId);
                P(cmd, "@SourceDate", SqlDbType.Date, sourceDate);
                P(cmd, "@Url", SqlDbType.NVarChar, url, 2048);
                await cmd.ExecuteNonQueryAsync(ct);
                await transaction.CommitAsync(ct);
                return new ImportResult(versionId, false, filas, filasLeidas, filasRechazadas, certified);
            }
        }
        await r.DisposeAsync();
        await transaction.RollbackAsync(ct);
        return null;
    }
    public async Task<long> PublishGenerationAsync(CancellationToken ct)
    {
        using var cmd=Command(Writer,"dbo.PublicarGeneracion"); cmd.CommandType=CommandType.StoredProcedure;
        P(cmd,"@Propietario",SqlDbType.UniqueIdentifier,owner); P(cmd,"@Token",SqlDbType.BigInt,token);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }
    public async Task<QueryHeader?> GetHeaderAsync(CancellationToken ct)
    {
        await using var c=await OpenAsync(ct); using var cmd=Command(c,"dbo.Encabezado"); cmd.CommandType=CommandType.StoredProcedure;
        await using var r=await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)?new(r.GetInt64(0),new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(1),DateTimeKind.Local)),r.GetString(2),r.GetString(3)):null;
    }
    public async Task<JsonElement> GetOperationalStateAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, "dbo.EstadoFuentes"); cmd.CommandType = CommandType.StoredProcedure;
        return JsonSerializer.Deserialize<JsonElement>((string)(await cmd.ExecuteScalarAsync(ct))!);
    }
    public async IAsyncEnumerable<object> ReadDocumentAsync(long generation, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        using var cmd = Command(c, "dbo.DocumentoTecnico"); cmd.CommandType = CommandType.StoredProcedure;
        P(cmd, "@GeneracionId", SqlDbType.BigInt, generation);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct)) yield return new { versionId = reader.GetInt64(0), hojaOrdinal = reader.GetInt32(1),
            fila = reader.GetInt32(2), columna = reader.GetInt32(3), hoja = reader.GetString(4), valor = reader.IsDBNull(5) ? null : reader.GetString(5) };
    }
    public async Task EnsureCatalogAsync(CancellationToken ct, AgentOptions? settings = null)
    {
        foreach (var listing in SourceCatalog.Sat)
        {
            using var cmd = Command(Writer, """
                IF NOT EXISTS(SELECT 1 FROM dbo.Fuente WHERE FuenteKey=@Key)
                INSERT dbo.Fuente(FuenteKey,Origen,Articulo,Listado,Nombre,Url,EsDocumento) VALUES(@Key,'SAT',@Article,@Listing,@Name,@Url,@Document);
                """);
            P(cmd,"@Key",SqlDbType.NVarChar,listing.Key,100); P(cmd,"@Article",SqlDbType.VarChar,listing.Article,16);
            P(cmd,"@Listing",SqlDbType.VarChar,listing.Slug,80); P(cmd,"@Name",SqlDbType.NVarChar,listing.Name,200);
            P(cmd,"@Url",SqlDbType.NVarChar,listing.Url,2048); P(cmd,"@Document",SqlDbType.Bit,listing.Document);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        using var dof = Command(Writer, """
            IF NOT EXISTS(SELECT 1 FROM dbo.Fuente WHERE FuenteKey='dof:publicaciones')
            INSERT dbo.Fuente(FuenteKey,Origen,Articulo,Listado,Nombre,Url,EsDocumento)
            VALUES('dof:publicaciones','DOF',NULL,'publicaciones-dof',N'Publicaciones DOF','https://sidof.segob.gob.mx/dof/sidof/',0);
            """);
        await dof.ExecuteNonQueryAsync(ct);
        if (settings is not null)
        {
            foreach (var listing in SourceCatalog.Sat)
            {
                using var enabled = Command(Writer, "UPDATE dbo.Fuente SET Habilitada=@Enabled WHERE FuenteKey=@Key;");
                P(enabled,"@Enabled",SqlDbType.Bit,settings.SatEnabled && (settings.EnabledSatSources.Length == 0 || settings.EnabledSatSources.Contains(listing.Key)));
                P(enabled,"@Key",SqlDbType.NVarChar,listing.Key,100); await enabled.ExecuteNonQueryAsync(ct);
            }
            using var objective = Command(Writer, """
                UPDATE dbo.Fuente SET Habilitada=@Enabled WHERE FuenteKey='dof:publicaciones';
                UPDATE dbo.ObjetivoCobertura SET DofDesde=@Start,DofHasta=@End,DofHabilitado=@Enabled WHERE Id=1;
                IF @@ROWCOUNT=0 INSERT dbo.ObjetivoCobertura(Id,DofDesde,DofHasta,DofHabilitado) VALUES(1,@Start,@End,@Enabled);
                """);
            P(objective,"@Enabled",SqlDbType.Bit,settings.DofEnabled); P(objective,"@Start",SqlDbType.Date,settings.DofStartDate);
            P(objective,"@End",SqlDbType.Date,DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.Now,Scheduling.ScheduleCalculator.Zone(settings.Schedule.TimeZone)).DateTime));
            await objective.ExecuteNonQueryAsync(ct);
        }
    }
    private static SqlCommand Query(SqlConnection c,string procedure,long generation,QueryFilter filter,string[]? rfcs)
    {
        if(filter.Article is not null&&!SourceCatalog.Articles.Contains(filter.Article))throw new ArgumentException("Artículo desconocido.");
        var cmd=Command(c,procedure); cmd.CommandType=CommandType.StoredProcedure;
        P(cmd,"@GeneracionId",SqlDbType.BigInt,generation); P(cmd,"@Articulo",SqlDbType.VarChar,filter.Article,16); P(cmd,"@Listado",SqlDbType.VarChar,filter.Listing,80);
        P(cmd,"@Historico",SqlDbType.Bit,filter.IncludeHistory); P(cmd,"@FiltrarRfc",SqlDbType.Bit,rfcs is not null);
        var table=new DataTable(); table.Columns.Add("Rfc",typeof(string)); table.Columns.Add("OrdenEntrada",typeof(int));
        if(rfcs is not null) {var normalized=Rfc.ValidateMany(rfcs); for(var i=0;i<normalized.Length;i++)table.Rows.Add(normalized[i],i);}
        var p=cmd.Parameters.Add("@Rfcs",SqlDbType.Structured); p.TypeName="dbo.RfcInput"; p.Value=table;
        return cmd;
    }
    public async IAsyncEnumerable<Appearance> ReadAsync(long generation,QueryFilter filter,string[]? rfcs,[EnumeratorCancellation]CancellationToken ct)
    {
        await using var c=await OpenAsync(ct); using var cmd=Query(c,"dbo.Apariciones",generation,filter,rfcs);
        await using var r=await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess,ct);
        while(await r.ReadAsync(ct))yield return new(r.GetInt64(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8),
            r.IsDBNull(9)?null:DateOnly.FromDateTime(r.GetDateTime(9)),r.GetBoolean(10),r.IsDBNull(11)?null:r.GetInt64(11),r.GetString(12),r.GetInt32(13),r.GetInt32(14),r.GetString(15));
    }
    public async Task SaveExecutionAsync(ExecutionReport report,CancellationToken ct)
    {
        await using var c=await OpenAsync(ct); await using var tx=(SqlTransaction)await c.BeginTransactionAsync(ct);
        using var cmd=Command(c,"""
            IF NOT EXISTS(SELECT 1 FROM dbo.Ejecucion WITH(UPDLOCK,HOLDLOCK) WHERE EjecucionId=@Id)
            INSERT dbo.Ejecucion(EjecucionId,Inicio,Fin,Resultado,Disparador,ResumenJson) VALUES(@Id,@Start,@End,@Result,@Trigger,@Json);
            """,tx);
        P(cmd,"@Id",SqlDbType.UniqueIdentifier,report.EjecucionId); P(cmd,"@Start",SqlDbType.DateTime2,report.Inicio); P(cmd,"@End",SqlDbType.DateTime2,report.Fin);
        P(cmd,"@Result",SqlDbType.VarChar,report.Resultado,30); P(cmd,"@Trigger",SqlDbType.VarChar,report.Disparador,30); P(cmd,"@Json",SqlDbType.NVarChar,JsonSerializer.Serialize(report,Json),-1);
        await cmd.ExecuteNonQueryAsync(ct);
        foreach(var source in report.Fuentes)
        {
            using var state=Command(c,"""
                UPDATE dbo.FuenteEstado WITH(UPDLOCK,HOLDLOCK) SET UltimoIntento=@Date,UltimoExito=CASE WHEN @Ok=1 THEN @Date ELSE UltimoExito END,Resultado=@Result,ErrorSeguro=@Error WHERE FuenteKey=@Source;
                IF @@ROWCOUNT=0 INSERT dbo.FuenteEstado(FuenteKey,UltimoIntento,UltimoExito,Resultado,ErrorSeguro) VALUES(@Source,@Date,CASE WHEN @Ok=1 THEN @Date ELSE NULL END,@Result,@Error);
                """,tx);
            P(state,"@Date",SqlDbType.DateTime2,report.Fin); P(state,"@Ok",SqlDbType.Bit,source.Resultado is "cambios" or "sin-cambios"); P(state,"@Result",SqlDbType.VarChar,source.Resultado,30);
            P(state,"@Error",SqlDbType.NVarChar,source.Error,2000); P(state,"@Source",SqlDbType.NVarChar,source.Fuente,100); await state.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
    public async Task CompleteDofDayAsync(DateOnly date,bool complete,string? detail,CancellationToken ct)
    {
        using var cmd=Command(Writer,"""
            UPDATE dbo.CoberturaDof SET Completa=@Complete,Comprobada=SYSDATETIME(),Detalle=@Detail WHERE Fecha=@Date;
            IF @@ROWCOUNT=0 INSERT dbo.CoberturaDof(Fecha,Completa,Comprobada,Detalle) VALUES(@Date,@Complete,SYSDATETIME(),@Detail);
            """);
        P(cmd,"@Complete",SqlDbType.Bit,complete);P(cmd,"@Date",SqlDbType.Date,date);P(cmd,"@Detail",SqlDbType.NVarChar,detail,2000);await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<DateOnly?> LastCompleteDofDateAsync(CancellationToken ct)
    {
        await using var c=await OpenAsync(ct); using var cmd=Command(c,"SELECT Fecha,Completa FROM dbo.CoberturaDof ORDER BY Fecha;");
        await using var r=await cmd.ExecuteReaderAsync(ct); DateOnly? last=null;
        while(await r.ReadAsync(ct)) {var day=DateOnly.FromDateTime(r.GetDateTime(0));if(!r.GetBoolean(1)||(last is not null&&day!=last.Value.AddDays(1)))break;last=day;}
        return last;
    }
    public async Task<IReadOnlyList<DateOnly>> PendingDofDaysAsync(DateOnly startDate,DateOnly today,int lookback,int max,CancellationToken ct)
    {
        if(max<1||lookback<1)throw new ArgumentOutOfRangeException(nameof(max));
        await using var c=await OpenAsync(ct);
        using var cmd=Command(c,"SELECT Fecha,Completa,Comprobada FROM dbo.CoberturaDof WHERE Fecha BETWEEN @Start AND @End;");
        P(cmd,"@Start",SqlDbType.Date,startDate);P(cmd,"@End",SqlDbType.Date,today);
        var complete=new Dictionary<DateOnly,DateTime>();
        await using(var r=await cmd.ExecuteReaderAsync(ct))while(await r.ReadAsync(ct))if(r.GetBoolean(1))complete[DateOnly.FromDateTime(r.GetDateTime(0))]=r.GetDateTime(2);
        var result=new List<DateOnly>();
        // Fill historical holes first; never derive the next day from MAX(Fecha).
        for(var day=startDate;day<=today&&result.Count<max;day=day.AddDays(1))if(!complete.ContainsKey(day))result.Add(day);
        var cutoff=today.AddDays(1-lookback);
        foreach(var day in complete.Where(x=>x.Key>=cutoff).OrderBy(x=>x.Value).ThenBy(x=>x.Key).Select(x=>x.Key).Take(max-result.Count))result.Add(day);
        return result;
    }
    public async Task SaveDofPendingAsync(long code,DateOnly date,string reason,CancellationToken ct)
    {
        using var cmd=Command(Writer,"""
            UPDATE dbo.NotaPendiente SET Fecha=@Date,Estado='pendiente',Motivo=@Reason,UltimoIntento=SYSDATETIME() WHERE CodNota=@Code;
            IF @@ROWCOUNT=0 INSERT dbo.NotaPendiente(CodNota,Fecha,Estado,Motivo,UltimoIntento) VALUES(@Code,@Date,'pendiente',@Reason,SYSDATETIME());
            """);
        P(cmd,"@Code",SqlDbType.BigInt,code);P(cmd,"@Date",SqlDbType.Date,date);P(cmd,"@Reason",SqlDbType.NVarChar,reason,2000);await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task ClearDofPendingAsync(long code,CancellationToken ct)
    {
        using var cmd=Command(Writer,"DELETE dbo.NotaPendiente WHERE CodNota=@Code;");P(cmd,"@Code",SqlDbType.BigInt,code);await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task RecordAdjunctAsync(long code,string url,string state,string? path,string? reason,CancellationToken ct)
    {
        using var cmd=Command(Writer,"""
            UPDATE dbo.Adjunto SET Estado=@State,EvidenciaRuta=@Path,Motivo=@Reason WHERE CodNota=@Code AND Url=@Url;
            IF @@ROWCOUNT=0 INSERT dbo.Adjunto(CodNota,Url,Estado,EvidenciaRuta,Motivo) VALUES(@Code,@Url,@State,@Path,@Reason);
            """);
        P(cmd,"@Code",SqlDbType.BigInt,code);P(cmd,"@Url",SqlDbType.NVarChar,url,2048);P(cmd,"@State",SqlDbType.VarChar,state,30);P(cmd,"@Path",SqlDbType.NVarChar,path,2048);P(cmd,"@Reason",SqlDbType.NVarChar,reason,2000);await cmd.ExecuteNonQueryAsync(ct);
    }
}
