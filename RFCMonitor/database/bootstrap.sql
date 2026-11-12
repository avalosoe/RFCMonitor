SET XACT_ABORT ON;
GO

-- ============================================================================
-- MonitorRFC - Script de Inicialización y Estructura (Bootstrap)
-- ============================================================================
-- Define el modelo relacional completo para SQL Server, garantizando
-- idempotencia, soporte para identificadores ampliados (RFC hasta 100 caracteres
-- y valores no estructurados), nombres de contribuyente y estatus extendidos.
-- ============================================================================

-- ----------------------------------------------------------------------------
-- 1. Tablas de Catálogo y Configuración
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Articulo' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.Articulo (
        Clave varchar(16) NOT NULL PRIMARY KEY,
        Orden int NOT NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Fuente' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.Fuente (
        FuenteKey nvarchar(100) NOT NULL PRIMARY KEY,
        Origen varchar(8) NOT NULL,
        Articulo varchar(16) NULL REFERENCES dbo.Articulo(Clave),
        Listado varchar(100) NOT NULL,
        Nombre nvarchar(500) NOT NULL,
        Url nvarchar(2048) NOT NULL,
        EsDocumento bit NOT NULL DEFAULT 0,
        Habilitada bit NOT NULL DEFAULT 1
    );
END;
GO

-- ----------------------------------------------------------------------------
-- 2. Tablas de Ingesta y Versiones de Publicaciones
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Publicacion' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.Publicacion (
        PublicacionId bigint IDENTITY(1,1) PRIMARY KEY,
        FuenteKey nvarchar(100) NOT NULL REFERENCES dbo.Fuente(FuenteKey),
        ClaveExterna nvarchar(100) NOT NULL,
        PrimeraDeteccion datetime2(3) NOT NULL,
        VersionActivaId bigint NULL,
        CONSTRAINT UQ_mrf_Publicacion UNIQUE (FuenteKey, ClaveExterna)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PublicacionVersion' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PublicacionVersion (
        VersionId bigint IDENTITY(1,1) PRIMARY KEY,
        PublicacionId bigint NOT NULL REFERENCES dbo.Publicacion(PublicacionId),
        HashContenido char(64) NOT NULL,
        ParserVersion varchar(40) NOT NULL,
        Url nvarchar(2048) NOT NULL,
        EvidenciaRuta nvarchar(2048) NOT NULL,
        FechaFuente date NULL,
        AlcanceFecha varchar(30) NOT NULL,
        Detectada datetime2(3) NOT NULL,
        Incorporada datetime2(3) NULL,
        Estado varchar(20) NOT NULL DEFAULT 'preparando' CHECK (Estado IN ('preparando', 'publicada')),
        Filas bigint NOT NULL DEFAULT 0,
        FilasLeidas bigint NOT NULL DEFAULT 0,
        FilasRechazadas bigint NOT NULL DEFAULT 0,
        Certificacion varchar(30) NOT NULL DEFAULT 'pendiente' CHECK (Certificacion IN ('certificada', 'requiere-revision', 'pendiente')),
        CodNota bigint NULL,
        Titulo nvarchar(max) NULL,
        Edicion nvarchar(100) NULL,
        Organismo nvarchar(1000) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_mrf_ActiveVersion')
BEGIN
    ALTER TABLE dbo.Publicacion
    ADD CONSTRAINT FK_mrf_ActiveVersion
    FOREIGN KEY (VersionActivaId) REFERENCES dbo.PublicacionVersion(VersionId);
END;
GO

