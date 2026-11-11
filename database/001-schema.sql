-- Esquema base limpio de MonitorRFC. SQL Server 2019+.
SET XACT_ABORT ON;
GO
IF OBJECT_ID(N'dbo.Articulo',N'U') IS NULL
 CREATE TABLE dbo.Articulo(Clave varchar(16) NOT NULL PRIMARY KEY, Orden int NOT NULL);
GO
IF OBJECT_ID(N'dbo.Fuente',N'U') IS NULL
 CREATE TABLE dbo.Fuente(FuenteKey nvarchar(100) NOT NULL PRIMARY KEY, Origen varchar(8) NOT NULL,
  Articulo varchar(16) NULL REFERENCES dbo.Articulo(Clave), Listado varchar(80) NOT NULL,
  Nombre nvarchar(200) NOT NULL, Url nvarchar(2048) NOT NULL, EsDocumento bit NOT NULL DEFAULT 0);
GO
IF OBJECT_ID(N'dbo.MigracionEsquema',N'U') IS NULL
 CREATE TABLE dbo.MigracionEsquema(Version int NOT NULL PRIMARY KEY, AplicadaUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());
GO
IF OBJECT_ID(N'dbo.Publicacion',N'U') IS NULL
 CREATE TABLE dbo.Publicacion(PublicacionId bigint IDENTITY PRIMARY KEY, FuenteKey nvarchar(100) NOT NULL REFERENCES dbo.Fuente(FuenteKey),
  ClaveExterna nvarchar(100) NOT NULL, PrimeraDeteccionUtc datetime2(3) NOT NULL, VersionActivaId bigint NULL,
  CONSTRAINT UQ_mrf_Publicacion UNIQUE(FuenteKey,ClaveExterna));
GO
IF OBJECT_ID(N'dbo.PublicacionVersion',N'U') IS NULL
 CREATE TABLE dbo.PublicacionVersion(VersionId bigint IDENTITY PRIMARY KEY, PublicacionId bigint NOT NULL REFERENCES dbo.Publicacion(PublicacionId),
  HashContenido char(64) NOT NULL, ParserVersion varchar(40) NOT NULL, Url nvarchar(2048) NOT NULL, EvidenciaRuta nvarchar(2048) NOT NULL,
  FechaFuente date NULL, AlcanceFecha varchar(30) NOT NULL, DetectadaUtc datetime2(3) NOT NULL, IncorporadaUtc datetime2(3) NULL,
  Estado varchar(20) NOT NULL DEFAULT 'preparando' CHECK(Estado IN ('preparando','publicada')), Filas bigint NOT NULL DEFAULT 0,
  CodNota bigint NULL, Titulo nvarchar(max) NULL, Edicion nvarchar(40) NULL, Organismo nvarchar(500) NULL);
GO
IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE name='FK_mrf_ActiveVersion')
 ALTER TABLE dbo.Publicacion ADD CONSTRAINT FK_mrf_ActiveVersion FOREIGN KEY(VersionActivaId) REFERENCES dbo.PublicacionVersion(VersionId);
GO
IF OBJECT_ID(N'dbo.RegistroFuente',N'U') IS NULL
 CREATE TABLE dbo.RegistroFuente(RegistroId bigint IDENTITY PRIMARY KEY, VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId),
  TablaOrdinal int NOT NULL, FilaOrdinal int NOT NULL, Rfc nvarchar(13) COLLATE Latin1_General_100_BIN2 NOT NULL,
  NombreOriginal nvarchar(1000) NOT NULL, Articulo varchar(16) NOT NULL REFERENCES dbo.Articulo(Clave), Listado varchar(80) NOT NULL,
  EstadoOriginal nvarchar(1000) NOT NULL, FechaPublicacion date NULL, EntidadOriginal nvarchar(500) NULL,
  DetalleJson nvarchar(max) NOT NULL CHECK(ISJSON(DetalleJson)=1), CONSTRAINT UQ_mrf_Fila UNIQUE(VersionId,TablaOrdinal,FilaOrdinal));
GO
IF OBJECT_ID(N'dbo.DocumentoCelda',N'U') IS NULL
 CREATE TABLE dbo.DocumentoCelda(VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId), HojaOrdinal int NOT NULL,
  FilaOrdinal int NOT NULL, ColumnaOrdinal int NOT NULL, Hoja nvarchar(255) NOT NULL, Valor nvarchar(max) NULL,
  PRIMARY KEY(VersionId,HojaOrdinal,FilaOrdinal,ColumnaOrdinal));
GO
IF OBJECT_ID(N'dbo.Ejecucion',N'U') IS NULL
 CREATE TABLE dbo.Ejecucion(EjecucionId uniqueidentifier NOT NULL PRIMARY KEY, InicioUtc datetime2(3) NOT NULL, FinUtc datetime2(3) NOT NULL,
  Resultado varchar(30) NOT NULL, Disparador varchar(30) NOT NULL, ResumenJson nvarchar(max) NOT NULL CHECK(ISJSON(ResumenJson)=1));
