SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

CREATE OR ALTER PROCEDURE dbo.ConsultarEstatusGeneral
AS
BEGIN
    SET NOCOUNT ON;

    SELECT (
        SELECT 
            JSON_QUERY((
                SELECT 
                    f.FuenteClave               AS fuente,
                    f.Articulo                  AS articulo,
                    f.Listado                   AS listado,
                    f.Nombre                    AS nombre,
                    f.FechaPublicacionPortal    AS fechaPortal,
                    f.UltimoIntento             AS ultimoIntento,
                    f.UltimaDescargaExitosa     AS ultimaDescargaExitosa,
                    f.Resultado                 AS resultado,
                    f.UltimoError               AS error,
                    f.TotalRegistrosVigentes    AS registrosVigentes,
                    f.TotalNuevos               AS nuevos,
                    f.TotalModificados          AS modificados,
                    f.TotalSinCambios           AS sinCambios
                FROM dbo.ControlFuenteSAT f
                ORDER BY f.FuenteClave
                FOR JSON PATH
            )) AS fuentesSat,
            JSON_QUERY((
                SELECT 
                    Fecha               AS fecha,
                    Completa            AS completa,
                    FechaComprobacion   AS comprobada,
                    TotalNotasFiscales  AS totalNotas,
                    Detalle             AS detalle
                FROM dbo.ControlCoberturaDOF
                ORDER BY Fecha DESC
                FOR JSON PATH
            )) AS coberturaDof,
            JSON_QUERY((
                SELECT 
                    CodNota         AS codNota,
                    Fecha           AS fecha,
                    Estado          AS estado,
                    Motivo          AS motivo,
                    UltimoIntento   AS ultimoIntento
                FROM dbo.NotaPendienteDOF
                ORDER BY Fecha DESC, CodNota
                FOR JSON PATH
            )) AS notasPendientesDof,
            JSON_QUERY((
                SELECT TOP(1)
                    EjecucionId AS ejecucionId,
                    Inicio      AS inicio,
                    Fin         AS fin,
                    Resultado   AS resultado
                FROM dbo.BitacoraEjecucion
                ORDER BY Inicio DESC
                FOR JSON PATH
            )) AS ultimaEjecucion
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    ) AS EstadoJson;
END;
GO

