SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- Una fila con los metadatos de las fuentes (SAT 69-b y no localizados) para los encabezados.
CREATE OR ALTER VIEW dbo.vw_LegacyInfoFuentes
AS
SELECT
    FechaPortal69b      = (SELECT MAX(c.FechaPublicacionPortal) FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69-b' AND c.EsDocumento = 0),
    UltimaDescarga69b   = (SELECT MAX(c.UltimaDescargaExitosa)  FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69-b' AND c.EsDocumento = 0),
    UltimoIntento69b    = (SELECT MAX(c.UltimoIntento) FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69-b' AND c.EsDocumento = 0),
    FechaPortalNL       = (SELECT MAX(c.FechaPublicacionPortal) FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69' AND c.Listado = 'no-localizados'),
    UltimaDescargaNL    = (SELECT MAX(c.UltimaDescargaExitosa)  FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69' AND c.Listado = 'no-localizados'),
    UltimoIntentoNL     = (SELECT MAX(c.UltimoIntento) FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69' AND c.Listado = 'no-localizados'),
    FechaPortalBis      = (SELECT MAX(c.FechaPublicacionPortal) FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69-b-bis' AND c.EsDocumento = 0),
    UltimaDescargaBis   = (SELECT MAX(c.UltimaDescargaExitosa)  FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69-b-bis' AND c.EsDocumento = 0),
    UltimoIntentoBis    = (SELECT MAX(c.UltimoIntento) FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69-b-bis' AND c.EsDocumento = 0);
GO

