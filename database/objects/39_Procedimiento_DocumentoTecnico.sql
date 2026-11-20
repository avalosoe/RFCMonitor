SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

CREATE OR ALTER PROCEDURE dbo.DocumentoTecnico
    @GeneracionId   bigint = 1
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 
        CAST(1 AS bigint) AS VersionId,
        HojaOrdinal,
        FilaOrdinal,
        ColumnaOrdinal,
        Hoja,
        Valor
    FROM dbo.DocumentoTecnico69 WITH (NOLOCK)
    ORDER BY HojaOrdinal, FilaOrdinal, ColumnaOrdinal;
END;
GO

