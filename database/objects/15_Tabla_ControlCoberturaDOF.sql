SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

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

