SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- ----------------------------------------------------------------------------
-- 5. Publicaciones Oficiales y Control SIDOF / DOF
-- ----------------------------------------------------------------------------
-- Almacena los decretos y notas publicadas en el DOF a nivel maestro para
-- auditoría legal, trazabilidad forense y enlace directo con contribuyentes extraídos.
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PublicacionDOF' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PublicacionDOF (
        CodNota                 bigint NOT NULL PRIMARY KEY,
        Titulo                  nvarchar(1000) NOT NULL,
        FechaPublicacion        date NOT NULL,
        Edicion                 varchar(50) NULL,
        Organismo               nvarchar(500) NULL,
        Articulo                varchar(16) NOT NULL REFERENCES dbo.Articulo(Clave),
        UrlNota                 nvarchar(2048) NOT NULL,
        HashArchivo             varchar(64) NULL,
        RutaArchivo             nvarchar(2048) NULL,
        TotalContribuyentes     int NOT NULL DEFAULT 0,
        FechaRegistro           datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
        CodDiario               bigint NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PublicacionDOF') AND name = 'CodDiario')
BEGIN
    ALTER TABLE dbo.PublicacionDOF ADD CodDiario bigint NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PublicacionDOF_FechaPublicacion' AND object_id = OBJECT_ID('dbo.PublicacionDOF'))
BEGIN
    CREATE INDEX IX_PublicacionDOF_FechaPublicacion 
    ON dbo.PublicacionDOF(FechaPublicacion DESC, Articulo);
END;
GO