-- ----------------------------------------------------------------------------
-- 3. Detalle de Registros, Archivos Fuente y Documentos
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RegistroFuente' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.RegistroFuente (
        RegistroId bigint IDENTITY(1,1) PRIMARY KEY,
        VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId),
        TablaOrdinal int NOT NULL,
        FilaOrdinal int NOT NULL,
        Rfc nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
        NombreOriginal nvarchar(2000) NOT NULL,
        Articulo varchar(16) NOT NULL REFERENCES dbo.Articulo(Clave),
        Listado varchar(100) NOT NULL,
        EstadoOriginal nvarchar(2000) NOT NULL,
        FechaPublicacion date NULL,
        EntidadOriginal nvarchar(1000) NULL,
        DetalleJson nvarchar(max) NOT NULL CHECK (ISJSON(DetalleJson) = 1),
        CONSTRAINT UQ_mrf_Fila UNIQUE (VersionId, TablaOrdinal, FilaOrdinal)
    );
END
ELSE
BEGIN
    -- Asegurar ampliación de campos si la tabla ya existía
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.RegistroFuente') AND name = 'Rfc' AND max_length < 200)
    BEGIN
        ALTER TABLE dbo.RegistroFuente ALTER COLUMN Rfc nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL;
    END;
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.RegistroFuente') AND name = 'NombreOriginal' AND max_length < 4000)
    BEGIN
        ALTER TABLE dbo.RegistroFuente ALTER COLUMN NombreOriginal nvarchar(2000) NOT NULL;
    END;
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.RegistroFuente') AND name = 'EstadoOriginal' AND max_length < 4000)
    BEGIN
        ALTER TABLE dbo.RegistroFuente ALTER COLUMN EstadoOriginal nvarchar(2000) NOT NULL;
    END;
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.RegistroFuente') AND name = 'EntidadOriginal' AND max_length < 2000)
    BEGIN
        ALTER TABLE dbo.RegistroFuente ALTER COLUMN EntidadOriginal nvarchar(1000) NULL;
    END;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'FilaFuente' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.FilaFuente (
        VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId),
        TablaOrdinal int NOT NULL,
        FilaOrdinal int NOT NULL,
        RfcOriginal nvarchar(500) NULL,
        EsValida bit NOT NULL,
        MotivoRechazo nvarchar(2000) NULL,
        DatosOriginalesJson nvarchar(max) NOT NULL CHECK (ISJSON(DatosOriginalesJson) = 1),
        PRIMARY KEY (VersionId, TablaOrdinal, FilaOrdinal)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'DocumentoCelda' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.DocumentoCelda (
        VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId),
        HojaOrdinal int NOT NULL,
        FilaOrdinal int NOT NULL,
        ColumnaOrdinal int NOT NULL,
        Hoja nvarchar(255) NOT NULL,
        Valor nvarchar(max) NULL,
        PRIMARY KEY (VersionId, HojaOrdinal, FilaOrdinal, ColumnaOrdinal)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ObservacionPublicacion' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ObservacionPublicacion (
        ObservacionId bigint IDENTITY(1,1) PRIMARY KEY,
        VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId),
        Observada datetime2(3) NOT NULL,
        FechaFuente date NULL,
        AlcanceFecha varchar(30) NOT NULL,
        Url nvarchar(2048) NOT NULL,
        SinCambios bit NOT NULL
    );
END;
GO

