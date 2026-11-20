SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- ----------------------------------------------------------------------------
-- 9. Procedimiento Almacenado de Delta e Ingesta Masiva SAT
-- ----------------------------------------------------------------------------
-- Este procedimiento procesa la tabla de staging (#ContribuyenteStaging) creada
-- durante el bulk copy del archivo descargado del SAT, comparando el HashFila.
-- Soporta múltiples registros por RFC (p. ej. cancelaciones con diferentes montos/fechas)
-- sin generar violaciones de clave y preservando toda la información sin pérdidas.
-- - Registros Idénticos: Solo actualizan la marca UltimaDescarga. CERO filas en Historial.
-- - Registros Nuevos: Se insertan en ContribuyenteVigente y ContribuyenteHistorial ('Alta Inicial' o 'Cambio Situacion').
-- - Registros Retirados: Se eliminan de Vigente si ya no vienen en la fuente oficial.
-- ----------------------------------------------------------------------------

CREATE OR ALTER PROCEDURE dbo.AplicarCambiosSAT
    @FuenteClave            varchar(100),
    @Articulo               varchar(16),
    @Listado                varchar(100),
    @UrlPortal              nvarchar(2048),
    @FechaPortal            date = NULL,
    @HashArchivo            char(64) = NULL,
    @TotalNuevos            int OUTPUT,
    @TotalModificados       int OUTPUT,
    @TotalSinCambios        int OUTPUT,
    @TotalVigentes          int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF OBJECT_ID('tempdb..#ContribuyenteStaging') IS NULL
    BEGIN
        THROW 50001, 'La tabla temporal #ContribuyenteStaging no fue encontrada.', 1;
    END;

    DECLARE @FechaHoraLocal datetime2(3) = SYSDATETIME();

    -- 1. Actualizar marca UltimaDescarga de registros existentes que coinciden exactamente en contenido (HashFila)
    UPDATE v
    SET v.UltimaDescarga = @FechaHoraLocal
    FROM dbo.ContribuyenteVigente v
    WHERE v.Articulo = @Articulo
      AND v.Listado = @Listado
      AND EXISTS (
          SELECT 1 
          FROM #ContribuyenteStaging s 
          WHERE s.HashFila = v.HashFila
      );

    SET @TotalSinCambios = @@ROWCOUNT;

    -- 2. Insertar en ContribuyenteVigente los registros de staging cuyo HashFila no exista actualmente en vigentes.
    -- Se deduplica en caso de que el archivo CSV contenga líneas 100% idénticas repetidas en el origen.
    WITH StagingDeduplicada AS (
        SELECT *,
               ROW_NUMBER() OVER (PARTITION BY HashFila ORDER BY (SELECT NULL)) AS rn
        FROM #ContribuyenteStaging
    )
    INSERT INTO dbo.ContribuyenteVigente (
        Rfc, RazonSocial, Articulo, Listado, Situacion, FechaEfectiva,
        OrigenUltimaModificacion, FechaModificacion, TipoPersona, EntidadFederativa,
        Monto, Ejercicio, Periodo, Motivo, FechaCancelacion, FechaAutorizacion,
        Administracion, NumeroOficioSAT, FechaOficioSAT, NumeroOficioDOF, FechaOficioDOF,
        PresuncionOficioSAT, PresuncionFechaSAT, PresuncionOficioDOF, PresuncionFechaDOF,
        DesvirtuadoOficioSAT, DesvirtuadoFechaSAT, DesvirtuadoOficioDOF, DesvirtuadoFechaDOF,
        DefinitivoOficioSAT, DefinitivoFechaSAT, DefinitivoOficioDOF, DefinitivoFechaDOF,
        SentenciaOficioSAT, SentenciaFechaSAT, SentenciaOficioDOF, SentenciaFechaDOF,
        HashFila, UltimaDescarga
    )
    SELECT 
        s.Rfc, s.RazonSocial, @Articulo, @Listado, s.Situacion, s.FechaEfectiva,
        'SAT', @FechaHoraLocal, s.TipoPersona, s.EntidadFederativa,
        s.Monto, s.Ejercicio, s.Periodo, s.Motivo, s.FechaCancelacion, s.FechaAutorizacion,
        s.Administracion, s.NumeroOficioSAT, s.FechaOficioSAT, s.NumeroOficioDOF, s.FechaOficioDOF,
        s.PresuncionOficioSAT, s.PresuncionFechaSAT, s.PresuncionOficioDOF, s.PresuncionFechaDOF,
        s.DesvirtuadoOficioSAT, s.DesvirtuadoFechaSAT, s.DesvirtuadoOficioDOF, s.DesvirtuadoFechaDOF,
        s.DefinitivoOficioSAT, s.DefinitivoFechaSAT, s.DefinitivoOficioDOF, s.DefinitivoFechaDOF,
        s.SentenciaOficioSAT, s.SentenciaFechaSAT, s.SentenciaOficioDOF, s.SentenciaFechaDOF,
        s.HashFila, @FechaHoraLocal
    FROM StagingDeduplicada s
    WHERE s.rn = 1
      AND NOT EXISTS (
          SELECT 1 
          FROM dbo.ContribuyenteVigente v 
          WHERE v.Articulo = @Articulo AND v.Listado = @Listado AND v.HashFila = s.HashFila
      );

    SET @TotalNuevos = @@ROWCOUNT;

    -- 3. Registrar eventos en el Historial para los registros nuevos agregados
    IF @TotalNuevos > 0
    BEGIN
        INSERT INTO dbo.ContribuyenteHistorial (
            Rfc, RazonSocial, Articulo, Listado, Situacion, FechaEfectiva,
            TipoEvento, Origen, FechaRegistro, CodNotaDOF, UrlOrigen,
            NumeroOficioSAT, FechaOficioSAT, NumeroOficioDOF, FechaOficioDOF,
            EntidadFederativa, Motivo, HashFila
        )
        SELECT 
            v.Rfc, v.RazonSocial, v.Articulo, v.Listado, v.Situacion, v.FechaEfectiva,
            CASE 
                WHEN EXISTS (
                    SELECT 1 
                    FROM dbo.ContribuyenteHistorial h 
                    WHERE h.Rfc = v.Rfc AND h.Articulo = v.Articulo AND h.Listado = v.Listado
                ) THEN 'Cambio Situacion'
                ELSE 'Alta Inicial'
            END,
            'SAT', @FechaHoraLocal, NULL, @UrlPortal,
            v.NumeroOficioSAT, v.FechaOficioSAT, v.NumeroOficioDOF, v.FechaOficioDOF,
            v.EntidadFederativa, v.Motivo, v.HashFila
        FROM dbo.ContribuyenteVigente v
        WHERE v.Articulo = @Articulo 
          AND v.Listado = @Listado 
          AND v.FechaModificacion = @FechaHoraLocal;
    END;

    -- 4. Retirar de Vigente los registros de esta fuente que ya no están presentes en la descarga oficial
    DELETE FROM dbo.ContribuyenteVigente
    WHERE Articulo = @Articulo AND Listado = @Listado AND UltimaDescarga < @FechaHoraLocal;

    SET @TotalModificados = 0;

    -- 5. Contar el total vigente actual de esta fuente
    SELECT @TotalVigentes = COUNT(*) 
    FROM dbo.ContribuyenteVigente 
    WHERE Articulo = @Articulo AND Listado = @Listado;

    -- 6. Actualizar el estado de ControlFuenteSAT
    UPDATE dbo.ControlFuenteSAT
    SET 
        FechaPublicacionPortal  = COALESCE(@FechaPortal, FechaPublicacionPortal),
        UltimoHashArchivo       = COALESCE(@HashArchivo, UltimoHashArchivo),
        UltimaDescargaExitosa   = @FechaHoraLocal,
        UltimoIntento           = @FechaHoraLocal,
        TotalRegistrosVigentes  = @TotalVigentes,
        TotalNuevos             = @TotalNuevos,
        TotalModificados        = @TotalModificados,
        TotalSinCambios         = @TotalSinCambios,
        Resultado               = CASE WHEN @TotalNuevos > 0 THEN 'cambios' ELSE 'sin-cambios' END,
        UltimoError             = NULL
    WHERE FuenteClave = @FuenteClave;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO dbo.ControlFuenteSAT (
            FuenteClave, Articulo, Listado, Nombre, UrlPortal, EsDocumento, Habilitada,
            FechaPublicacionPortal, UltimoHashArchivo, UltimaDescargaExitosa, UltimoIntento,
            TotalRegistrosVigentes, TotalNuevos, TotalModificados, TotalSinCambios, Resultado
        )
        VALUES (
            @FuenteClave, @Articulo, @Listado, @FuenteClave, @UrlPortal, 0, 1,
            @FechaPortal, @HashArchivo, @FechaHoraLocal, @FechaHoraLocal,
            @TotalVigentes, @TotalNuevos, @TotalModificados, @TotalSinCambios,
            CASE WHEN @TotalNuevos > 0 THEN 'cambios' ELSE 'sin-cambios' END
        );
    END;
END;
GO

