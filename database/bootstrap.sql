SET XACT_ABORT ON;
GO
CREATE TABLE dbo.Articulo(Clave varchar(16) NOT NULL PRIMARY KEY, Orden int NOT NULL);
CREATE TABLE dbo.Fuente(FuenteKey nvarchar(100) NOT NULL PRIMARY KEY, Origen varchar(8) NOT NULL,
 Articulo varchar(16) NULL REFERENCES dbo.Articulo(Clave), Listado varchar(80) NOT NULL, Nombre nvarchar(200) NOT NULL,
 Url nvarchar(2048) NOT NULL, EsDocumento bit NOT NULL DEFAULT 0, Habilitada bit NOT NULL DEFAULT 1);
CREATE TABLE dbo.Publicacion(PublicacionId bigint IDENTITY PRIMARY KEY, FuenteKey nvarchar(100) NOT NULL REFERENCES dbo.Fuente(FuenteKey),
 ClaveExterna nvarchar(100) NOT NULL, PrimeraDeteccionUtc datetime2(3) NOT NULL, VersionActivaId bigint NULL,
 CONSTRAINT UQ_mrf_Publicacion UNIQUE(FuenteKey,ClaveExterna));
CREATE TABLE dbo.PublicacionVersion(VersionId bigint IDENTITY PRIMARY KEY, PublicacionId bigint NOT NULL REFERENCES dbo.Publicacion(PublicacionId),
 HashContenido char(64) NOT NULL, ParserVersion varchar(40) NOT NULL, Url nvarchar(2048) NOT NULL, EvidenciaRuta nvarchar(2048) NOT NULL,
 FechaFuente date NULL, AlcanceFecha varchar(30) NOT NULL, DetectadaUtc datetime2(3) NOT NULL, IncorporadaUtc datetime2(3) NULL,
 Estado varchar(20) NOT NULL DEFAULT 'preparando' CHECK(Estado IN ('preparando','publicada')), Filas bigint NOT NULL DEFAULT 0,
 FilasLeidas bigint NOT NULL DEFAULT 0, FilasRechazadas bigint NOT NULL DEFAULT 0,
 Certificacion varchar(30) NOT NULL DEFAULT 'pendiente' CHECK(Certificacion IN ('certificada','requiere-revision','pendiente')),
 CodNota bigint NULL, Titulo nvarchar(max) NULL, Edicion nvarchar(40) NULL, Organismo nvarchar(500) NULL);
ALTER TABLE dbo.Publicacion ADD CONSTRAINT FK_mrf_ActiveVersion FOREIGN KEY(VersionActivaId) REFERENCES dbo.PublicacionVersion(VersionId);
CREATE TABLE dbo.RegistroFuente(RegistroId bigint IDENTITY PRIMARY KEY, VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId),
 TablaOrdinal int NOT NULL, FilaOrdinal int NOT NULL, Rfc nvarchar(13) COLLATE Latin1_General_100_BIN2 NOT NULL,
 NombreOriginal nvarchar(1000) NOT NULL, Articulo varchar(16) NOT NULL REFERENCES dbo.Articulo(Clave), Listado varchar(80) NOT NULL,
 EstadoOriginal nvarchar(1000) NOT NULL, FechaPublicacion date NULL, EntidadOriginal nvarchar(500) NULL,
 DetalleJson nvarchar(max) NOT NULL CHECK(ISJSON(DetalleJson)=1), CONSTRAINT UQ_mrf_Fila UNIQUE(VersionId,TablaOrdinal,FilaOrdinal));
CREATE TABLE dbo.FilaFuente(VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId), TablaOrdinal int NOT NULL,
 FilaOrdinal int NOT NULL, RfcOriginal nvarchar(400) NULL, EsValida bit NOT NULL, MotivoRechazo nvarchar(1000) NULL,
 DatosOriginalesJson nvarchar(max) NOT NULL CHECK(ISJSON(DatosOriginalesJson)=1),
 PRIMARY KEY(VersionId,TablaOrdinal,FilaOrdinal));
