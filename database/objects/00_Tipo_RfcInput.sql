SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

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

