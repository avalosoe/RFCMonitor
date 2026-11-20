SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- ============================================================================
-- 12. Objetos para endpoints de compatibilidad con el proyecto legado (RfcStatusApi)
--     POST /api/Rfc/Edos-Efos, GET /api/EdosEfos/all, GET /api/EdosEfos/all69
-- ----------------------------------------------------------------------------
-- Objetos NUEVOS e independientes (prefijo Legacy): no modifican vistas, funciones
-- ni procedimientos existentes. Convenciones:
--   * Un solo registro por RFC; el "principal" es el de 69-b / 69-b-bis (si existe),
--     luego el de fecha efectiva más reciente, prefiriendo el SAT sobre publicaciones-dof.
--   * Fechas como texto dd/MM/yyyy (formato de las publicaciones del SAT).
-- ============================================================================

-- Detalle por registro vigente con columnas por etapa (Presunción/Desvirtuados/Definitivos/Favorables)
-- y ranking del registro principal por RFC (Rn = 1).
CREATE OR ALTER VIEW dbo.vw_LegacyRfcDetalle
AS
WITH B AS (
    SELECT
        v.ContribuyenteId, v.Rfc, v.RazonSocial, v.Articulo, v.Listado, v.Situacion, v.FechaEfectiva,
        EsEfos = CASE WHEN v.Articulo IN ('69-b', '69-b-bis') THEN 1 ELSE 0 END,
        Etapa  = CASE WHEN v.Articulo NOT IN ('69-b', '69-b-bis') THEN NULL
                      WHEN v.Situacion LIKE N'%efinitiv%' THEN 'D'
                      WHEN v.Situacion LIKE N'%resunt%'   THEN 'P'
                      WHEN v.Situacion LIKE N'%esvirtu%'  THEN 'V'
                      WHEN v.Situacion LIKE N'%entencia%' THEN 'F' END,
        NumeroOficioSAT = NULLIF(v.NumeroOficioSAT, N''), FechaOficioSAT = v.FechaOficioSAT,
        NumeroOficioDOF = NULLIF(v.NumeroOficioDOF, N''), FechaOficioDOF = v.FechaOficioDOF,
        PresOfSAT = NULLIF(v.PresuncionOficioSAT, N''),   PresFSAT = v.PresuncionFechaSAT,
        PresOfDOF = NULLIF(v.PresuncionOficioDOF, N''),   PresFDOF = v.PresuncionFechaDOF,
        DesvOfSAT = NULLIF(v.DesvirtuadoOficioSAT, N''),  DesvFSAT = v.DesvirtuadoFechaSAT,
        DesvOfDOF = NULLIF(v.DesvirtuadoOficioDOF, N''),  DesvFDOF = v.DesvirtuadoFechaDOF,
        DefOfSAT  = NULLIF(v.DefinitivoOficioSAT, N''),   DefFSAT  = v.DefinitivoFechaSAT,
        DefOfDOF  = NULLIF(v.DefinitivoOficioDOF, N''),   DefFDOF  = v.DefinitivoFechaDOF,
        SentOfSAT = NULLIF(v.SentenciaOficioSAT, N''),    SentFSAT = v.SentenciaFechaSAT,
        SentOfDOF = NULLIF(v.SentenciaOficioDOF, N''),    SentFDOF = v.SentenciaFechaDOF
    FROM dbo.ContribuyenteVigente v
), S AS (
    SELECT B.*,
        folioPresuncionSAT          = COALESCE(PresOfSAT, CASE WHEN Etapa = 'P' THEN NumeroOficioSAT END),
        publicacionSATPresuncion    = COALESCE(CONVERT(varchar(10), PresFSAT, 103), CASE WHEN Etapa = 'P' THEN CONVERT(varchar(10), FechaOficioSAT, 103) END),
        folioPresuncionDOF          = COALESCE(PresOfDOF, CASE WHEN Etapa = 'P' THEN NumeroOficioDOF END),
        publicacionDOFPresuncion    = COALESCE(CONVERT(varchar(10), PresFDOF, 103), CASE WHEN Etapa = 'P' THEN CONVERT(varchar(10), FechaOficioDOF, 103) END),
        folioDesvirtuadosSAT        = COALESCE(DesvOfSAT, CASE WHEN Etapa = 'V' THEN NumeroOficioSAT END),
        publicacionSATDesvirtuados  = COALESCE(CONVERT(varchar(10), DesvFSAT, 103), CASE WHEN Etapa = 'V' THEN CONVERT(varchar(10), FechaOficioSAT, 103) END),
        folioDesvirtuadosDOF        = COALESCE(DesvOfDOF, CASE WHEN Etapa = 'V' THEN NumeroOficioDOF END),
        publicacionDOFDesvirtuados  = COALESCE(CONVERT(varchar(10), DesvFDOF, 103), CASE WHEN Etapa = 'V' THEN CONVERT(varchar(10), FechaOficioDOF, 103) END),
        folioDefinitivosSAT         = COALESCE(DefOfSAT, CASE WHEN Etapa = 'D' THEN NumeroOficioSAT END),
        publicacionSATDefinitivos   = COALESCE(CONVERT(varchar(10), DefFSAT, 103), CASE WHEN Etapa = 'D' THEN CONVERT(varchar(10), FechaOficioSAT, 103) END),
        folioDefinitivosDOF         = COALESCE(DefOfDOF, CASE WHEN Etapa = 'D' THEN NumeroOficioDOF END),
        publicacionDOFDefinitivos   = COALESCE(CONVERT(varchar(10), DefFDOF, 103), CASE WHEN Etapa = 'D' THEN CONVERT(varchar(10), FechaOficioDOF, 103) END),
        folioFavorablesSAT          = COALESCE(SentOfSAT, CASE WHEN Etapa = 'F' THEN NumeroOficioSAT END),
        publicacionSATFavorables    = COALESCE(CONVERT(varchar(10), SentFSAT, 103), CASE WHEN Etapa = 'F' THEN CONVERT(varchar(10), FechaOficioSAT, 103) END),
        folioFavorablesDOF          = COALESCE(SentOfDOF, CASE WHEN Etapa = 'F' THEN NumeroOficioDOF END),
        publicacionDOFFavorables    = COALESCE(CONVERT(varchar(10), SentFDOF, 103), CASE WHEN Etapa = 'F' THEN CONVERT(varchar(10), FechaOficioDOF, 103) END)
    FROM B
)
SELECT S.*,
    NumeroFechaOficioSAT = CASE Etapa WHEN 'P' THEN folioPresuncionSAT WHEN 'V' THEN folioDesvirtuadosSAT WHEN 'D' THEN folioDefinitivosSAT WHEN 'F' THEN folioFavorablesSAT ELSE NumeroOficioSAT END,
    PublicacionPaginaSAT = CASE Etapa WHEN 'P' THEN publicacionSATPresuncion WHEN 'V' THEN publicacionSATDesvirtuados WHEN 'D' THEN publicacionSATDefinitivos WHEN 'F' THEN publicacionSATFavorables ELSE CONVERT(varchar(10), FechaOficioSAT, 103) END,
    NumeroFechaOficioGlobal = CASE Etapa WHEN 'P' THEN folioPresuncionDOF WHEN 'V' THEN folioDesvirtuadosDOF WHEN 'D' THEN folioDefinitivosDOF WHEN 'F' THEN folioFavorablesDOF ELSE NumeroOficioDOF END,
    PublicacionDOF = CASE Etapa WHEN 'P' THEN publicacionDOFPresuncion WHEN 'V' THEN publicacionDOFDesvirtuados WHEN 'D' THEN publicacionDOFDefinitivos WHEN 'F' THEN publicacionDOFFavorables ELSE CONVERT(varchar(10), FechaOficioDOF, 103) END,
    Rn = ROW_NUMBER() OVER (
        PARTITION BY Rfc
        ORDER BY (1 - EsEfos),
                 CASE WHEN FechaEfectiva IS NULL THEN 1 ELSE 0 END,
                 FechaEfectiva DESC,
                 CASE WHEN Listado = 'publicaciones-dof' THEN 1 ELSE 0 END,
                 ContribuyenteId DESC)
FROM S;
GO

