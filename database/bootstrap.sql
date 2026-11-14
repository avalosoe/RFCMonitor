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

        -- Trazabilidad DOF de origen vigente
        CodNotaDOF                  bigint NULL,
        UrlOrigenDOF                nvarchar(2048) NULL,

        -- Control de Delta y Comparativa
        HashFila                    binary(32) NOT NULL,
        UltimaDescarga              datetime2(3) NOT NULL DEFAULT SYSDATETIME()
    );
END;
GO

-- Asegurar columnas de trazabilidad DOF si la tabla ya existía
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ContribuyenteVigente') AND name = 'CodNotaDOF')
BEGIN
    ALTER TABLE dbo.ContribuyenteVigente ADD CodNotaDOF bigint NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ContribuyenteVigente') AND name = 'UrlOrigenDOF')
BEGIN
    ALTER TABLE dbo.ContribuyenteVigente ADD UrlOrigenDOF nvarchar(2048) NULL;
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

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteVigente_CodNotaDOF' AND object_id = OBJECT_ID('dbo.ContribuyenteVigente'))
BEGIN
    CREATE INDEX IX_ContribuyenteVigente_CodNotaDOF 
    ON dbo.ContribuyenteVigente(CodNotaDOF)
    WHERE CodNotaDOF IS NOT NULL;
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

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteHistorial_CodNotaDOF' AND object_id = OBJECT_ID('dbo.ContribuyenteHistorial'))
BEGIN
    CREATE INDEX IX_ContribuyenteHistorial_CodNotaDOF 
    ON dbo.ContribuyenteHistorial(CodNotaDOF)
    WHERE CodNotaDOF IS NOT NULL;
END;
GO

-- ----------------------------------------------------------------------------
-- 5. Publicaciones Oficiales y Control SIDOF / DOF
-- ----------------------------------------------------------------------------
-- Almacena los decretos y notas publicadas en el DOF a nivel maestro para
-- auditoría legal, trazabilidad forense y enlace directo con contribuyentes extraídos.
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PublicacionDOF' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PublicacionDOF (
        CodNota                 bigint NOT NULL PRIMARY KEY,
        Titulo                  nvarchar(1000) NOT NULL,
        FechaPublicacion        date NOT NULL,
        Edicion                 varchar(50) NULL,
        Organismo               nvarchar(500) NULL,
        Articulo                varchar(16) NOT NULL REFERENCES dbo.Articulo(Clave),
        UrlNota                 nvarchar(2048) NOT NULL,
        HashArchivo             varchar(64) NULL,
        RutaArchivo             nvarchar(2048) NULL,
        TotalContribuyentes     int NOT NULL DEFAULT 0,
        FechaRegistro           datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
        CodDiario               bigint NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PublicacionDOF') AND name = 'CodDiario')
BEGIN
    ALTER TABLE dbo.PublicacionDOF ADD CodDiario bigint NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PublicacionDOF_FechaPublicacion' AND object_id = OBJECT_ID('dbo.PublicacionDOF'))
BEGIN
    CREATE INDEX IX_PublicacionDOF_FechaPublicacion 
    ON dbo.PublicacionDOF(FechaPublicacion DESC, Articulo);
END;
GO

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

