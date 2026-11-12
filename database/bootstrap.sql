SET XACT_ABORT ON;
GO

-- ============================================================================
-- MonitorRFC - Script de Inicialización y Estructura (Bootstrap)
-- ============================================================================
-- Define el modelo relacional normalizado y de alto rendimiento para SQL Server,
-- garantizando idempotencia, indentación limpia, columnas relacionales reales
-- (sin almacenamiento redundante de JSON ni tablas de desecho), control de
-- versiones por delta con hashing criptográfico y trazabilidad de eventos.
-- ============================================================================

-- ----------------------------------------------------------------------------
-- 1. Catálogos Base
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Articulo' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.Articulo (
        Clave   varchar(16) NOT NULL PRIMARY KEY,
        Orden   int NOT NULL
    );
END;
GO

-- Semilla de Artículos Fiscales
IF NOT EXISTS (SELECT 1 FROM dbo.Articulo WHERE Clave = '69')
    INSERT INTO dbo.Articulo (Clave, Orden) VALUES ('69', 1);

IF NOT EXISTS (SELECT 1 FROM dbo.Articulo WHERE Clave = '69-b')
    INSERT INTO dbo.Articulo (Clave, Orden) VALUES ('69-b', 2);

IF NOT EXISTS (SELECT 1 FROM dbo.Articulo WHERE Clave = '69-b-bis')
    INSERT INTO dbo.Articulo (Clave, Orden) VALUES ('69-b-bis', 3);

IF NOT EXISTS (SELECT 1 FROM dbo.Articulo WHERE Clave = '49-bis')
    INSERT INTO dbo.Articulo (Clave, Orden) VALUES ('49-bis', 4);
GO

-- ----------------------------------------------------------------------------
-- 2. Control Operativo de Fuentes SAT
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ControlFuenteSAT' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ControlFuenteSAT (
        FuenteClave             varchar(100) NOT NULL PRIMARY KEY,
        Articulo                varchar(16) NOT NULL REFERENCES dbo.Articulo(Clave),
        Listado                 varchar(100) NOT NULL,
        Nombre                  nvarchar(500) NOT NULL,
        UrlPortal               nvarchar(2048) NOT NULL,
        EsDocumento             bit NOT NULL DEFAULT 0,
        Habilitada              bit NOT NULL DEFAULT 1,
        FechaPublicacionPortal  date NULL,
        UltimoHashArchivo       char(64) NULL,
        UltimaDescargaExitosa   datetime2(3) NULL,
        UltimoIntento           datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
        TotalRegistrosVigentes  int NOT NULL DEFAULT 0,
        TotalNuevos             int NOT NULL DEFAULT 0,
        TotalModificados        int NOT NULL DEFAULT 0,
        TotalSinCambios         int NOT NULL DEFAULT 0,
        Resultado               varchar(30) NOT NULL DEFAULT 'Pendiente',
        UltimoError             nvarchar(2000) NULL
    );
END;
GO

