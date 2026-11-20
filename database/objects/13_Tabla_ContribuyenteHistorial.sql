SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- ----------------------------------------------------------------------------
-- 4. Tabla de Historial y Trazabilidad de Estados
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ContribuyenteHistorial' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ContribuyenteHistorial (
        HistorialId         bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Rfc                 nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
        RazonSocial         nvarchar(2000) NOT NULL,
        Articulo            varchar(16) NOT NULL REFERENCES dbo.Articulo(Clave),
        Listado             varchar(100) NOT NULL,
        Situacion           nvarchar(2000) NOT NULL,
        FechaEfectiva       date NULL,
        TipoEvento          varchar(50) NOT NULL CHECK (TipoEvento IN ('Alta Inicial', 'Cambio Situacion', 'Publicacion DOF', 'Actualizacion SAT')),
        Origen              varchar(8) NOT NULL CHECK (Origen IN ('SAT', 'DOF')),
        FechaRegistro       datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
        CodNotaDOF          bigint NULL,
        UrlOrigen           nvarchar(2048) NULL,
        NumeroOficioSAT     nvarchar(200) NULL,
        FechaOficioSAT      date NULL,
        NumeroOficioDOF     nvarchar(200) NULL,
        FechaOficioDOF      date NULL,
        EntidadFederativa   nvarchar(1000) NULL,
        Motivo              nvarchar(2000) NULL,
        HashFila            binary(32) NOT NULL
    );
END;
GO

-- Índices de Trazabilidad para ContribuyenteHistorial
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteHistorial_RfcArticulo' AND object_id = OBJECT_ID('dbo.ContribuyenteHistorial'))
BEGIN
    CREATE INDEX IX_ContribuyenteHistorial_RfcArticulo 
    ON dbo.ContribuyenteHistorial(Rfc, Articulo, FechaEfectiva DESC)
    INCLUDE (Situacion, TipoEvento, Origen, CodNotaDOF);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteHistorial_ArticuloListado' AND object_id = OBJECT_ID('dbo.ContribuyenteHistorial'))
BEGIN
    CREATE INDEX IX_ContribuyenteHistorial_ArticuloListado 
    ON dbo.ContribuyenteHistorial(Articulo, Listado, FechaRegistro DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteHistorial_CodNotaDOF' AND object_id = OBJECT_ID('dbo.ContribuyenteHistorial'))
BEGIN
    CREATE INDEX IX_ContribuyenteHistorial_CodNotaDOF 
    ON dbo.ContribuyenteHistorial(CodNotaDOF)
    WHERE CodNotaDOF IS NOT NULL;
END;
GO

