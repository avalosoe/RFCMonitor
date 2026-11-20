SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- ----------------------------------------------------------------------------
-- 6. Documento Técnico Normativo Artículo 69 (Matriz Excel)
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'DocumentoTecnico69' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.DocumentoTecnico69 (
        HojaOrdinal         int NOT NULL,
        FilaOrdinal         int NOT NULL,
        ColumnaOrdinal      int NOT NULL,
        Hoja                nvarchar(255) NOT NULL,
        Valor               nvarchar(max) NULL,
        FechaActualizacion  datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
        PRIMARY KEY (HojaOrdinal, FilaOrdinal, ColumnaOrdinal)
    );
END;
GO

