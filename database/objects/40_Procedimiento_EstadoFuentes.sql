SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

CREATE OR ALTER PROCEDURE dbo.EstadoFuentes
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.ConsultarEstatusGeneral;
END;
GO

