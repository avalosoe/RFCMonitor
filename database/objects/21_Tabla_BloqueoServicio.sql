SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

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

