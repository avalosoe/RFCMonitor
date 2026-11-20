SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

CREATE OR ALTER PROCEDURE dbo.Apariciones
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
            v.ContribuyenteId               AS RegistroId,
            CAST(1 AS bigint)               AS VersionId,
            v.Listado                       AS FuenteKey,
            v.OrigenUltimaModificacion      AS Origen,
            v.Articulo                      AS Articulo,
            v.Listado                       AS Listado,
            v.Rfc                           AS Rfc,
            v.RazonSocial                   AS NombreOriginal,
            v.Situacion                     AS EstadoOriginal,
            v.FechaEfectiva                 AS FechaPublicacion,
            CONVERT(bit, 0)                 AS Historica,
            v.CodNotaDOF                    AS CodNota,
            COALESCE(v.UrlOrigenDOF, N'')   AS Url,
            0                               AS TablaOrdinal,
            0                               AS FilaOrdinal,
            N'{}'                           AS DetalleJson
        FROM dbo.ContribuyenteVigente v
        JOIN dbo.Articulo a ON a.Clave = v.Articulo
        WHERE (@Articulo IS NULL OR v.Articulo = @Articulo)
          AND (@Listado IS NULL OR v.Listado = @Listado)
          AND (@FiltrarRfc = 0 OR EXISTS (SELECT 1 FROM @Rfcs r WHERE r.Rfc = v.Rfc))
        ORDER BY 
            v.Rfc COLLATE Latin1_General_100_BIN2,
            a.Orden,
            CASE WHEN v.FechaEfectiva IS NULL THEN 1 ELSE 0 END,
            v.FechaEfectiva DESC,
            v.Listado
        OPTION (RECOMPILE);
    END
    ELSE
    BEGIN
        SELECT 
            h.HistorialId                   AS RegistroId,
            CAST(1 AS bigint)               AS VersionId,
            h.Listado                       AS FuenteKey,
            h.Origen                        AS Origen,
            h.Articulo                      AS Articulo,
            h.Listado                       AS Listado,
            h.Rfc                           AS Rfc,
            h.RazonSocial                   AS NombreOriginal,
            h.Situacion                     AS EstadoOriginal,
            h.FechaEfectiva                 AS FechaPublicacion,
            CONVERT(bit, CASE WHEN h.TipoEvento = 'Alta Inicial' THEN 0 ELSE 1 END) AS Historica,
            h.CodNotaDOF                    AS CodNota,
            COALESCE(h.UrlOrigen, N'')      AS Url,
            0                               AS TablaOrdinal,
            0                               AS FilaOrdinal,
            N'{}'                           AS DetalleJson
        FROM dbo.ContribuyenteHistorial h
        JOIN dbo.Articulo a ON a.Clave = h.Articulo
        WHERE (@Articulo IS NULL OR h.Articulo = @Articulo)
          AND (@Listado IS NULL OR h.Listado = @Listado)
          AND (@FiltrarRfc = 0 OR EXISTS (SELECT 1 FROM @Rfcs r WHERE r.Rfc = h.Rfc))
        ORDER BY 
            h.Rfc COLLATE Latin1_General_100_BIN2,
            a.Orden,
            CASE WHEN h.FechaEfectiva IS NULL THEN 1 ELSE 0 END,
            h.FechaEfectiva DESC,
            h.FechaRegistro DESC
        OPTION (RECOMPILE);
    END;
END;
GO

