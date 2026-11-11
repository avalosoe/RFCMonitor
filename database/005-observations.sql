IF OBJECT_ID(N'dbo.ObservacionPublicacion',N'U') IS NULL
 CREATE TABLE dbo.ObservacionPublicacion(
 ObservacionId bigint IDENTITY PRIMARY KEY,VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId),
 ObservadaUtc datetime2(3) NOT NULL,FechaFuente date NULL,AlcanceFecha varchar(30) NOT NULL,
 Url nvarchar(2048) NOT NULL,SinCambios bit NOT NULL);
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ObservacionPublicacion') AND name='IX_mrf_ObservacionVersion')
 CREATE INDEX IX_mrf_ObservacionVersion ON dbo.ObservacionPublicacion(VersionId,ObservadaUtc DESC);
GO
IF NOT EXISTS(SELECT 1 FROM dbo.MigracionEsquema WHERE Version=5) INSERT dbo.MigracionEsquema(Version) VALUES(5);
GO
