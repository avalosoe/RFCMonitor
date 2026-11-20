SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

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

