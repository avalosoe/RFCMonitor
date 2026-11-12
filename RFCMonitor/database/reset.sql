SET XACT_ABORT ON;
GO

-- ============================================================================
-- MonitorRFC - Script de Limpieza Integral (Reset)
-- ============================================================================
-- Este script elimina de manera segura e idempotente todos los objetos de la
-- solución RFCMonitor en la base de datos SQL Server, respetando el orden
-- estricto de dependencias de claves foráneas y tipos de tabla.
-- ============================================================================

-- 1. Vistas
DROP VIEW IF EXISTS dbo.Vigentes;
GO

-- 2. Procedimientos almacenados
DROP PROCEDURE IF EXISTS dbo.PublicarGeneracion;
DROP PROCEDURE IF EXISTS dbo.EstadoFuentes;
DROP PROCEDURE IF EXISTS dbo.DocumentoTecnico;
DROP PROCEDURE IF EXISTS dbo.Conteos;
DROP PROCEDURE IF EXISTS dbo.Apariciones;
DROP PROCEDURE IF EXISTS dbo.Encabezado;
GO

-- 3. Funciones
DROP FUNCTION IF EXISTS dbo.FilasGeneracion;
GO

-- 4. Tipos definidos por el usuario (deben eliminarse después de los SPs que los consumen)
IF TYPE_ID(N'dbo.RfcInput') IS NOT NULL
BEGIN
    DROP TYPE dbo.RfcInput;
END;
GO

-- 5. Ruptura de dependencias circulares (FK de Publicacion hacia PublicacionVersion)
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_mrf_ActiveVersion')
BEGIN
    ALTER TABLE dbo.Publicacion DROP CONSTRAINT FK_mrf_ActiveVersion;
END;
GO

-- 6. Tablas principales de la solución RFCMonitor (orden inverso a sus dependencias)
DROP TABLE IF EXISTS dbo.GeneracionActiva;
DROP TABLE IF EXISTS dbo.GeneracionVersion;
DROP TABLE IF EXISTS dbo.Generacion;
DROP TABLE IF EXISTS dbo.DocumentoCelda;
DROP TABLE IF EXISTS dbo.RegistroFuente;
DROP TABLE IF EXISTS dbo.FilaFuente;
DROP TABLE IF EXISTS dbo.ObservacionPublicacion;
DROP TABLE IF EXISTS dbo.PublicacionVersion;
DROP TABLE IF EXISTS dbo.Publicacion;
DROP TABLE IF EXISTS dbo.FuenteEstado;
DROP TABLE IF EXISTS dbo.Fuente;
DROP TABLE IF EXISTS dbo.Articulo;
DROP TABLE IF EXISTS dbo.Ejecucion;
DROP TABLE IF EXISTS dbo.CoberturaDof;
DROP TABLE IF EXISTS dbo.ObjetivoCobertura;
DROP TABLE IF EXISTS dbo.NotaPendiente;
DROP TABLE IF EXISTS dbo.Adjunto;
DROP TABLE IF EXISTS dbo.Lease;
GO

-- 7. Tablas legadas y de versiones históricas (EdosSatScraper / RfcStatusApi)
DROP TABLE IF EXISTS dbo.CampoFuente;
DROP TABLE IF EXISTS dbo.MigracionEsquema;
DROP TABLE IF EXISTS dbo.Detalle69;
DROP TABLE IF EXISTS dbo.Detalle69B;
DROP TABLE IF EXISTS dbo.Detalle69BBis;
DROP TABLE IF EXISTS dbo.Detalle49Bis;
DROP TABLE IF EXISTS dbo.Fila;
DROP TABLE IF EXISTS dbo.Sat69BPublicacionEtapa;
DROP TABLE IF EXISTS dbo.Sat69BBisPublicacionEtapa;
DROP TABLE IF EXISTS dbo.Sat69BRegistro;
DROP TABLE IF EXISTS dbo.Sat69BBisRegistro;
DROP TABLE IF EXISTS dbo.CanceladosDetalle;
DROP TABLE IF EXISTS dbo.ReduccionMultasDetalle;
DROP TABLE IF EXISTS dbo.ConcursoMercantilDetalle;
DROP TABLE IF EXISTS dbo.ReduccionRecargosDetalle;
DROP TABLE IF EXISTS dbo.CondonadosDecretoDetalle;
DROP TABLE IF EXISTS dbo.CondonadosHistoricoDetalle;
DROP TABLE IF EXISTS dbo.CanceladosHistoricoDetalle;
DROP TABLE IF EXISTS dbo.RetornoInversionesDetalle;
DROP TABLE IF EXISTS dbo.ExigiblesDetalle;
DROP TABLE IF EXISTS dbo.FirmesDetalle;
DROP TABLE IF EXISTS dbo.NoLocalizadosDetalle;
DROP TABLE IF EXISTS dbo.SentenciasDetalle;
DROP TABLE IF EXISTS dbo.CsdSinEfectosDetalle;
DROP TABLE IF EXISTS dbo.EntesOmisosDetalle;
GO

-- 8. Roles y permisos de base de datos
DECLARE @sql nvarchar(max) = N'';
SELECT @sql = STRING_AGG(N'ALTER ROLE ' + QUOTENAME(r.name) + N' DROP MEMBER ' + QUOTENAME(m.name) + N';', CHAR(10))
FROM sys.database_role_members rm
JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id
WHERE r.name IN (N'mrf_api', N'mrf_ingestor');

IF @sql <> N'' EXEC sys.sp_executesql @sql;

IF DATABASE_PRINCIPAL_ID(N'mrf_api') IS NOT NULL DROP ROLE mrf_api;
IF DATABASE_PRINCIPAL_ID(N'mrf_ingestor') IS NOT NULL DROP ROLE mrf_ingestor;
GO