-- ----------------------------------------------------------------------------
-- 3. Tabla Principal de Contribuyentes Vigentes (Estado Actual Relacional)
-- ----------------------------------------------------------------------------
-- Contiene las publicaciones vigentes de contribuyentes. Un contribuyente
-- puede poseer múltiples registros en un mismo listado (p. ej. diversos
-- créditos cancelados, multas reducidas o condonaciones con distintos montos/fechas).
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ContribuyenteVigente' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ContribuyenteVigente (
        ContribuyenteId             bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Rfc                         nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
        RazonSocial                 nvarchar(2000) NOT NULL,
        Articulo                    varchar(16) NOT NULL REFERENCES dbo.Articulo(Clave),
        Listado                     varchar(100) NOT NULL,
        Situacion                   nvarchar(2000) NOT NULL,
        FechaEfectiva               date NULL,
        OrigenUltimaModificacion    varchar(8) NOT NULL CHECK (OrigenUltimaModificacion IN ('SAT', 'DOF')),
        FechaModificacion           datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
        
        -- Atributos Relacionales SAT General (Artículo 69 y genéricos)
        TipoPersona                 varchar(20) NULL,
        EntidadFederativa           nvarchar(1000) NULL,
        Monto                       decimal(18,2) NULL,
        Ejercicio                   varchar(50) NULL,
        Periodo                     varchar(100) NULL,
        Motivo                      nvarchar(2000) NULL,
        FechaCancelacion            date NULL,
        FechaAutorizacion           date NULL,
        Administracion              nvarchar(500) NULL,
        NumeroOficioSAT             nvarchar(200) NULL,
        FechaOficioSAT              date NULL,
        NumeroOficioDOF             nvarchar(200) NULL,
        FechaOficioDOF              date NULL,

        -- Atributos Relacionales Etapas 69-B y 69-B Bis
        PresuncionOficioSAT         nvarchar(200) NULL,
        PresuncionFechaSAT          date NULL,
        PresuncionOficioDOF         nvarchar(200) NULL,
        PresuncionFechaDOF          date NULL,
        DesvirtuadoOficioSAT        nvarchar(200) NULL,
        DesvirtuadoFechaSAT         date NULL,
        DesvirtuadoOficioDOF        nvarchar(200) NULL,
        DesvirtuadoFechaDOF         date NULL,
        DefinitivoOficioSAT         nvarchar(200) NULL,
        DefinitivoFechaSAT          date NULL,
        DefinitivoOficioDOF         nvarchar(200) NULL,
        DefinitivoFechaDOF          date NULL,
        SentenciaOficioSAT          nvarchar(200) NULL,
        SentenciaFechaSAT           date NULL,
        SentenciaOficioDOF          nvarchar(200) NULL,
        SentenciaFechaDOF           date NULL,

        -- Control de Delta y Comparativa
        HashFila                    binary(32) NOT NULL,
        UltimaDescarga              datetime2(3) NOT NULL DEFAULT SYSDATETIME()
    );
END;
GO

-- Asegurar eliminación de restricción UNIQUE si fue creada previamente
IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_ContribuyenteVigente_RfcArticuloListado')
BEGIN
    ALTER TABLE dbo.ContribuyenteVigente DROP CONSTRAINT UQ_ContribuyenteVigente_RfcArticuloListado;
END;
GO

-- Índices de Alto Rendimiento para ContribuyenteVigente
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteVigente_Rfc' AND object_id = OBJECT_ID('dbo.ContribuyenteVigente'))
BEGIN
    CREATE INDEX IX_ContribuyenteVigente_Rfc 
    ON dbo.ContribuyenteVigente(Rfc)
    INCLUDE (RazonSocial, Articulo, Listado, Situacion, FechaEfectiva, OrigenUltimaModificacion);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteVigente_ArticuloListado' AND object_id = OBJECT_ID('dbo.ContribuyenteVigente'))
BEGIN
    CREATE INDEX IX_ContribuyenteVigente_ArticuloListado 
    ON dbo.ContribuyenteVigente(Articulo, Listado)
    INCLUDE (Rfc, Situacion, FechaEfectiva);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteVigente_ArticuloListadoHash' AND object_id = OBJECT_ID('dbo.ContribuyenteVigente'))
BEGIN
    CREATE INDEX IX_ContribuyenteVigente_ArticuloListadoHash 
    ON dbo.ContribuyenteVigente(Articulo, Listado, HashFila);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteVigente_FechaEfectiva' AND object_id = OBJECT_ID('dbo.ContribuyenteVigente'))
BEGIN
    CREATE INDEX IX_ContribuyenteVigente_FechaEfectiva 
    ON dbo.ContribuyenteVigente(FechaEfectiva DESC)
    INCLUDE (Rfc, Articulo, Listado, Situacion);
END;
GO

-- ----------------------------------------------------------------------------
-- 4. Tabla de Historial y Trazabilidad de Estados
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ContribuyenteHistorial' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ContribuyenteHistorial (
        HistorialId         bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Rfc                 nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
        RazonSocial         nvarchar(2000) NOT NULL,
        Articulo            varchar(16) NOT NULL REFERENCES dbo.Articulo(Clave),
        Listado             varchar(100) NOT NULL,
        Situacion           nvarchar(2000) NOT NULL,
        FechaEfectiva       date NULL,
        TipoEvento          varchar(50) NOT NULL CHECK (TipoEvento IN ('Alta Inicial', 'Cambio Situacion', 'Publicacion DOF', 'Actualizacion SAT')),
        Origen              varchar(8) NOT NULL CHECK (Origen IN ('SAT', 'DOF')),
        FechaRegistro       datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
        CodNotaDOF          bigint NULL,
        UrlOrigen           nvarchar(2048) NULL,
        NumeroOficioSAT     nvarchar(200) NULL,
        FechaOficioSAT      date NULL,
        NumeroOficioDOF     nvarchar(200) NULL,
        FechaOficioDOF      date NULL,
        EntidadFederativa   nvarchar(1000) NULL,
        Motivo              nvarchar(2000) NULL,
        HashFila            binary(32) NOT NULL
    );