GO
IF OBJECT_ID(N'dbo.FuenteEstado',N'U') IS NULL
 CREATE TABLE dbo.FuenteEstado(FuenteKey nvarchar(100) NOT NULL PRIMARY KEY, UltimoIntentoUtc datetime2(3) NOT NULL,
  UltimoExitoUtc datetime2(3) NULL, Resultado varchar(30) NOT NULL, ErrorSeguro nvarchar(2000) NULL);
GO
IF OBJECT_ID(N'dbo.CoberturaDof',N'U') IS NULL
 CREATE TABLE dbo.CoberturaDof(Fecha date NOT NULL PRIMARY KEY, Completa bit NOT NULL, ComprobadaUtc datetime2(3) NOT NULL, Detalle nvarchar(2000) NULL);
GO
IF OBJECT_ID(N'dbo.NotaPendiente',N'U') IS NULL
 CREATE TABLE dbo.NotaPendiente(CodNota bigint NOT NULL PRIMARY KEY, Fecha date NOT NULL, Estado varchar(30) NOT NULL, Motivo nvarchar(2000) NOT NULL, UltimoIntentoUtc datetime2(3) NOT NULL);
GO
IF OBJECT_ID(N'dbo.Adjunto',N'U') IS NULL
 CREATE TABLE dbo.Adjunto(AdjuntoId bigint IDENTITY PRIMARY KEY, CodNota bigint NOT NULL, Url nvarchar(2048) NOT NULL, Estado varchar(30) NOT NULL, EvidenciaRuta nvarchar(2048) NULL, Motivo nvarchar(2000) NULL);
GO
IF OBJECT_ID(N'dbo.Lease',N'U') IS NULL
 CREATE TABLE dbo.Lease(Recurso varchar(100) NOT NULL PRIMARY KEY, Propietario uniqueidentifier NOT NULL, FencingToken bigint NOT NULL, ExpiraUtc datetime2(3) NOT NULL);
GO
IF OBJECT_ID(N'dbo.Generacion',N'U') IS NULL
 CREATE TABLE dbo.Generacion(GeneracionId bigint IDENTITY PRIMARY KEY, CreadaUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(), FuentesJson nvarchar(max) NOT NULL CHECK(ISJSON(FuentesJson)=1), CoberturaJson nvarchar(max) NOT NULL CHECK(ISJSON(CoberturaJson)=1));
GO
IF OBJECT_ID(N'dbo.GeneracionVersion',N'U') IS NULL
 CREATE TABLE dbo.GeneracionVersion(GeneracionId bigint NOT NULL REFERENCES dbo.Generacion(GeneracionId), VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId), EsActiva bit NOT NULL, PRIMARY KEY(GeneracionId,VersionId));
GO
IF OBJECT_ID(N'dbo.GeneracionActiva',N'U') IS NULL
 CREATE TABLE dbo.GeneracionActiva(Id tinyint NOT NULL PRIMARY KEY CHECK(Id=1), GeneracionId bigint NOT NULL REFERENCES dbo.Generacion(GeneracionId));
GO
IF TYPE_ID(N'dbo.RfcInput') IS NULL EXEC(N'CREATE TYPE dbo.RfcInput AS TABLE(Rfc nvarchar(13) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY, OrdenEntrada int NOT NULL)');
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'IX_mrf_Registro_VersionRfc' AND object_id=OBJECT_ID(N'dbo.RegistroFuente')) CREATE INDEX IX_mrf_Registro_VersionRfc ON dbo.RegistroFuente(VersionId,Rfc,Articulo,Listado);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'IX_mrf_Registro_Rfc' AND object_id=OBJECT_ID(N'dbo.RegistroFuente')) CREATE INDEX IX_mrf_Registro_Rfc ON dbo.RegistroFuente(Rfc,VersionId);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'IX_mrf_Version_Publicacion' AND object_id=OBJECT_ID(N'dbo.PublicacionVersion')) CREATE INDEX IX_mrf_Version_Publicacion ON dbo.PublicacionVersion(PublicacionId,VersionId);
GO
INSERT dbo.Articulo(Clave,Orden) SELECT s.Clave,s.Orden FROM (VALUES('69',1),('69-b',2),('69-b-bis',3),('49-bis',4))s(Clave,Orden) WHERE NOT EXISTS(SELECT 1 FROM dbo.Articulo t WHERE t.Clave=s.Clave);
GO
IF NOT EXISTS(SELECT 1 FROM dbo.MigracionEsquema WHERE Version=1) INSERT dbo.MigracionEsquema(Version) VALUES(1);
GO
