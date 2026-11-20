SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

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