END;
GO

-- Índices de Trazabilidad para ContribuyenteHistorial
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteHistorial_RfcArticulo' AND object_id = OBJECT_ID('dbo.ContribuyenteHistorial'))
BEGIN
    CREATE INDEX IX_ContribuyenteHistorial_RfcArticulo 
    ON dbo.ContribuyenteHistorial(Rfc, Articulo, FechaEfectiva DESC)
    INCLUDE (Situacion, TipoEvento, Origen, CodNotaDOF);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteHistorial_ArticuloListado' AND object_id = OBJECT_ID('dbo.ContribuyenteHistorial'))
BEGIN
    CREATE INDEX IX_ContribuyenteHistorial_ArticuloListado 
    ON dbo.ContribuyenteHistorial(Articulo, Listado, FechaRegistro DESC);
END;
GO

-- ----------------------------------------------------------------------------
-- 5. Control de Cobertura y Operación SIDOF / DOF
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ControlCoberturaDOF' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ControlCoberturaDOF (
        Fecha               date NOT NULL PRIMARY KEY,
        Completa            bit NOT NULL,
        FechaComprobacion   datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
        TotalNotasFiscales  int NOT NULL DEFAULT 0,
        Detalle             nvarchar(2000) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'NotaPendienteDOF' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.NotaPendienteDOF (
        CodNota             bigint NOT NULL PRIMARY KEY,
        Fecha               date NOT NULL,
        Estado              varchar(30) NOT NULL,
        Motivo              nvarchar(2000) NOT NULL,
        UltimoIntento       datetime2(3) NOT NULL DEFAULT SYSDATETIME()
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AdjuntoDOF' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.AdjuntoDOF (
        AdjuntoId           bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CodNota             bigint NOT NULL,
        Url                 nvarchar(2048) NOT NULL,
        Estado              varchar(30) NOT NULL,
        RutaArchivo         nvarchar(2048) NULL,
        Motivo              nvarchar(2000) NULL
    );
END;
GO

-- ----------------------------------------------------------------------------
-- 6. Documento Técnico Normativo Artículo 69 (Matriz Excel)
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'DocumentoTecnico69' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.DocumentoTecnico69 (
        HojaOrdinal         int NOT NULL,
        FilaOrdinal         int NOT NULL,
        ColumnaOrdinal      int NOT NULL,
        Hoja                nvarchar(255) NOT NULL,
        Valor               nvarchar(max) NULL,
        FechaActualizacion  datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
        PRIMARY KEY (HojaOrdinal, FilaOrdinal, ColumnaOrdinal)
    );
END;
GO

