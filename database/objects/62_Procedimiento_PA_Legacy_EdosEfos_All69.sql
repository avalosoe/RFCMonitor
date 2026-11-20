SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- GET /api/EdosEfos/all69: todos los RFC de todos los artículos, paginado por número de página.
-- Resultados: 1) metadatos, 2) total del universo filtrado, 3) datos de la página.
CREATE OR ALTER PROCEDURE dbo.PA_Legacy_EdosEfos_All69
    @Pagina int = NULL,
    @TamanoPagina int = NULL,
    @Articulo varchar(16) = NULL,
    @Listado varchar(100) = NULL,
    @Situacion nvarchar(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    -- ÚNICO lugar donde se define el tamaño de página de /api/EdosEfos/all69.
    -- Para cambiarlo basta modificar estas dos constantes y volver a ejecutar este script (sin recompilar la API).
    DECLARE @TamanoDefecto int = 10000;   -- tamaño cuando el cliente no envía tamanoPagina
    DECLARE @TamanoMaximo  int = 10000;   -- máximo permitido por página

    SET @Pagina = COALESCE(@Pagina, 1);
    SET @TamanoPagina = COALESCE(@TamanoPagina, @TamanoDefecto);
    IF @Pagina < 1
        THROW 50002, 'pagina debe ser mayor o igual a 1.', 1;
    IF @TamanoPagina NOT BETWEEN 1 AND @TamanoMaximo
    BEGIN
        DECLARE @Mensaje nvarchar(200) = CONCAT(N'tamanoPagina debe estar entre 1 y ', @TamanoMaximo, N'.');
        THROW 50003, @Mensaje, 1;
    END;
    DECLARE @Omitir bigint = (CONVERT(bigint, @Pagina) - 1) * @TamanoPagina;

    SELECT FechaPortal69b, UltimaDescarga69b, UltimoIntento69b, FechaPortalNL, UltimaDescargaNL, UltimoIntentoNL, FechaPortalBis, UltimaDescargaBis, UltimoIntentoBis FROM dbo.vw_LegacyInfoFuentes;

    -- Total del universo filtrado + valores efectivos de paginación (la API solo los refleja en la respuesta).
    SELECT COUNT_BIG(*) AS TotalRegistros, @Pagina AS Pagina, @TamanoPagina AS TamanoPagina
    FROM dbo.vw_LegacyRfcDetalle d
    WHERE d.Rn = 1
      AND (@Articulo IS NULL OR EXISTS (
          SELECT 1 FROM dbo.ContribuyenteVigente f
          WHERE f.Rfc = d.Rfc AND f.Articulo = @Articulo AND f.Listado = @Listado
            AND (@Situacion IS NULL OR f.Situacion LIKE '%' + @Situacion + '%')));

    SELECT d.Rfc, d.RazonSocial, d.Situacion,
           d.folioPresuncionSAT, d.publicacionSATPresuncion, d.folioPresuncionDOF, d.publicacionDOFPresuncion,
           d.folioDesvirtuadosSAT, d.publicacionSATDesvirtuados, d.folioDesvirtuadosDOF, d.publicacionDOFDesvirtuados,
           d.folioDefinitivosSAT, d.publicacionSATDefinitivos, d.folioDefinitivosDOF, d.publicacionDOFDefinitivos,
           d.folioFavorablesSAT, d.publicacionSATFavorables, d.folioFavorablesDOF, d.publicacionDOFFavorables,
           CONVERT(varchar(10), nl.FechaPrimeraPublicacion, 103) AS fechaPrimeraPublicacion_nl,
           nl.EntidadFederativa AS entidadFederativa_nl,
           CAST(CASE WHEN nl.Rfc IS NULL THEN 0 ELSE 1 END AS bit) AS enListado69NL,
           a.Articulo AS articulo
    FROM dbo.vw_LegacyRfcDetalle d
    JOIN dbo.vw_LegacyRfcArticulos a ON a.Rfc = d.Rfc
    LEFT JOIN dbo.vw_LegacyNoLocalizados nl ON nl.Rfc = d.Rfc
    WHERE d.Rn = 1
      AND (@Articulo IS NULL OR EXISTS (
          SELECT 1 FROM dbo.ContribuyenteVigente f
          WHERE f.Rfc = d.Rfc AND f.Articulo = @Articulo AND f.Listado = @Listado
            AND (@Situacion IS NULL OR f.Situacion LIKE '%' + @Situacion + '%')))
    ORDER BY d.Rfc COLLATE Latin1_General_100_BIN2
    OFFSET @Omitir ROWS FETCH NEXT @TamanoPagina ROWS ONLY;
END;
GO

