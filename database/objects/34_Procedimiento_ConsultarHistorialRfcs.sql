SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

CREATE OR ALTER PROCEDURE dbo.ConsultarHistorialRfcs
    @Articulo       varchar(16) = NULL,
    @Listado        varchar(100) = NULL,
    @Rfcs           dbo.RfcInput READONLY
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 
        h.HistorialId,
        h.Rfc,
        h.RazonSocial,
        h.Articulo,
        h.Listado,
        h.Situacion,
        h.FechaEfectiva,
        h.TipoEvento,
        h.Origen,
        h.FechaRegistro,
        h.CodNotaDOF,
        h.UrlOrigen,
        h.NumeroOficioSAT,
        h.FechaOficioSAT,
        h.NumeroOficioDOF,
        h.FechaOficioDOF,
        h.EntidadFederativa,
        h.Motivo,
        p.Titulo AS TituloDOF,
        p.Edicion AS EdicionDOF,
        p.CodDiario AS CodDiarioDOF,
        p.FechaPublicacion AS FechaPublicacionDOF
    FROM dbo.ContribuyenteHistorial h
    JOIN dbo.Articulo a ON a.Clave = h.Articulo
    LEFT JOIN dbo.PublicacionDOF p ON p.CodNota = h.CodNotaDOF
    WHERE EXISTS (SELECT 1 FROM @Rfcs r WHERE r.Rfc = h.Rfc)
      AND (@Articulo IS NULL OR h.Articulo = @Articulo)
      AND (@Listado IS NULL OR h.Listado = @Listado)
    ORDER BY 
        h.Rfc COLLATE Latin1_General_100_BIN2,
        CASE WHEN h.FechaEfectiva IS NULL THEN 1 ELSE 0 END,
        h.FechaEfectiva ASC,
        h.FechaRegistro ASC
    OPTION (RECOMPILE);
END;
GO