-- ----------------------------------------------------------------------------
-- 7. Operación del Servicio, Ejecuciones y Bloqueo Concurrente
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'BitacoraEjecucion' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.BitacoraEjecucion (
        EjecucionId         uniqueidentifier NOT NULL PRIMARY KEY,
        Inicio              datetime2(3) NOT NULL,
        Fin                 datetime2(3) NULL,
        Disparador          varchar(50) NOT NULL,
        Resultado           varchar(30) NOT NULL,
        ResumenJson         nvarchar(max) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'BloqueoServicio' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.BloqueoServicio (
        Recurso             varchar(100) NOT NULL PRIMARY KEY,
        Propietario         uniqueidentifier NOT NULL,
        FencingToken        bigint NOT NULL,
        Expira              datetime2(3) NOT NULL
    );
END;
GO

-- ----------------------------------------------------------------------------
-- 8. Tipos de Parámetro Definidos por el Usuario (TVP)
-- ----------------------------------------------------------------------------

IF TYPE_ID(N'dbo.RfcInput') IS NULL
BEGIN
    CREATE TYPE dbo.RfcInput AS TABLE (
        Rfc             nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
        OrdenEntrada    int NOT NULL
    );
END;
GO

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

-- ----------------------------------------------------------------------------
-- 10. Procedimiento Almacenado para Ingesta y Trazabilidad DOF
-- ----------------------------------------------------------------------------
-- Registra una publicación oficial del DOF en el historial del RFC y evalúa
-- si la fecha efectiva es más reciente que el estatus vigente actual para
-- actualizar ContribuyenteVigente.
-- ----------------------------------------------------------------------------

CREATE OR ALTER PROCEDURE dbo.AplicarPublicacionDOF
    @Rfc                nvarchar(100),
    @RazonSocial        nvarchar(2000),
    @Articulo           varchar(16),
    @Listado            varchar(100),
    @Situacion          nvarchar(2000),
    @FechaEfectiva      date,
    @CodNotaDOF         bigint,
    @UrlOrigen          nvarchar(2048),
    @NumeroOficioDOF    nvarchar(200) = NULL,
    @EntidadFederativa  nvarchar(1000) = NULL,
    @Motivo             nvarchar(2000) = NULL,
    @HashFila           binary(32)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @FechaHoraLocal datetime2(3) = SYSDATETIME();

    -- 1. Siempre registrar en el Historial para trazabilidad completa
    INSERT INTO dbo.ContribuyenteHistorial (
        Rfc, RazonSocial, Articulo, Listado, Situacion, FechaEfectiva,
        TipoEvento, Origen, FechaRegistro, CodNotaDOF, UrlOrigen,
        NumeroOficioSAT, FechaOficioSAT, NumeroOficioDOF, FechaOficioDOF,
        EntidadFederativa, Motivo, HashFila
    )
    VALUES (
        @Rfc, @RazonSocial, @Articulo, @Listado, @Situacion, @FechaEfectiva,
        'Publicacion DOF', 'DOF', @FechaHoraLocal, @CodNotaDOF, @UrlOrigen,
        NULL, NULL, @NumeroOficioDOF, @FechaEfectiva,
        @EntidadFederativa, @Motivo, @HashFila
    );

    -- 2. Evaluar y Actualizar/Insertar en ContribuyenteVigente
    IF NOT EXISTS (SELECT 1 FROM dbo.ContribuyenteVigente WHERE Rfc = @Rfc AND Articulo = @Articulo AND Listado = @Listado)
    BEGIN
        INSERT INTO dbo.ContribuyenteVigente (
            Rfc, RazonSocial, Articulo, Listado, Situacion, FechaEfectiva,
            OrigenUltimaModificacion, FechaModificacion, TipoPersona, EntidadFederativa,
            NumeroOficioDOF, FechaOficioDOF, HashFila, UltimaDescarga
        )
        VALUES (
            @Rfc, @RazonSocial, @Articulo, @Listado, @Situacion, @FechaEfectiva,
            'DOF', @FechaHoraLocal, NULL, @EntidadFederativa,
            @NumeroOficioDOF, @FechaEfectiva, @HashFila, @FechaHoraLocal
        );
    END
    ELSE
    BEGIN
        -- Si existe, se actualiza solo si la fecha del DOF es más reciente o igual a la fecha efectiva registrada
        UPDATE dbo.ContribuyenteVigente
        SET 
            RazonSocial                 = @RazonSocial,
            Situacion                   = @Situacion,
            FechaEfectiva               = @FechaEfectiva,
            OrigenUltimaModificacion    = 'DOF',
            FechaModificacion           = @FechaHoraLocal,
            NumeroOficioDOF             = COALESCE(@NumeroOficioDOF, NumeroOficioDOF),
            FechaOficioDOF              = @FechaEfectiva,
            EntidadFederativa           = COALESCE(@EntidadFederativa, EntidadFederativa),
            HashFila                    = @HashFila
        WHERE Rfc = @Rfc 
          AND Articulo = @Articulo 
          AND Listado = @Listado
          AND (@FechaEfectiva >= FechaEfectiva OR FechaEfectiva IS NULL);
    END;
END;
GO

-- ----------------------------------------------------------------------------
-- 11. Procedimientos Almacenados de Consulta / API Consolidada
-- ----------------------------------------------------------------------------

CREATE OR ALTER PROCEDURE dbo.ConsultarContribuyentesVigentes
    @Articulo       varchar(16) = NULL,
    @Listado        varchar(100) = NULL,
    @FiltrarRfc     bit = 0,
    @Rfcs           dbo.RfcInput READONLY
AS
BEGIN
    SET NOCOUNT ON;

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
        v.UltimaDescarga
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
END;
GO

CREATE OR ALTER PROCEDURE dbo.ConsultarContribuyenteHistorial
    @Rfc            nvarchar(100),
    @Articulo       varchar(16) = NULL,
    @Listado        varchar(100) = NULL
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
        h.Motivo
    FROM dbo.ContribuyenteHistorial h
    JOIN dbo.Articulo a ON a.Clave = h.Articulo
    WHERE h.Rfc = @Rfc
      AND (@Articulo IS NULL OR h.Articulo = @Articulo)
      AND (@Listado IS NULL OR h.Listado = @Listado)
    ORDER BY 
        a.Orden,
        CASE WHEN h.FechaEfectiva IS NULL THEN 1 ELSE 0 END,
        h.FechaEfectiva DESC,
        h.FechaRegistro DESC;
END;
GO

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

-- ----------------------------------------------------------------------------
-- 12. Procedimientos y Vistas de Compatibilidad Retroactiva con el API
-- ----------------------------------------------------------------------------
-- Permiten que los endpoints actuales de la API sigan consumiendo contratos
-- estables mientras se transita a las nuevas estructuras directas.
-- ----------------------------------------------------------------------------

CREATE OR ALTER PROCEDURE dbo.Encabezado
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 
        1 AS GeneracionId,
        SYSDATETIME() AS Creada,
        N'[]' AS FuentesJson,
        N'{}' AS CoberturaJson;
END;
GO

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
            v.ContribuyenteId           AS RegistroId,
            1                           AS VersionId,
            v.Listado                   AS FuenteKey,
            v.OrigenUltimaModificacion  AS Origen,
            v.Articulo                  AS Articulo,
            v.Listado                   AS Listado,
            v.Rfc                       AS Rfc,
            v.RazonSocial               AS NombreOriginal,
            v.Situacion                 AS EstadoOriginal,
            v.FechaEfectiva             AS FechaPublicacion,
            CONVERT(bit, 0)             AS Historica,
            CONVERT(bigint, NULL)       AS CodNota,
            N''                         AS Url,
            0                           AS TablaOrdinal,
            0                           AS FilaOrdinal,
            N'{}'                       AS DetalleJson
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
            h.HistorialId               AS RegistroId,
            1                           AS VersionId,
            h.Listado                   AS FuenteKey,
            h.Origen                    AS Origen,
            h.Articulo                  AS Articulo,
            h.Listado                   AS Listado,
            h.Rfc                       AS Rfc,
            h.RazonSocial               AS NombreOriginal,
            h.Situacion                 AS EstadoOriginal,
            h.FechaEfectiva             AS FechaPublicacion,
            CONVERT(bit, CASE WHEN h.TipoEvento = 'Alta Inicial' THEN 0 ELSE 1 END) AS Historica,
            h.CodNotaDOF                AS CodNota,
            COALESCE(h.UrlOrigen, N'')  AS Url,
            0                           AS TablaOrdinal,
            0                           AS FilaOrdinal,
            N'{}'                       AS DetalleJson
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

CREATE OR ALTER PROCEDURE dbo.DocumentoTecnico
    @GeneracionId   bigint = 1
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 
        1 AS VersionId,
        HojaOrdinal,
        FilaOrdinal,
        ColumnaOrdinal,
        Hoja,
        Valor
    FROM dbo.DocumentoTecnico69
    ORDER BY HojaOrdinal, FilaOrdinal, ColumnaOrdinal;
END;
GO

CREATE OR ALTER PROCEDURE dbo.EstadoFuentes
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.ConsultarEstatusGeneral;
END;
GO

CREATE OR ALTER PROCEDURE dbo.PublicarGeneracion
    @Propietario    uniqueidentifier,
    @Token          bigint
AS
BEGIN
    SET NOCOUNT ON;
    SELECT 1 AS GeneracionId;
END;
GO