CREATE TABLE dbo.DocumentoCelda(VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId), HojaOrdinal int NOT NULL,
 FilaOrdinal int NOT NULL, ColumnaOrdinal int NOT NULL, Hoja nvarchar(255) NOT NULL, Valor nvarchar(max) NULL,
 PRIMARY KEY(VersionId,HojaOrdinal,FilaOrdinal,ColumnaOrdinal));
CREATE TABLE dbo.ObservacionPublicacion(ObservacionId bigint IDENTITY PRIMARY KEY, VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId),
 ObservadaUtc datetime2(3) NOT NULL, FechaFuente date NULL, AlcanceFecha varchar(30) NOT NULL, Url nvarchar(2048) NOT NULL, SinCambios bit NOT NULL);
CREATE TABLE dbo.Ejecucion(EjecucionId uniqueidentifier NOT NULL PRIMARY KEY, InicioUtc datetime2(3) NOT NULL, FinUtc datetime2(3) NOT NULL,
 Resultado varchar(30) NOT NULL, Disparador varchar(30) NOT NULL, ResumenJson nvarchar(max) NOT NULL CHECK(ISJSON(ResumenJson)=1));
CREATE TABLE dbo.FuenteEstado(FuenteKey nvarchar(100) NOT NULL PRIMARY KEY, UltimoIntentoUtc datetime2(3) NOT NULL,
 UltimoExitoUtc datetime2(3) NULL, Resultado varchar(30) NOT NULL, ErrorSeguro nvarchar(2000) NULL);
CREATE TABLE dbo.CoberturaDof(Fecha date NOT NULL PRIMARY KEY, Completa bit NOT NULL, ComprobadaUtc datetime2(3) NOT NULL, Detalle nvarchar(2000) NULL);
CREATE TABLE dbo.ObjetivoCobertura(Id tinyint PRIMARY KEY CHECK(Id=1), DofDesde date NOT NULL, DofHasta date NOT NULL, DofHabilitado bit NOT NULL);
CREATE TABLE dbo.NotaPendiente(CodNota bigint NOT NULL PRIMARY KEY, Fecha date NOT NULL, Estado varchar(30) NOT NULL,
 Motivo nvarchar(2000) NOT NULL, UltimoIntentoUtc datetime2(3) NOT NULL);
CREATE TABLE dbo.Adjunto(AdjuntoId bigint IDENTITY PRIMARY KEY, CodNota bigint NOT NULL, Url nvarchar(2048) NOT NULL,
 Estado varchar(30) NOT NULL, EvidenciaRuta nvarchar(2048) NULL, Motivo nvarchar(2000) NULL);
CREATE TABLE dbo.Lease(Recurso varchar(100) NOT NULL PRIMARY KEY, Propietario uniqueidentifier NOT NULL, FencingToken bigint NOT NULL, ExpiraUtc datetime2(3) NOT NULL);
CREATE TABLE dbo.Generacion(GeneracionId bigint IDENTITY PRIMARY KEY, CreadaUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
 FuentesJson nvarchar(max) NOT NULL CHECK(ISJSON(FuentesJson)=1), CoberturaJson nvarchar(max) NOT NULL CHECK(ISJSON(CoberturaJson)=1));
CREATE TABLE dbo.GeneracionVersion(GeneracionId bigint NOT NULL REFERENCES dbo.Generacion(GeneracionId), VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId),
 EsActiva bit NOT NULL, PRIMARY KEY(GeneracionId,VersionId));
