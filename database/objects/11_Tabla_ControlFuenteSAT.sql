SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- ----------------------------------------------------------------------------
-- 2. Control Operativo de Fuentes SAT
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ControlFuenteSAT' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ControlFuenteSAT (
        FuenteClave             varchar(100) NOT NULL PRIMARY KEY,
        Articulo                varchar(16) NOT NULL REFERENCES dbo.Articulo(Clave),
        Listado                 varchar(100) NOT NULL,
        Nombre                  nvarchar(500) NOT NULL,
        UrlPortal               nvarchar(2048) NOT NULL,
        EsDocumento             bit NOT NULL DEFAULT 0,
        Habilitada              bit NOT NULL DEFAULT 1,
        FechaPublicacionPortal  date NULL,
        UltimoHashArchivo       char(64) NULL,
        UltimaDescargaExitosa   datetime2(3) NULL,
        UltimoIntento           datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
        TotalRegistrosVigentes  int NOT NULL DEFAULT 0,
        TotalNuevos             int NOT NULL DEFAULT 0,
        TotalModificados        int NOT NULL DEFAULT 0,
        TotalSinCambios         int NOT NULL DEFAULT 0,
        Resultado               varchar(30) NOT NULL DEFAULT 'Pendiente',
        UltimoError             nvarchar(2000) NULL
    );
END;
GO

