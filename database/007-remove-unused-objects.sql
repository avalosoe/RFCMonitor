-- Removes tables no longer used by MonitorRFC. Apply after validating a backup.
SET XACT_ABORT ON;
GO
BEGIN TRANSACTION;
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
IF NOT EXISTS(SELECT 1 FROM dbo.MigracionEsquema WHERE Version=7) INSERT dbo.MigracionEsquema(Version) VALUES(7);
COMMIT;
GO