CREATE TABLE dbo.GeneracionActiva(Id tinyint NOT NULL PRIMARY KEY CHECK(Id=1), GeneracionId bigint NOT NULL REFERENCES dbo.Generacion(GeneracionId));
CREATE TYPE dbo.RfcInput AS TABLE(Rfc nvarchar(13) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY, OrdenEntrada int NOT NULL);
GO
CREATE INDEX IX_mrf_Registro_VersionRfc ON dbo.RegistroFuente(VersionId,Rfc,Articulo,Listado);
CREATE INDEX IX_mrf_Registro_Rfc ON dbo.RegistroFuente(Rfc,VersionId);
CREATE INDEX IX_mrf_Version_Publicacion ON dbo.PublicacionVersion(PublicacionId,VersionId);
CREATE INDEX IX_mrf_FilaFuente_Rechazo ON dbo.FilaFuente(VersionId,EsValida) INCLUDE(FilaOrdinal,RfcOriginal,MotivoRechazo);
CREATE INDEX IX_mrf_ObservacionVersion ON dbo.ObservacionPublicacion(VersionId,ObservadaUtc DESC);
INSERT dbo.Articulo(Clave,Orden) VALUES('69',1),('69-b',2),('69-b-bis',3),('49-bis',4);
GO
CREATE OR ALTER FUNCTION dbo.FilasGeneracion(@GeneracionId bigint,@Historico bit)
RETURNS TABLE AS RETURN
 SELECT r.*,gv.EsActiva,p.FuenteKey,f.Origen,v.CodNota,v.Url
 FROM dbo.GeneracionVersion gv JOIN dbo.PublicacionVersion v ON v.VersionId=gv.VersionId
 JOIN dbo.Publicacion p ON p.PublicacionId=v.PublicacionId JOIN dbo.Fuente f ON f.FuenteKey=p.FuenteKey
 JOIN dbo.RegistroFuente r ON r.VersionId=v.VersionId
 WHERE gv.GeneracionId=@GeneracionId AND (@Historico=1 OR gv.EsActiva=1);
GO
CREATE OR ALTER VIEW dbo.Vigentes AS
 SELECT r.* FROM dbo.GeneracionActiva a CROSS APPLY dbo.FilasGeneracion(a.GeneracionId,0) r WHERE a.Id=1;
GO
CREATE OR ALTER PROCEDURE dbo.Encabezado AS
BEGIN
 SET NOCOUNT ON;
 SELECT g.GeneracionId,g.CreadaUtc,g.FuentesJson,g.CoberturaJson FROM dbo.GeneracionActiva a JOIN dbo.Generacion g ON g.GeneracionId=a.GeneracionId WHERE a.Id=1;
END;
GO
CREATE OR ALTER PROCEDURE dbo.Apariciones @GeneracionId bigint,@Articulo varchar(16)=NULL,@Listado varchar(80)=NULL,@Historico bit=0,@FiltrarRfc bit=0,@Rfcs dbo.RfcInput READONLY
AS
BEGIN
 SET NOCOUNT ON;
 SELECT r.RegistroId,r.VersionId,r.FuenteKey,r.Origen,r.Articulo,r.Listado,r.Rfc,r.NombreOriginal,r.EstadoOriginal,r.FechaPublicacion,
 CONVERT(bit,1-r.EsActiva) Historica,r.CodNota,r.Url,r.TablaOrdinal,r.FilaOrdinal,r.DetalleJson
 FROM dbo.FilasGeneracion(@GeneracionId,@Historico) r JOIN dbo.Articulo a ON a.Clave=r.Articulo
 WHERE (@Articulo IS NULL OR r.Articulo=@Articulo) AND (@Listado IS NULL OR r.Listado=@Listado)
 AND (@FiltrarRfc=0 OR EXISTS(SELECT 1 FROM @Rfcs i WHERE i.Rfc=r.Rfc))
 ORDER BY r.Rfc COLLATE Latin1_General_100_BIN2,a.Orden,r.Listado,r.FechaPublicacion DESC,r.Origen,r.VersionId,r.TablaOrdinal,r.FilaOrdinal,r.RegistroId OPTION(RECOMPILE);
END;
GO
CREATE OR ALTER PROCEDURE dbo.Conteos @GeneracionId bigint,@Articulo varchar(16)=NULL,@Listado varchar(80)=NULL,@Historico bit=0,@FiltrarRfc bit=0,@Rfcs dbo.RfcInput READONLY
AS
BEGIN
 SET NOCOUNT ON;
 SELECT COUNT_BIG(DISTINCT r.Rfc) Rfcs,COUNT_BIG(*) Apariciones FROM dbo.FilasGeneracion(@GeneracionId,@Historico) r
 WHERE (@Articulo IS NULL OR r.Articulo=@Articulo) AND (@Listado IS NULL OR r.Listado=@Listado)
 AND (@FiltrarRfc=0 OR EXISTS(SELECT 1 FROM @Rfcs i WHERE i.Rfc=r.Rfc)) OPTION(RECOMPILE);
