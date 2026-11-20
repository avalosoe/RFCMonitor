SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

CREATE OR ALTER PROCEDURE dbo.PublicarGeneracion
    @Propietario    uniqueidentifier,
    @Token          bigint
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CAST(1 AS bigint) AS GeneracionId;
END;
GO

