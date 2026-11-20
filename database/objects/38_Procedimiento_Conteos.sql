SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

CREATE OR ALTER PROCEDURE dbo.Conteos
    @GeneracionId   bigint = 1,
    @Articulo       varchar(16) = NULL,
    @Listado        varchar(100) = NULL,
    @Historico      bit = 0,
    @FiltrarRfc     bit = 0,
    @Rfcs           dbo.RfcInput READONLY
AS
BEGIN
    SET NOCOUNT ON;

    IF @Historico = 0
    BEGIN
        SELECT 
            COUNT_BIG(DISTINCT v.Rfc) AS Rfcs,
            COUNT_BIG(*)              AS Apariciones
        FROM dbo.ContribuyenteVigente v
        WHERE (@Articulo IS NULL OR v.Articulo = @Articulo)
          AND (@Listado IS NULL OR v.Listado = @Listado)
          AND (@FiltrarRfc = 0 OR EXISTS (SELECT 1 FROM @Rfcs r WHERE r.Rfc = v.Rfc))
        OPTION (RECOMPILE);
    END
    ELSE
    BEGIN
        SELECT 
            COUNT_BIG(DISTINCT h.Rfc) AS Rfcs,
            COUNT_BIG(*)              AS Apariciones
        FROM dbo.ContribuyenteHistorial h
        WHERE (@Articulo IS NULL OR h.Articulo = @Articulo)
          AND (@Listado IS NULL OR h.Listado = @Listado)
          AND (@FiltrarRfc = 0 OR EXISTS (SELECT 1 FROM @Rfcs r WHERE r.Rfc = h.Rfc))
        OPTION (RECOMPILE);
    END;
END;
GO

