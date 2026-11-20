SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- GET /api/EdosEfos/all: todo lo correspondiente al 69-B / 69-B Bis, sin paginación.
-- Resultados: 1) metadatos, 2) total, 3) datos.
CREATE OR ALTER PROCEDURE dbo.PA_Legacy_EdosEfos_All
AS
BEGIN
    SET NOCOUNT ON;

    SELECT FechaPortal69b, UltimaDescarga69b, UltimoIntento69b, FechaPortalNL, UltimaDescargaNL, UltimoIntentoNL, FechaPortalBis, UltimaDescargaBis, UltimoIntentoBis FROM dbo.vw_LegacyInfoFuentes;

    SELECT d.Rfc, d.RazonSocial, d.Situacion,
           d.folioPresuncionSAT, d.publicacionSATPresuncion, d.folioPresuncionDOF, d.publicacionDOFPresuncion,
           d.folioDesvirtuadosSAT, d.publicacionSATDesvirtuados, d.folioDesvirtuadosDOF, d.publicacionDOFDesvirtuados,
           d.folioDefinitivosSAT, d.publicacionSATDefinitivos, d.folioDefinitivosDOF, d.publicacionDOFDefinitivos,
           d.folioFavorablesSAT, d.publicacionSATFavorables, d.folioFavorablesDOF, d.publicacionDOFFavorables
    INTO #Todos
    FROM dbo.vw_LegacyRfcDetalle d
    WHERE d.EsEfos = 1 AND d.Rn = 1;

    SELECT COUNT_BIG(*) AS TotalRegistros FROM #Todos;
    SELECT * FROM #Todos ORDER BY Rfc COLLATE Latin1_General_100_BIN2;
END;
GO

