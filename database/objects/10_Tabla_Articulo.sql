SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

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

