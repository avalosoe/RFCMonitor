SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

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

