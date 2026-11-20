SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- ----------------------------------------------------------------------------
-- 11. Procedimientos Almacenados de Consulta / API Consolidada
-- ----------------------------------------------------------------------------

CREATE OR ALTER PROCEDURE dbo.ConsultarContribuyentesVigentes
    @Articulo               varchar(16) = NULL,
    @Listado                varchar(100) = NULL,
    @Situacion              nvarchar(200) = NULL,
    @FiltrarRfc             bit = 0,
    @Rfcs                   dbo.RfcInput READONLY,
    @OrdenarPorJerarquia    bit = 0
AS
BEGIN
    SET NOCOUNT ON;

    IF @OrdenarPorJerarquia = 1
    BEGIN
        SELECT 
            v.ContribuyenteId,
            v.Rfc,
            v.RazonSocial,
            v.Articulo,
            v.Listado,
            v.Situacion,
            v.FechaEfectiva,
            v.OrigenUltimaModificacion,
            v.FechaModificacion,
            v.TipoPersona,
            v.EntidadFederativa,
            v.Monto,
            v.Ejercicio,
            v.Periodo,
            v.Motivo,
            v.FechaCancelacion,
            v.FechaAutorizacion,
            v.Administracion,
            v.NumeroOficioSAT,
            v.FechaOficioSAT,
            v.NumeroOficioDOF,
            v.FechaOficioDOF,
            v.PresuncionOficioSAT,
            v.PresuncionFechaSAT,
            v.PresuncionOficioDOF,
            v.PresuncionFechaDOF,
            v.DesvirtuadoOficioSAT,
            v.DesvirtuadoFechaSAT,
            v.DesvirtuadoOficioDOF,
            v.DesvirtuadoFechaDOF,
            v.DefinitivoOficioSAT,
            v.DefinitivoFechaSAT,
            v.DefinitivoOficioDOF,
            v.DefinitivoFechaDOF,
            v.SentenciaOficioSAT,
            v.SentenciaFechaSAT,
            v.SentenciaOficioDOF,
            v.SentenciaFechaDOF,
            v.CodNotaDOF,
            v.UrlOrigenDOF,
            v.UltimaDescarga,
            p.Titulo AS TituloDOF,
            p.Edicion AS EdicionDOF,
            p.CodDiario AS CodDiarioDOF,
            p.FechaPublicacion AS FechaPublicacionDOF
        FROM dbo.ContribuyenteVigente v
        JOIN dbo.Articulo a ON a.Clave = v.Articulo
        LEFT JOIN dbo.PublicacionDOF p ON p.CodNota = v.CodNotaDOF
        WHERE (@Articulo IS NULL OR v.Articulo = @Articulo)
          AND (@Listado IS NULL OR v.Listado = @Listado)
          AND (@Situacion IS NULL OR v.Situacion LIKE '%' + @Situacion + '%')
          AND (@FiltrarRfc = 0 OR EXISTS (SELECT 1 FROM @Rfcs r WHERE r.Rfc = v.Rfc))
        ORDER BY 
            a.Orden,
            v.Listado,
            v.Rfc COLLATE Latin1_General_100_BIN2
        OPTION (RECOMPILE);
    END
    ELSE
    BEGIN
        SELECT 
            v.ContribuyenteId,
            v.Rfc,
            v.RazonSocial,
            v.Articulo,
            v.Listado,
            v.Situacion,
            v.FechaEfectiva,
            v.OrigenUltimaModificacion,
            v.FechaModificacion,
            v.TipoPersona,
            v.EntidadFederativa,
            v.Monto,
            v.Ejercicio,
            v.Periodo,
            v.Motivo,
            v.FechaCancelacion,
            v.FechaAutorizacion,
            v.Administracion,
            v.NumeroOficioSAT,
            v.FechaOficioSAT,
            v.NumeroOficioDOF,
            v.FechaOficioDOF,
            v.PresuncionOficioSAT,
            v.PresuncionFechaSAT,
            v.PresuncionOficioDOF,
            v.PresuncionFechaDOF,
            v.DesvirtuadoOficioSAT,
            v.DesvirtuadoFechaSAT,
            v.DesvirtuadoOficioDOF,
            v.DesvirtuadoFechaDOF,
            v.DefinitivoOficioSAT,
            v.DefinitivoFechaSAT,
            v.DefinitivoOficioDOF,
            v.DefinitivoFechaDOF,
            v.SentenciaOficioSAT,
            v.SentenciaFechaSAT,
            v.SentenciaOficioDOF,
            v.SentenciaFechaDOF,
            v.CodNotaDOF,
            v.UrlOrigenDOF,
            v.UltimaDescarga,
            p.Titulo AS TituloDOF,
            p.Edicion AS EdicionDOF,
            p.CodDiario AS CodDiarioDOF,
            p.FechaPublicacion AS FechaPublicacionDOF
        FROM dbo.ContribuyenteVigente v
        JOIN dbo.Articulo a ON a.Clave = v.Articulo
        LEFT JOIN dbo.PublicacionDOF p ON p.CodNota = v.CodNotaDOF
        WHERE (@Articulo IS NULL OR v.Articulo = @Articulo)
          AND (@Listado IS NULL OR v.Listado = @Listado)
          AND (@Situacion IS NULL OR v.Situacion LIKE '%' + @Situacion + '%')
          AND (@FiltrarRfc = 0 OR EXISTS (SELECT 1 FROM @Rfcs r WHERE r.Rfc = v.Rfc))
        ORDER BY 
            v.Rfc COLLATE Latin1_General_100_BIN2,
            a.Orden,
            CASE WHEN v.FechaEfectiva IS NULL THEN 1 ELSE 0 END,
            v.FechaEfectiva DESC,
            v.Listado
        OPTION (RECOMPILE);
    END;
END;
GO

