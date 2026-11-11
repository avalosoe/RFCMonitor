-- Metadatos inmutables por versión, objetivo de cobertura y consulta común.
IF COL_LENGTH('dbo.Fuente','Habilitada') IS NULL
 ALTER TABLE dbo.Fuente ADD Habilitada bit NOT NULL CONSTRAINT DF_mrf_Fuente_Habilitada DEFAULT 1;
GO
IF OBJECT_ID(N'dbo.ObjetivoCobertura',N'U') IS NULL
 CREATE TABLE dbo.ObjetivoCobertura(Id tinyint PRIMARY KEY CHECK(Id=1),DofDesde date NOT NULL,DofHasta date NOT NULL,DofHabilitado bit NOT NULL);
GO
CREATE OR ALTER FUNCTION dbo.FilasGeneracion(@GeneracionId bigint,@Historico bit)
RETURNS TABLE AS RETURN
 SELECT r.*,gv.EsActiva,p.FuenteKey,f.Origen,v.CodNota,v.Url
 FROM dbo.GeneracionVersion gv
 JOIN dbo.PublicacionVersion v ON v.VersionId=gv.VersionId
 JOIN dbo.Publicacion p ON p.PublicacionId=v.PublicacionId
 JOIN dbo.Fuente f ON f.FuenteKey=p.FuenteKey
 JOIN dbo.RegistroFuente r ON r.VersionId=v.VersionId
 WHERE gv.GeneracionId=@GeneracionId AND (@Historico=1 OR gv.EsActiva=1);
GO
CREATE OR ALTER VIEW dbo.Vigentes AS
 SELECT r.* FROM dbo.GeneracionActiva a CROSS APPLY dbo.FilasGeneracion(a.GeneracionId,0) r WHERE a.Id=1;
GO
CREATE OR ALTER PROCEDURE dbo.Apariciones
 @GeneracionId bigint,@Articulo varchar(16)=NULL,@Listado varchar(80)=NULL,@Historico bit=0,
 @FiltrarRfc bit=0,@Rfcs dbo.RfcInput READONLY
AS
BEGIN
 SET NOCOUNT ON;
 SELECT r.RegistroId,r.VersionId,r.FuenteKey,r.Origen,r.Articulo,r.Listado,r.Rfc,r.NombreOriginal,r.EstadoOriginal,
 r.FechaPublicacion,CONVERT(bit,1-r.EsActiva) Historica,r.CodNota,r.Url,r.TablaOrdinal,r.FilaOrdinal,r.DetalleJson
 FROM dbo.FilasGeneracion(@GeneracionId,@Historico) r
 JOIN dbo.Articulo a ON a.Clave=r.Articulo
 WHERE (@Articulo IS NULL OR r.Articulo=@Articulo) AND (@Listado IS NULL OR r.Listado=@Listado)
 AND (@FiltrarRfc=0 OR EXISTS(SELECT 1 FROM @Rfcs i WHERE i.Rfc=r.Rfc))
 ORDER BY r.Rfc COLLATE Latin1_General_100_BIN2,a.Orden,r.Listado,r.FechaPublicacion DESC,r.Origen,r.VersionId,r.TablaOrdinal,r.FilaOrdinal,r.RegistroId
 OPTION(RECOMPILE);
END;
GO
CREATE OR ALTER PROCEDURE dbo.PublicarGeneracion @Propietario uniqueidentifier,@Token bigint
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 BEGIN TRANSACTION;
 IF APPLOCK_MODE('public','MonitorRFC.ingestion','Session')<>'Exclusive' OR NOT EXISTS(SELECT 1 FROM dbo.Lease WITH(UPDLOCK,HOLDLOCK) WHERE Recurso='ingestion' AND Propietario=@Propietario AND FencingToken=@Token)
 BEGIN ROLLBACK; THROW 51000,'Lease de ingesta perdido.',1; END;
 DECLARE @Fuentes nvarchar(max)=(
 SELECT CASE WHEN f.Origen='DOF' AND ra.Articulo IS NOT NULL THEN CONCAT('dof:',ra.Articulo,':publicaciones-dof') ELSE f.FuenteKey END fuenteId,
 f.Origen origen,COALESCE(ra.Articulo,f.Articulo) articulo,f.Listado listado,f.Nombre nombre,f.EsDocumento esDocumento,f.Habilitada habilitada,
 v.VersionId versionId,v.FechaFuente fechaActualizacionFuente,v.AlcanceFecha alcanceFecha,
 p.PrimeraDeteccionUtc primeraDeteccionUtc,v.DetectadaUtc detectadaEn,v.IncorporadaUtc incorporadaEn,
 v.HashContenido hashContenido,v.ParserVersion parserVersion,v.Filas registros,v.Url url,v.CodNota codNota,
 CONVERT(bit,CASE WHEN v.VersionId IS NOT NULL AND v.VersionId<>p.VersionActivaId THEN 1 ELSE 0 END) esHistorica
 FROM dbo.Fuente f
 LEFT JOIN dbo.Publicacion p ON p.FuenteKey=f.FuenteKey
 LEFT JOIN dbo.PublicacionVersion v ON v.PublicacionId=p.PublicacionId AND v.Estado='publicada'
 OUTER APPLY(SELECT DISTINCT r.Articulo FROM dbo.RegistroFuente r WHERE r.VersionId=v.VersionId AND f.Origen='DOF') ra
 ORDER BY f.FuenteKey,v.VersionId,ra.Articulo FOR JSON PATH,INCLUDE_NULL_VALUES);
 DECLARE @Desde date,@Hasta date,@Dof bit;
 SELECT @Desde=DofDesde,@Hasta=DofHasta,@Dof=DofHabilitado FROM dbo.ObjetivoCobertura WHERE Id=1;
 DECLARE @Dias int=(SELECT COUNT(*) FROM dbo.CoberturaDof WHERE Fecha BETWEEN @Desde AND @Hasta AND Completa=1);
 DECLARE @Cobertura nvarchar(max)=(SELECT
 CASE WHEN NOT EXISTS(SELECT 1 FROM dbo.Fuente f WHERE f.Origen='SAT' AND f.Habilitada=1) THEN 'deshabilitada'
 WHEN EXISTS(SELECT 1 FROM dbo.Fuente f WHERE f.Origen='SAT' AND f.Habilitada=1 AND NOT EXISTS(SELECT 1 FROM dbo.Publicacion p WHERE p.FuenteKey=f.FuenteKey AND p.VersionActivaId IS NOT NULL)) THEN 'parcial' ELSE 'completa' END sat,
 JSON_QUERY((SELECT @Desde desde,@Hasta hasta,@Dof habilitado,
 CONVERT(bit,CASE WHEN @Dof=1 AND @Dias=DATEDIFF(day,@Desde,@Hasta)+1 AND NOT EXISTS(SELECT 1 FROM dbo.NotaPendiente WHERE Fecha BETWEEN @Desde AND @Hasta) THEN 1 ELSE 0 END) completa,
 (SELECT COUNT(*) FROM dbo.NotaPendiente WHERE Fecha BETWEEN @Desde AND @Hasta) pendientes,
 JSON_QUERY((SELECT Fecha fecha,Completa completa,ComprobadaUtc comprobadaUtc,Detalle detalle FROM dbo.CoberturaDof ORDER BY Fecha FOR JSON PATH)) dias
 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES)) dof
 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
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
IF NOT EXISTS(SELECT 1 FROM dbo.MigracionEsquema WHERE Version=4) INSERT dbo.MigracionEsquema(Version) VALUES(4);
GO