END;
GO
CREATE OR ALTER PROCEDURE dbo.DocumentoTecnico @GeneracionId bigint AS
BEGIN
 SET NOCOUNT ON;
 SELECT c.VersionId,c.HojaOrdinal,c.FilaOrdinal,c.ColumnaOrdinal,c.Hoja,c.Valor
 FROM dbo.GeneracionVersion v JOIN dbo.DocumentoCelda c ON c.VersionId=v.VersionId WHERE v.GeneracionId=@GeneracionId AND v.EsActiva=1
 ORDER BY c.VersionId,c.HojaOrdinal,c.FilaOrdinal,c.ColumnaOrdinal;
END;
GO
CREATE OR ALTER PROCEDURE dbo.EstadoFuentes AS
BEGIN
 SET NOCOUNT ON;
 SELECT (SELECT JSON_QUERY((SELECT f.FuenteKey fuente,f.Origen origen,f.Articulo articulo,f.Listado listado,e.UltimoIntentoUtc ultimoIntentoUtc,
 e.UltimoExitoUtc ultimoExitoUtc,e.Resultado resultado,e.ErrorSeguro error,s.registrosAceptados,s.filasLeidas,s.filasRechazadas,s.certificacion,s.incorporadaEn
 FROM dbo.Fuente f LEFT JOIN dbo.FuenteEstado e ON e.FuenteKey=f.FuenteKey
 OUTER APPLY(SELECT SUM(v.Filas) registrosAceptados,SUM(v.FilasLeidas) filasLeidas,SUM(v.FilasRechazadas) filasRechazadas,
 CASE WHEN MAX(CASE WHEN v.Certificacion='requiere-revision' THEN 1 ELSE 0 END)=1 THEN 'requiere-revision'
 WHEN MIN(CASE WHEN v.Certificacion='certificada' THEN 1 ELSE 0 END)=1 THEN 'certificada' ELSE 'pendiente' END certificacion,MAX(v.IncorporadaUtc) incorporadaEn
 FROM dbo.Publicacion p JOIN dbo.PublicacionVersion v ON v.VersionId=p.VersionActivaId WHERE p.FuenteKey=f.FuenteKey) s ORDER BY f.FuenteKey FOR JSON PATH)) fuentes,
 JSON_QUERY((SELECT Fecha fecha,Completa completa,ComprobadaUtc comprobadaUtc,Detalle detalle FROM dbo.CoberturaDof ORDER BY Fecha FOR JSON PATH)) cobertura,
 JSON_QUERY((SELECT CodNota codNota,Fecha fecha,Estado estado,Motivo motivo FROM dbo.NotaPendiente ORDER BY Fecha,CodNota FOR JSON PATH)) pendientes,
 JSON_QUERY((SELECT TOP(1) EjecucionId ejecucionId,InicioUtc inicioUtc,FinUtc finUtc,Resultado resultado FROM dbo.Ejecucion ORDER BY FinUtc DESC FOR JSON PATH)) ultimaEjecucion
 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER) EstadoJson;
