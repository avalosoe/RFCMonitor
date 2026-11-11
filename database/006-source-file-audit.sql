-- Source-file audit and quarantine. Apply after 005-observations.sql.
-- These tables preserve every CSV row and column exactly as received, including
-- rows that cannot participate in the normalized RFC projection.
SET XACT_ABORT ON;
GO
IF COL_LENGTH('dbo.PublicacionVersion','FilasLeidas') IS NULL
 ALTER TABLE dbo.PublicacionVersion ADD FilasLeidas bigint NOT NULL CONSTRAINT DF_mrf_Version_FilasLeidas DEFAULT 0;
GO
IF COL_LENGTH('dbo.PublicacionVersion','FilasRechazadas') IS NULL
 ALTER TABLE dbo.PublicacionVersion ADD FilasRechazadas bigint NOT NULL CONSTRAINT DF_mrf_Version_FilasRechazadas DEFAULT 0;
GO
IF COL_LENGTH('dbo.PublicacionVersion','Certificacion') IS NULL
 ALTER TABLE dbo.PublicacionVersion ADD Certificacion varchar(30) NOT NULL CONSTRAINT DF_mrf_Version_Certificacion DEFAULT 'pendiente'
 CHECK(Certificacion IN ('certificada','requiere-revision','pendiente'));
GO
-- Previous published versions were accepted atomically by the old importer.
-- Preserve them as the last certified reference when a new file needs review.
UPDATE dbo.PublicacionVersion SET Certificacion='certificada'
WHERE Estado='publicada' AND FilasRechazadas=0 AND Certificacion='pendiente';
GO
IF OBJECT_ID(N'dbo.FilaFuente',N'U') IS NULL
 CREATE TABLE dbo.FilaFuente(
   VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId),
   TablaOrdinal int NOT NULL,
   FilaOrdinal int NOT NULL,
   RfcOriginal nvarchar(400) NULL,
   EsValida bit NOT NULL,
   MotivoRechazo nvarchar(1000) NULL,
   PRIMARY KEY(VersionId,TablaOrdinal,FilaOrdinal));
GO
IF OBJECT_ID(N'dbo.CampoFuente',N'U') IS NULL
 CREATE TABLE dbo.CampoFuente(
   VersionId bigint NOT NULL,
   TablaOrdinal int NOT NULL,
   FilaOrdinal int NOT NULL,
   ColumnaOrdinal int NOT NULL,
   NombreOriginal nvarchar(1000) NOT NULL,
   ValorOriginal nvarchar(max) NULL,
   PRIMARY KEY(VersionId,TablaOrdinal,FilaOrdinal,ColumnaOrdinal),
   FOREIGN KEY(VersionId,TablaOrdinal,FilaOrdinal) REFERENCES dbo.FilaFuente(VersionId,TablaOrdinal,FilaOrdinal));
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.FilaFuente') AND name='IX_mrf_FilaFuente_Rechazo')
 CREATE INDEX IX_mrf_FilaFuente_Rechazo ON dbo.FilaFuente(VersionId,EsValida) INCLUDE(FilaOrdinal,RfcOriginal,MotivoRechazo);
GO
CREATE OR ALTER PROCEDURE dbo.EstadoFuentes AS
BEGIN
 SET NOCOUNT ON;
 SELECT (SELECT JSON_QUERY((SELECT f.FuenteKey fuente,f.Origen origen,f.Articulo articulo,f.Listado listado,
 e.UltimoIntentoUtc ultimoIntentoUtc,e.UltimoExitoUtc ultimoExitoUtc,e.Resultado resultado,e.ErrorSeguro error,
 s.registrosAceptados,s.filasLeidas,s.filasRechazadas,s.certificacion,s.incorporadaEn
 FROM dbo.Fuente f
 LEFT JOIN dbo.FuenteEstado e ON e.FuenteKey=f.FuenteKey
 OUTER APPLY(SELECT SUM(v.Filas) registrosAceptados,SUM(v.FilasLeidas) filasLeidas,SUM(v.FilasRechazadas) filasRechazadas,
   CASE WHEN MAX(CASE WHEN v.Certificacion='requiere-revision' THEN 1 ELSE 0 END)=1 THEN 'requiere-revision'
        WHEN MIN(CASE WHEN v.Certificacion='certificada' THEN 1 ELSE 0 END)=1 THEN 'certificada' ELSE 'pendiente' END certificacion,
   MAX(v.IncorporadaUtc) incorporadaEn
   FROM dbo.Publicacion p JOIN dbo.PublicacionVersion v ON v.VersionId=p.VersionActivaId WHERE p.FuenteKey=f.FuenteKey) s
 ORDER BY f.FuenteKey FOR JSON PATH)) fuentes,
 JSON_QUERY((SELECT Fecha fecha,Completa completa,ComprobadaUtc comprobadaUtc,Detalle detalle FROM dbo.CoberturaDof ORDER BY Fecha FOR JSON PATH)) cobertura,
 JSON_QUERY((SELECT CodNota codNota,Fecha fecha,Estado estado,Motivo motivo FROM dbo.NotaPendiente ORDER BY Fecha,CodNota FOR JSON PATH)) pendientes,
 JSON_QUERY((SELECT TOP(1) EjecucionId ejecucionId,InicioUtc inicioUtc,FinUtc finUtc,Resultado resultado FROM dbo.Ejecucion ORDER BY FinUtc DESC FOR JSON PATH)) ultimaEjecucion
 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER) EstadoJson;
END;
GO
IF NOT EXISTS(SELECT 1 FROM dbo.MigracionEsquema WHERE Version=6) INSERT dbo.MigracionEsquema(Version) VALUES(6);
GO