-- ----------------------------------------------------------------------------
-- 4. Operación del Servicio, Ejecuciones y Bloqueos
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Ejecucion' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.Ejecucion (
        EjecucionId uniqueidentifier NOT NULL PRIMARY KEY,
        Inicio datetime2(3) NOT NULL,
        Fin datetime2(3) NOT NULL,
        Resultado varchar(30) NOT NULL,
        Disparador varchar(30) NOT NULL,
        ResumenJson nvarchar(max) NOT NULL CHECK (ISJSON(ResumenJson) = 1)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'FuenteEstado' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.FuenteEstado (
        FuenteKey nvarchar(100) NOT NULL PRIMARY KEY,
        UltimoIntento datetime2(3) NOT NULL,
        UltimoExito datetime2(3) NULL,
        Resultado varchar(30) NOT NULL,
        ErrorSeguro nvarchar(2000) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'CoberturaDof' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.CoberturaDof (
        Fecha date NOT NULL PRIMARY KEY,
        Completa bit NOT NULL,
        Comprobada datetime2(3) NOT NULL,
        Detalle nvarchar(2000) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ObjetivoCobertura' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ObjetivoCobertura (
        Id tinyint PRIMARY KEY CHECK (Id = 1),
        DofDesde date NOT NULL,
        DofHasta date NOT NULL,
        DofHabilitado bit NOT NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'NotaPendiente' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.NotaPendiente (
        CodNota bigint NOT NULL PRIMARY KEY,
        Fecha date NOT NULL,
        Estado varchar(30) NOT NULL,
        Motivo nvarchar(2000) NOT NULL,
        UltimoIntento datetime2(3) NOT NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Adjunto' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.Adjunto (
        AdjuntoId bigint IDENTITY(1,1) PRIMARY KEY,
        CodNota bigint NOT NULL,
        Url nvarchar(2048) NOT NULL,
        Estado varchar(30) NOT NULL,
        EvidenciaRuta nvarchar(2048) NULL,
        Motivo nvarchar(2000) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Lease' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.Lease (
        Recurso varchar(100) NOT NULL PRIMARY KEY,
        Propietario uniqueidentifier NOT NULL,
        FencingToken bigint NOT NULL,
        Expira datetime2(3) NOT NULL
    );
END;
GO

-- ----------------------------------------------------------------------------
-- 5. Generaciones Snapshot de la API
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Generacion' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.Generacion (
        GeneracionId bigint IDENTITY(1,1) PRIMARY KEY,
        Creada datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
        FuentesJson nvarchar(max) NOT NULL CHECK (ISJSON(FuentesJson) = 1),
        CoberturaJson nvarchar(max) NOT NULL CHECK (ISJSON(CoberturaJson) = 1)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'GeneracionVersion' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.GeneracionVersion (
        GeneracionId bigint NOT NULL REFERENCES dbo.Generacion(GeneracionId),
        VersionId bigint NOT NULL REFERENCES dbo.PublicacionVersion(VersionId),
        EsActiva bit NOT NULL,
        PRIMARY KEY (GeneracionId, VersionId)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'GeneracionActiva' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.GeneracionActiva (
        Id tinyint NOT NULL PRIMARY KEY CHECK (Id = 1),
        GeneracionId bigint NOT NULL REFERENCES dbo.Generacion(GeneracionId)
    );
END;
GO

-- ----------------------------------------------------------------------------
-- 6. Tipos Definidos por el Usuario
-- ----------------------------------------------------------------------------

IF TYPE_ID(N'dbo.RfcInput') IS NULL
BEGIN
    CREATE TYPE dbo.RfcInput AS TABLE (
        Rfc nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
        OrdenEntrada int NOT NULL
    );
END;
GO

-- ----------------------------------------------------------------------------
-- 7. Índices de Rendimiento
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_mrf_Registro_VersionRfc' AND object_id = OBJECT_ID('dbo.RegistroFuente'))
BEGIN
    CREATE INDEX IX_mrf_Registro_VersionRfc 
    ON dbo.RegistroFuente(VersionId, Rfc, Articulo, Listado);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_mrf_Registro_Rfc' AND object_id = OBJECT_ID('dbo.RegistroFuente'))
BEGIN
    CREATE INDEX IX_mrf_Registro_Rfc 
    ON dbo.RegistroFuente(Rfc, VersionId);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_mrf_GeneracionVersion_Activa' AND object_id = OBJECT_ID('dbo.GeneracionVersion'))
BEGIN
    CREATE INDEX IX_mrf_GeneracionVersion_Activa 
    ON dbo.GeneracionVersion(GeneracionId, VersionId) 
    WHERE EsActiva = 1;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_mrf_Version_Publicacion' AND object_id = OBJECT_ID('dbo.PublicacionVersion'))
BEGIN
    CREATE INDEX IX_mrf_Version_Publicacion 
    ON dbo.PublicacionVersion(PublicacionId, VersionId);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_mrf_FilaFuente_Rechazo' AND object_id = OBJECT_ID('dbo.FilaFuente'))
BEGIN
    CREATE INDEX IX_mrf_FilaFuente_Rechazo 
    ON dbo.FilaFuente(VersionId, EsValida) 
    INCLUDE(FilaOrdinal, RfcOriginal, MotivoRechazo);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_mrf_ObservacionVersion' AND object_id = OBJECT_ID('dbo.ObservacionPublicacion'))
BEGIN
    CREATE INDEX IX_mrf_ObservacionVersion 
    ON dbo.ObservacionPublicacion(VersionId, Observada DESC);
END;
GO

-- ----------------------------------------------------------------------------
-- 8. Datos Semilla (Artículos Fiscales)
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM dbo.Articulo WHERE Clave = '69')
    INSERT INTO dbo.Articulo(Clave, Orden) VALUES ('69', 1);

IF NOT EXISTS (SELECT 1 FROM dbo.Articulo WHERE Clave = '69-b')
    INSERT INTO dbo.Articulo(Clave, Orden) VALUES ('69-b', 2);

IF NOT EXISTS (SELECT 1 FROM dbo.Articulo WHERE Clave = '69-b-bis')
    INSERT INTO dbo.Articulo(Clave, Orden) VALUES ('69-b-bis', 3);

IF NOT EXISTS (SELECT 1 FROM dbo.Articulo WHERE Clave = '49-bis')
    INSERT INTO dbo.Articulo(Clave, Orden) VALUES ('49-bis', 4);
GO

-- ----------------------------------------------------------------------------
-- 9. Funciones y Vistas
-- ----------------------------------------------------------------------------

CREATE OR ALTER FUNCTION dbo.FilasGeneracion(
    @GeneracionId bigint,
    @Historico bit
)
RETURNS TABLE 
AS 
RETURN
    SELECT 
        r.RegistroId,
        r.VersionId,
        r.TablaOrdinal,
        r.FilaOrdinal,
        r.Rfc,
        r.NombreOriginal,
        r.Articulo,
        r.Listado,
        r.EstadoOriginal,
        r.FechaPublicacion,
        r.EntidadOriginal,
        r.DetalleJson,
        gv.EsActiva,
        p.FuenteKey,
        f.Origen,
        v.CodNota,
        v.Url
    FROM dbo.GeneracionVersion gv
    JOIN dbo.PublicacionVersion v ON v.VersionId = gv.VersionId
    JOIN dbo.Publicacion p ON p.PublicacionId = v.PublicacionId
    JOIN dbo.Fuente f ON f.FuenteKey = p.FuenteKey
    JOIN dbo.RegistroFuente r ON r.VersionId = v.VersionId
    WHERE gv.GeneracionId = @GeneracionId
      AND (@Historico = 1 OR gv.EsActiva = 1);
GO

CREATE OR ALTER VIEW dbo.Vigentes 
AS
    SELECT r.* 
    FROM dbo.GeneracionActiva a 
    CROSS APPLY dbo.FilasGeneracion(a.GeneracionId, 0) r 
    WHERE a.Id = 1;
GO

-- ----------------------------------------------------------------------------
-- 10. Procedimientos Almacenados de Consulta y API
-- ----------------------------------------------------------------------------

CREATE OR ALTER PROCEDURE dbo.Encabezado
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 
        g.GeneracionId,
        g.Creada,
        g.FuentesJson,
        g.CoberturaJson 
    FROM dbo.GeneracionActiva a 
    JOIN dbo.Generacion g ON g.GeneracionId = a.GeneracionId 
    WHERE a.Id = 1;
END;
GO

CREATE OR ALTER PROCEDURE dbo.Apariciones
    @GeneracionId bigint,
    @Articulo varchar(16) = NULL,
    @Listado varchar(100) = NULL,
    @Historico bit = 0,
    @FiltrarRfc bit = 0,
    @Rfcs dbo.RfcInput READONLY
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 
        r.RegistroId,
        r.VersionId,
        r.FuenteKey,
        r.Origen,
        r.Articulo,
        r.Listado,
        r.Rfc,
        r.NombreOriginal,
        r.EstadoOriginal,
        r.FechaPublicacion,
        CONVERT(bit, 1 - r.EsActiva) AS Historica,
        r.CodNota,
        r.Url,
        r.TablaOrdinal,
        r.FilaOrdinal,
        r.DetalleJson
    FROM dbo.FilasGeneracion(@GeneracionId, @Historico) r 
    JOIN dbo.Articulo a ON a.Clave = r.Articulo
    WHERE (@Articulo IS NULL OR r.Articulo = @Articulo) 
      AND (@Listado IS NULL OR r.Listado = @Listado)
      AND (@FiltrarRfc = 0 OR EXISTS (SELECT 1 FROM @Rfcs i WHERE i.Rfc = r.Rfc))
    ORDER BY 
        r.Rfc COLLATE Latin1_General_100_BIN2,
        a.Orden,
        CASE WHEN r.FechaPublicacion IS NULL THEN 1 ELSE 0 END,
        r.FechaPublicacion DESC,
        r.Origen,
        r.Listado,
        r.VersionId,
        r.TablaOrdinal,
        r.FilaOrdinal,
        r.RegistroId 
    OPTION(RECOMPILE);
END;
GO

CREATE OR ALTER PROCEDURE dbo.Conteos
    @GeneracionId bigint,
    @Articulo varchar(16) = NULL,
    @Listado varchar(100) = NULL,
    @Historico bit = 0,
    @FiltrarRfc bit = 0,
    @Rfcs dbo.RfcInput READONLY
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 
        COUNT_BIG(DISTINCT r.Rfc) AS Rfcs,
        COUNT_BIG(*) AS Apariciones 
    FROM dbo.FilasGeneracion(@GeneracionId, @Historico) r
    WHERE (@Articulo IS NULL OR r.Articulo = @Articulo) 
      AND (@Listado IS NULL OR r.Listado = @Listado)
      AND (@FiltrarRfc = 0 OR EXISTS (SELECT 1 FROM @Rfcs i WHERE i.Rfc = r.Rfc)) 
    OPTION(RECOMPILE);
END;
GO

CREATE OR ALTER PROCEDURE dbo.DocumentoTecnico
    @GeneracionId bigint
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 
        c.VersionId,
        c.HojaOrdinal,
        c.FilaOrdinal,
        c.ColumnaOrdinal,
        c.Hoja,
        c.Valor
    FROM dbo.GeneracionVersion v 
    JOIN dbo.DocumentoCelda c ON c.VersionId = v.VersionId 
    WHERE v.GeneracionId = @GeneracionId 
      AND v.EsActiva = 1
    ORDER BY 
        c.VersionId,
        c.HojaOrdinal,
        c.FilaOrdinal,
        c.ColumnaOrdinal;
END;
GO

CREATE OR ALTER PROCEDURE dbo.EstadoFuentes
AS
BEGIN
    SET NOCOUNT ON;

    SELECT (
        SELECT 
            JSON_QUERY((
                SELECT 
                    f.FuenteKey AS fuente,
                    f.Origen AS origen,
                    f.Articulo AS articulo,
                    f.Listado AS listado,
                    e.UltimoIntento AS ultimoIntento,
                    e.UltimoExito AS ultimoExito,
                    e.Resultado AS resultado,
                    e.ErrorSeguro AS error,
                    s.registrosAceptados,
                    s.filasLeidas,
                    s.filasRechazadas,
                    s.certificacion,
                    s.incorporadaEn
                FROM dbo.Fuente f 
                LEFT JOIN dbo.FuenteEstado e ON e.FuenteKey = f.FuenteKey
                OUTER APPLY (
                    SELECT 
                        SUM(v.Filas) AS registrosAceptados,
                        SUM(v.FilasLeidas) AS filasLeidas,
                        SUM(v.FilasRechazadas) AS filasRechazadas,
                        CASE 
                            WHEN MAX(CASE WHEN v.Certificacion = 'requiere-revision' THEN 1 ELSE 0 END) = 1 THEN 'requiere-revision'
                            WHEN MIN(CASE WHEN v.Certificacion = 'certificada' THEN 1 ELSE 0 END) = 1 THEN 'certificada' 
                            ELSE 'pendiente' 
                        END AS certificacion,
                        MAX(v.Incorporada) AS incorporadaEn
                    FROM dbo.Publicacion p 
                    JOIN dbo.PublicacionVersion v ON v.VersionId = p.VersionActivaId 
                    WHERE p.FuenteKey = f.FuenteKey
                ) s 
                ORDER BY f.FuenteKey 
                FOR JSON PATH
            )) AS fuentes,
            JSON_QUERY((
                SELECT 
                    Fecha AS fecha,
                    Completa AS completa,
                    Comprobada AS comprobada,
                    Detalle AS detalle 
                FROM dbo.CoberturaDof 
                ORDER BY Fecha 
                FOR JSON PATH
            )) AS cobertura,
            JSON_QUERY((
                SELECT 
                    CodNota AS codNota,
                    Fecha AS fecha,
                    Estado AS estado,
                    Motivo AS motivo 
                FROM dbo.NotaPendiente 
                ORDER BY Fecha, CodNota 
                FOR JSON PATH
            )) AS pendientes,
            JSON_QUERY((
                SELECT TOP(1) 
                    EjecucionId AS ejecucionId,
                    Inicio AS inicio,
                    Fin AS fin,
                    Resultado AS resultado 
                FROM dbo.Ejecucion 
                ORDER BY Fin DESC 
                FOR JSON PATH
            )) AS ultimaEjecucion
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    ) AS EstadoJson;
END;
GO

CREATE OR ALTER PROCEDURE dbo.PublicarGeneracion
    @Propietario uniqueidentifier,
    @Token bigint
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF APPLOCK_MODE('public', 'MonitorRFC.ingestion', 'Session') <> 'Exclusive' 
       OR NOT EXISTS (SELECT 1 FROM dbo.Lease WITH(UPDLOCK, HOLDLOCK) WHERE Recurso = 'ingestion' AND Propietario = @Propietario AND FencingToken = @Token)
    BEGIN
        ROLLBACK;
        THROW 51000, 'Lease de ingesta perdido.', 1;
    END;

    DECLARE @Fuentes nvarchar(max) = (
        SELECT 
            CASE 
                WHEN f.Origen = 'DOF' AND ra.Articulo IS NOT NULL THEN CONCAT('dof:', ra.Articulo, ':publicaciones-dof') 
                ELSE f.FuenteKey 
            END AS fuenteId,
            f.Origen AS origen,
            COALESCE(ra.Articulo, f.Articulo) AS articulo,
            f.Listado AS listado,
            f.Nombre AS nombre,
            f.EsDocumento AS esDocumento,
            f.Habilitada AS habilitada,
            v.VersionId AS versionId,
            v.FechaFuente AS fechaActualizacionFuente,
            v.AlcanceFecha AS alcanceFecha,
            p.PrimeraDeteccion AS primeraDeteccion,
            v.Detectada AS detectadaEn,
            v.Incorporada AS incorporadaEn,
            v.HashContenido AS hashContenido,
            v.ParserVersion AS parserVersion,
            v.Filas AS registros,
            v.Url AS url,
            v.CodNota AS codNota,
            CONVERT(bit, CASE WHEN v.VersionId IS NOT NULL AND v.VersionId <> p.VersionActivaId THEN 1 ELSE 0 END) AS esHistorica
        FROM dbo.Fuente f 
        LEFT JOIN dbo.Publicacion p ON p.FuenteKey = f.FuenteKey 
        LEFT JOIN dbo.PublicacionVersion v ON v.PublicacionId = p.PublicacionId AND v.Estado = 'publicada'
        OUTER APPLY (
            SELECT DISTINCT r.Articulo 
            FROM dbo.RegistroFuente r 
            WHERE r.VersionId = v.VersionId AND f.Origen = 'DOF'
        ) ra 
        ORDER BY f.FuenteKey, v.VersionId, ra.Articulo 
        FOR JSON PATH, INCLUDE_NULL_VALUES
    );

    DECLARE @Desde date, @Hasta date, @Dof bit;
    SELECT @Desde = DofDesde, @Hasta = DofHasta, @Dof = DofHabilitado 
    FROM dbo.ObjetivoCobertura 
    WHERE Id = 1;

    DECLARE @Dias int = (
        SELECT COUNT(*) 
        FROM dbo.CoberturaDof 
        WHERE Fecha BETWEEN @Desde AND @Hasta AND Completa = 1
    );

    DECLARE @Cobertura nvarchar(max) = (
        SELECT 
            CASE 
                WHEN NOT EXISTS(SELECT 1 FROM dbo.Fuente f WHERE f.Origen = 'SAT' AND f.Habilitada = 1) THEN 'deshabilitada'
                WHEN EXISTS(SELECT 1 FROM dbo.Fuente f WHERE f.Origen = 'SAT' AND f.Habilitada = 1 AND NOT EXISTS(SELECT 1 FROM dbo.Publicacion p WHERE p.FuenteKey = f.FuenteKey AND p.VersionActivaId IS NOT NULL)) THEN 'parcial' 
                ELSE 'completa' 
            END AS sat,
            JSON_QUERY((
                SELECT 
                    @Desde AS desde,
                    @Hasta AS hasta,
                    @Dof AS habilitado,
                    CONVERT(bit, CASE WHEN @Dof = 1 AND @Dias = DATEDIFF(day, @Desde, @Hasta) + 1 AND NOT EXISTS(SELECT 1 FROM dbo.NotaPendiente WHERE Fecha BETWEEN @Desde AND @Hasta) THEN 1 ELSE 0 END) AS completa,
                    (SELECT COUNT(*) FROM dbo.NotaPendiente WHERE Fecha BETWEEN @Desde AND @Hasta) AS pendientes,
                    JSON_QUERY((
                        SELECT 
                            Fecha AS fecha,
                            Completa AS completa,
                            Comprobada AS comprobada,
                            Detalle AS detalle 
                        FROM dbo.CoberturaDof 
                        ORDER BY Fecha 
                        FOR JSON PATH
                    )) AS dias 
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
            )) AS dof 
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    );

    INSERT INTO dbo.Generacion (FuentesJson, CoberturaJson) 
    VALUES (@Fuentes, @Cobertura);

    DECLARE @Id bigint = SCOPE_IDENTITY();

    INSERT INTO dbo.GeneracionVersion (GeneracionId, VersionId, EsActiva) 
    SELECT 
        @Id,
        v.VersionId,
        CONVERT(bit, CASE WHEN p.VersionActivaId = v.VersionId THEN 1 ELSE 0 END) 
    FROM dbo.PublicacionVersion v 
    JOIN dbo.Publicacion p ON p.PublicacionId = v.PublicacionId 
    WHERE v.Estado = 'publicada';

    UPDATE dbo.GeneracionActiva 
    SET GeneracionId = @Id 
    WHERE Id = 1;

    IF @@ROWCOUNT = 0 
    BEGIN
        INSERT INTO dbo.GeneracionActiva (Id, GeneracionId) 
        VALUES (1, @Id);
    END;

    COMMIT;

    SELECT @Id;
END;
GO