END;
GO
CREATE OR ALTER PROCEDURE dbo.PublicarGeneracion @Propietario uniqueidentifier,@Token bigint
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON; BEGIN TRANSACTION;
 IF APPLOCK_MODE('public','MonitorRFC.ingestion','Session')<>'Exclusive' OR NOT EXISTS(SELECT 1 FROM dbo.Lease WITH(UPDLOCK,HOLDLOCK) WHERE Recurso='ingestion' AND Propietario=@Propietario AND FencingToken=@Token)
 BEGIN ROLLBACK; THROW 51000,'Lease de ingesta perdido.',1; END;
 DECLARE @Fuentes nvarchar(max)=(SELECT CASE WHEN f.Origen='DOF' AND ra.Articulo IS NOT NULL THEN CONCAT('dof:',ra.Articulo,':publicaciones-dof') ELSE f.FuenteKey END fuenteId,
 f.Origen origen,COALESCE(ra.Articulo,f.Articulo) articulo,f.Listado listado,f.Nombre nombre,f.EsDocumento esDocumento,f.Habilitada habilitada,v.VersionId versionId,
 v.FechaFuente fechaActualizacionFuente,v.AlcanceFecha alcanceFecha,p.PrimeraDeteccionUtc primeraDeteccionUtc,v.DetectadaUtc detectadaEn,v.IncorporadaUtc incorporadaEn,
 v.HashContenido hashContenido,v.ParserVersion parserVersion,v.Filas registros,v.Url url,v.CodNota codNota,
 CONVERT(bit,CASE WHEN v.VersionId IS NOT NULL AND v.VersionId<>p.VersionActivaId THEN 1 ELSE 0 END) esHistorica
 FROM dbo.Fuente f LEFT JOIN dbo.Publicacion p ON p.FuenteKey=f.FuenteKey LEFT JOIN dbo.PublicacionVersion v ON v.PublicacionId=p.PublicacionId AND v.Estado='publicada'
 OUTER APPLY(SELECT DISTINCT r.Articulo FROM dbo.RegistroFuente r WHERE r.VersionId=v.VersionId AND f.Origen='DOF') ra ORDER BY f.FuenteKey,v.VersionId,ra.Articulo FOR JSON PATH,INCLUDE_NULL_VALUES);
 DECLARE @Desde date,@Hasta date,@Dof bit; SELECT @Desde=DofDesde,@Hasta=DofHasta,@Dof=DofHabilitado FROM dbo.ObjetivoCobertura WHERE Id=1;
 DECLARE @Dias int=(SELECT COUNT(*) FROM dbo.CoberturaDof WHERE Fecha BETWEEN @Desde AND @Hasta AND Completa=1);
 DECLARE @Cobertura nvarchar(max)=(SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM dbo.Fuente f WHERE f.Origen='SAT' AND f.Habilitada=1) THEN 'deshabilitada'
 WHEN EXISTS(SELECT 1 FROM dbo.Fuente f WHERE f.Origen='SAT' AND f.Habilitada=1 AND NOT EXISTS(SELECT 1 FROM dbo.Publicacion p WHERE p.FuenteKey=f.FuenteKey AND p.VersionActivaId IS NOT NULL)) THEN 'parcial' ELSE 'completa' END sat,
 JSON_QUERY((SELECT @Desde desde,@Hasta hasta,@Dof habilitado,CONVERT(bit,CASE WHEN @Dof=1 AND @Dias=DATEDIFF(day,@Desde,@Hasta)+1 AND NOT EXISTS(SELECT 1 FROM dbo.NotaPendiente WHERE Fecha BETWEEN @Desde AND @Hasta) THEN 1 ELSE 0 END) completa,
 (SELECT COUNT(*) FROM dbo.NotaPendiente WHERE Fecha BETWEEN @Desde AND @Hasta) pendientes,JSON_QUERY((SELECT Fecha fecha,Completa completa,ComprobadaUtc comprobadaUtc,Detalle detalle FROM dbo.CoberturaDof ORDER BY Fecha FOR JSON PATH)) dias FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES)) dof FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
 INSERT dbo.Generacion(FuentesJson,CoberturaJson) VALUES(@Fuentes,@Cobertura); DECLARE @Id bigint=SCOPE_IDENTITY();
 INSERT dbo.GeneracionVersion(GeneracionId,VersionId,EsActiva) SELECT @Id,v.VersionId,CONVERT(bit,CASE WHEN p.VersionActivaId=v.VersionId THEN 1 ELSE 0 END) FROM dbo.PublicacionVersion v JOIN dbo.Publicacion p ON p.PublicacionId=v.PublicacionId WHERE v.Estado='publicada';
 UPDATE dbo.GeneracionActiva SET GeneracionId=@Id WHERE Id=1; IF @@ROWCOUNT=0 INSERT dbo.GeneracionActiva(Id,GeneracionId) VALUES(1,@Id); COMMIT; SELECT @Id;
END;
GO
