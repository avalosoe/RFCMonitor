SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- POST /api/Rfc/Edos-Efos: consulta por RFC (sin duplicados). 1er resultado: metadatos; 2do: datos.
CREATE OR ALTER PROCEDURE dbo.PA_Legacy_EdosEfos_Consultar
    @Rfcs dbo.RfcInput READONLY
AS
BEGIN
    SET NOCOUNT ON;

    SELECT FechaPortal69b, UltimaDescarga69b, UltimoIntento69b, FechaPortalNL, UltimaDescargaNL, UltimoIntentoNL, FechaPortalBis, UltimaDescargaBis, UltimoIntentoBis FROM dbo.vw_LegacyInfoFuentes;

    SELECT
        d.Rfc                                   AS rfc,
        d.RazonSocial                           AS nombreContribuyente,
        d.Situacion                             AS estatus,
        d.NumeroFechaOficioSAT                  AS numeroFechaOficioSAT,
        d.PublicacionPaginaSAT                  AS publicacionPaginaSAT,
        d.NumeroFechaOficioGlobal               AS numeroFechaOficioGlobal,
        d.PublicacionDOF                        AS publicacionDOF,
        a.Articulo                              AS articulo
    FROM @Rfcs r
    JOIN dbo.vw_LegacyRfcDetalle d ON d.Rfc = r.Rfc AND d.Rn = 1
    JOIN dbo.vw_LegacyRfcArticulos a ON a.Rfc = r.Rfc
    ORDER BY r.OrdenEntrada
    OPTION (RECOMPILE);
END;
GO

