SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- No localizados (art. 69): primera publicación y entidad por RFC.
CREATE OR ALTER VIEW dbo.vw_LegacyNoLocalizados
AS
SELECT v.Rfc,
       FechaPrimeraPublicacion = MIN(v.FechaEfectiva),
       EntidadFederativa       = MAX(v.EntidadFederativa)
FROM dbo.ContribuyenteVigente v
WHERE v.Articulo = '69' AND v.Listado = 'no-localizados'
GROUP BY v.Rfc;
GO

