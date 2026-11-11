-- Run after 001-schema.sql. All API access is parameterized and snapshot-bound.
SET XACT_ABORT ON;
GO
CREATE OR ALTER PROCEDURE dbo.Encabezado AS
BEGIN
 SET NOCOUNT ON;
 SELECT g.GeneracionId,g.CreadaUtc,g.FuentesJson,g.CoberturaJson
 FROM dbo.GeneracionActiva a JOIN dbo.Generacion g ON g.GeneracionId=a.GeneracionId WHERE a.Id=1;
END;
GO
CREATE OR ALTER PROCEDURE dbo.Apariciones
 @GeneracionId bigint,@Articulo varchar(16)=NULL,@Listado varchar(80)=NULL,@Historico bit=0,
 @FiltrarRfc bit=0,@Rfcs dbo.RfcInput READONLY
AS
BEGIN
 SET NOCOUNT ON;
 SELECT r.RegistroId,r.VersionId,p.FuenteKey,f.Origen,r.Articulo,r.Listado,r.Rfc,r.NombreOriginal,r.EstadoOriginal,
 r.FechaPublicacion,CONVERT(bit,1-gv.EsActiva) Historica,v.CodNota,v.Url,r.TablaOrdinal,r.FilaOrdinal,r.DetalleJson
 FROM dbo.GeneracionVersion gv
 JOIN dbo.PublicacionVersion v ON v.VersionId=gv.VersionId
 JOIN dbo.Publicacion p ON p.PublicacionId=v.PublicacionId
 JOIN dbo.Fuente f ON f.FuenteKey=p.FuenteKey
 JOIN dbo.RegistroFuente r ON r.VersionId=v.VersionId
 WHERE gv.GeneracionId=@GeneracionId AND (@Historico=1 OR gv.EsActiva=1)
 AND (@Articulo IS NULL OR r.Articulo=@Articulo) AND (@Listado IS NULL OR r.Listado=@Listado)
 AND (@FiltrarRfc=0 OR EXISTS(SELECT 1 FROM @Rfcs i WHERE i.Rfc=r.Rfc))
 ORDER BY r.Rfc COLLATE Latin1_General_100_BIN2,r.Articulo,r.Listado,f.Origen,r.VersionId,r.TablaOrdinal,r.FilaOrdinal,r.RegistroId
 OPTION(RECOMPILE);
END;
GO
CREATE OR ALTER PROCEDURE dbo.Conteos
 @GeneracionId bigint,@Articulo varchar(16)=NULL,@Listado varchar(80)=NULL,@Historico bit=0,
 @FiltrarRfc bit=0,@Rfcs dbo.RfcInput READONLY
AS
BEGIN
 SET NOCOUNT ON;
 SELECT COUNT_BIG(DISTINCT r.Rfc) Rfcs,COUNT_BIG(*) Apariciones
 FROM dbo.GeneracionVersion gv JOIN dbo.RegistroFuente r ON r.VersionId=gv.VersionId
 WHERE gv.GeneracionId=@GeneracionId AND (@Historico=1 OR gv.EsActiva=1)
 AND (@Articulo IS NULL OR r.Articulo=@Articulo) AND (@Listado IS NULL OR r.Listado=@Listado)
 AND (@FiltrarRfc=0 OR EXISTS(SELECT 1 FROM @Rfcs i WHERE i.Rfc=r.Rfc)) OPTION(RECOMPILE);
END;
GO
CREATE OR ALTER PROCEDURE dbo.PublicarGeneracion @Propietario uniqueidentifier,@Token bigint
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 BEGIN TRANSACTION;
 IF APPLOCK_MODE('public','MonitorRFC.ingestion','Session')<>'Exclusive' OR NOT EXISTS(SELECT 1 FROM dbo.Lease WITH(UPDLOCK,HOLDLOCK) WHERE Recurso='ingestion' AND Propietario=@Propietario AND FencingToken=@Token)
 BEGIN ROLLBACK; THROW 51000,'Lease de ingesta perdido.',1; END;
 DECLARE @Fuentes nvarchar(max)=(SELECT f.FuenteKey fuente,f.Origen origen,f.Articulo articulo,f.Listado listado,f.Nombre nombre,f.EsDocumento esDocumento,
 e.UltimoIntentoUtc ultimoIntentoUtc,e.UltimoExitoUtc ultimoExitoUtc,e.Resultado resultado,e.ErrorSeguro error,
 JSON_QUERY((SELECT p.ClaveExterna claveExterna,v.VersionId versionId,v.FechaFuente fechaFuente,v.AlcanceFecha alcanceFecha,
 p.PrimeraDeteccionUtc primeraDeteccionUtc,v.DetectadaUtc detectadaUtc,v.IncorporadaUtc incorporadaUtc,v.HashContenido hashContenido,v.ParserVersion parserVersion,v.Filas registros,v.Url url,v.CodNota codNota
 FROM dbo.Publicacion p JOIN dbo.PublicacionVersion v ON v.VersionId=p.VersionActivaId WHERE p.FuenteKey=f.FuenteKey ORDER BY p.ClaveExterna FOR JSON PATH)) publicaciones
 FROM dbo.Fuente f LEFT JOIN dbo.FuenteEstado e ON e.FuenteKey=f.FuenteKey ORDER BY f.FuenteKey FOR JSON PATH);
 DECLARE @Cobertura nvarchar(max)=(SELECT Fecha fecha,Completa completa,ComprobadaUtc comprobadaUtc,Detalle detalle FROM dbo.CoberturaDof ORDER BY Fecha FOR JSON PATH);
 INSERT dbo.Generacion(FuentesJson,CoberturaJson) VALUES(@Fuentes,@Cobertura);
 DECLARE @Id bigint=SCOPE_IDENTITY();
 INSERT dbo.GeneracionVersion(GeneracionId,VersionId,EsActiva)
 SELECT @Id,v.VersionId,CONVERT(bit,CASE WHEN p.VersionActivaId=v.VersionId THEN 1 ELSE 0 END)
 FROM dbo.PublicacionVersion v JOIN dbo.Publicacion p ON p.PublicacionId=v.PublicacionId WHERE v.Estado='publicada';
 UPDATE dbo.GeneracionActiva SET GeneracionId=@Id WHERE Id=1;
 IF @@ROWCOUNT=0 INSERT dbo.GeneracionActiva(Id,GeneracionId) VALUES(1,@Id);
 COMMIT;
 SELECT @Id;
END;
GO
IF NOT EXISTS(SELECT 1 FROM dbo.MigracionEsquema WHERE Version=2) INSERT dbo.MigracionEsquema(Version) VALUES(2);
GO
