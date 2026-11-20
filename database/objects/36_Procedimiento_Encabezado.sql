SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- ----------------------------------------------------------------------------
-- 12. Procedimientos y Vistas de Compatibilidad Retroactiva con el API
-- ----------------------------------------------------------------------------
-- Permiten que los endpoints actuales de la API sigan consumiendo contratos
-- estables mientras se transita a las nuevas estructuras directas.
-- ----------------------------------------------------------------------------

CREATE OR ALTER PROCEDURE dbo.Encabezado
AS
BEGIN
    SET NOCOUNT ON;

    SELECT 
        CAST(1 AS bigint) AS GeneracionId,
        SYSDATETIME() AS Creada,
        N'[]' AS FuentesJson,
        N'{}' AS CoberturaJson;
END;
GO

