SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- Trabajos de sincronización DOF on-demand (API). Estado y avance persistentes para consultar tras un reinicio.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TrabajoDOF' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.TrabajoDOF (
        TrabajoId           uniqueidentifier NOT NULL PRIMARY KEY,
        FechaInicio         date NOT NULL,
        FechaFin            date NOT NULL,
        Articulo            varchar(16) NULL,
        Estado              varchar(20) NOT NULL CHECK (Estado IN ('en_cola', 'en_curso', 'completado', 'error', 'interrumpido')),
        DiaActual           int NOT NULL DEFAULT 0,
        TotalDias           int NOT NULL DEFAULT 0,
        Mensaje             nvarchar(1000) NULL,
        ResultadoJson       nvarchar(max) NULL,
        Error               nvarchar(2000) NULL,
        FechaCreacion       datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
        FechaInicioProceso  datetime2(3) NULL,
        FechaTermino        datetime2(3) NULL,
        FechaActualizacion  datetime2(3) NOT NULL DEFAULT SYSDATETIME()
    );
END;
GO