-- Trabajos de sincronización DOF on-demand (API). Estado y avance persistentes para consultar tras un reinicio.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TrabajoDOF' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.TrabajoDOF (
        TrabajoId           uniqueidentifier NOT NULL PRIMARY KEY,
        FechaInicio         date NOT NULL,
        FechaFin            date NOT NULL,
        Articulo            varchar(16) NULL,
        Estado              varchar(20) NOT NULL CHECK (Estado IN ('en_cola', 'en_curso', 'completado', 'error', 'interrumpido')),
        DiaActual           int NOT NULL DEFAULT 0,
        TotalDias           int NOT NULL DEFAULT 0,
        Mensaje             nvarchar(1000) NULL,
        ResultadoJson       nvarchar(max) NULL,
        Error               nvarchar(2000) NULL,
        FechaCreacion       datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
        FechaInicioProceso  datetime2(3) NULL,
        FechaTermino        datetime2(3) NULL,
        FechaActualizacion  datetime2(3) NOT NULL DEFAULT SYSDATETIME()
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
            NumeroOficioDOF, FechaOficioDOF, CodNotaDOF, UrlOrigenDOF, HashFila, UltimaDescarga
        )
        VALUES (
            @Rfc, @RazonSocial, @Articulo, @Listado, @Situacion, @FechaEfectiva,
            'DOF', @FechaHoraLocal, NULL, @EntidadFederativa,
            @NumeroOficioDOF, @FechaEfectiva, @CodNotaDOF, @UrlOrigen, @HashFila, @FechaHoraLocal
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
            CodNotaDOF                  = @CodNotaDOF,
            UrlOrigenDOF                = @UrlOrigen,
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
        h.Motivo,
        p.Titulo AS TituloDOF,
        p.Edicion AS EdicionDOF,
        p.CodDiario AS CodDiarioDOF,
        p.FechaPublicacion AS FechaPublicacionDOF
    FROM dbo.ContribuyenteHistorial h
    JOIN dbo.Articulo a ON a.Clave = h.Articulo
    LEFT JOIN dbo.PublicacionDOF p ON p.CodNota = h.CodNotaDOF
    WHERE h.Rfc = @Rfc
      AND (@Articulo IS NULL OR h.Articulo = @Articulo)
      AND (@Listado IS NULL OR h.Listado = @Listado)
    ORDER BY 
        CASE WHEN h.FechaEfectiva IS NULL THEN 1 ELSE 0 END,
        h.FechaEfectiva ASC,
        h.FechaRegistro ASC;
END;
GO

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
        CAST(1 AS bigint) AS GeneracionId,
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
        CAST(1 AS bigint) AS VersionId,
        HojaOrdinal,
        FilaOrdinal,
        ColumnaOrdinal,
        Hoja,
        Valor
    FROM dbo.DocumentoTecnico69 WITH (NOLOCK)
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
    SELECT CAST(1 AS bigint) AS GeneracionId;
END;
GO

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

-- Un renglón por RFC con los artículos en que aparece, concatenados:
-- "69-b, 69 (Cancelados), 49-bis" o "69 (Cancelados, Exigibles)". El artículo 69 lleva entre paréntesis sus listados del SAT
-- siempre que el RFC aparezca en al menos un listado del SAT del 69 (uno o varios).
CREATE OR ALTER VIEW dbo.vw_LegacyRfcArticulos
AS
WITH L AS (
    SELECT DISTINCT v.Rfc, v.Articulo, v.Listado FROM dbo.ContribuyenteVigente v
), N AS (
    SELECT l.Rfc, l.Articulo,
           NombreListados = STRING_AGG(CAST(COALESCE(c.Nombre, l.Listado) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY COALESCE(c.Nombre, l.Listado)),
           TotalListados  = COUNT(*)
    FROM L l
    LEFT JOIN dbo.ControlFuenteSAT c ON c.Articulo = l.Articulo AND c.Listado = l.Listado AND c.EsDocumento = 0
    WHERE l.Listado <> 'publicaciones-dof'
    GROUP BY l.Rfc, l.Articulo
), A AS (
    SELECT l.Rfc, l.Articulo,
           Orden = CASE l.Articulo WHEN '69-b' THEN 1 WHEN '69-b-bis' THEN 2 WHEN '69' THEN 3 ELSE 4 END
    FROM L l
    GROUP BY l.Rfc, l.Articulo
)
SELECT a.Rfc,
       Articulo = STRING_AGG(CAST(CASE WHEN a.Articulo = '69' AND n.NombreListados IS NOT NULL
                                       THEN a.Articulo + N' (' + n.NombreListados + N')'
                                       ELSE a.Articulo END AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY a.Orden)
FROM A a
LEFT JOIN N n ON n.Rfc = a.Rfc AND n.Articulo = a.Articulo
GROUP BY a.Rfc;
GO

-- No localizados (art. 69): primera publicación y entidad por RFC.
CREATE OR ALTER VIEW dbo.vw_LegacyNoLocalizados
AS
SELECT v.Rfc,
       FechaPrimeraPublicacion = MIN(v.FechaEfectiva),
       EntidadFederativa       = MAX(v.EntidadFederativa)
FROM dbo.ContribuyenteVigente v
WHERE v.Articulo = '69' AND v.Listado = 'no-localizados'
GROUP BY v.Rfc;
GO

-- Una fila con los metadatos de las fuentes (SAT 69-b y no localizados) para los encabezados.
CREATE OR ALTER VIEW dbo.vw_LegacyInfoFuentes
AS
SELECT
    FechaPortal69b      = (SELECT MAX(c.FechaPublicacionPortal) FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69-b' AND c.EsDocumento = 0),
    UltimaDescarga69b   = (SELECT MAX(c.UltimaDescargaExitosa)  FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69-b' AND c.EsDocumento = 0),
    UltimoIntento69b    = (SELECT MAX(c.UltimoIntento) FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69-b' AND c.EsDocumento = 0),
    FechaPortalNL       = (SELECT MAX(c.FechaPublicacionPortal) FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69' AND c.Listado = 'no-localizados'),
    UltimaDescargaNL    = (SELECT MAX(c.UltimaDescargaExitosa)  FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69' AND c.Listado = 'no-localizados'),
    UltimoIntentoNL     = (SELECT MAX(c.UltimoIntento) FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69' AND c.Listado = 'no-localizados'),
    FechaPortalBis      = (SELECT MAX(c.FechaPublicacionPortal) FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69-b-bis' AND c.EsDocumento = 0),
    UltimaDescargaBis   = (SELECT MAX(c.UltimaDescargaExitosa)  FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69-b-bis' AND c.EsDocumento = 0),
    UltimoIntentoBis    = (SELECT MAX(c.UltimoIntento) FROM dbo.ControlFuenteSAT c WHERE c.Articulo = '69-b-bis' AND c.EsDocumento = 0);
GO

-- POST /api/Rfc/Edos-Efos: consulta por RFC (sin duplicados). 1er resultado: metadatos; 2do: datos.
CREATE OR ALTER PROCEDURE dbo.PA_Legacy_EdosEfos_Consultar
    @Rfcs dbo.RfcInput READONLY
AS
BEGIN
    SET NOCOUNT ON;

    SELECT FechaPortal69b, UltimaDescarga69b, UltimoIntento69b, FechaPortalNL, UltimaDescargaNL, UltimoIntentoNL, FechaPortalBis, UltimaDescargaBis, UltimoIntentoBis FROM dbo.vw_LegacyInfoFuentes;

    SELECT
        d.Rfc                                   AS rfc,
        d.RazonSocial                           AS nombreContribuyente,
        d.Situacion                             AS estatus,
        d.NumeroFechaOficioSAT                  AS numeroFechaOficioSAT,
        d.PublicacionPaginaSAT                  AS publicacionPaginaSAT,
        d.NumeroFechaOficioGlobal               AS numeroFechaOficioGlobal,
        d.PublicacionDOF                        AS publicacionDOF,
        a.Articulo                              AS articulo
    FROM @Rfcs r
    JOIN dbo.vw_LegacyRfcDetalle d ON d.Rfc = r.Rfc AND d.Rn = 1
    JOIN dbo.vw_LegacyRfcArticulos a ON a.Rfc = r.Rfc
    ORDER BY r.OrdenEntrada
    OPTION (RECOMPILE);
END;
GO

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

-- GET /api/EdosEfos/all69: todos los RFC de todos los artículos, paginado por número de página.
-- Resultados: 1) metadatos, 2) total del universo filtrado, 3) datos de la página.
CREATE OR ALTER PROCEDURE dbo.PA_Legacy_EdosEfos_All69
    @Pagina int = 1,
    @TamanoPagina int = 10000,
    @Articulo varchar(16) = NULL,
    @Listado varchar(100) = NULL,
    @Situacion nvarchar(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @Pagina < 1
        THROW 50002, '@Pagina debe ser mayor o igual a 1.', 1;
    IF @TamanoPagina NOT BETWEEN 1 AND 10000
        THROW 50003, '@TamanoPagina debe estar entre 1 y 10000.', 1;
    DECLARE @Omitir bigint = (CONVERT(bigint, @Pagina) - 1) * @TamanoPagina;

    SELECT FechaPortal69b, UltimaDescarga69b, UltimoIntento69b, FechaPortalNL, UltimaDescargaNL, UltimoIntentoNL, FechaPortalBis, UltimaDescargaBis, UltimoIntentoBis FROM dbo.vw_LegacyInfoFuentes;

    SELECT COUNT_BIG(*) AS TotalRegistros
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
