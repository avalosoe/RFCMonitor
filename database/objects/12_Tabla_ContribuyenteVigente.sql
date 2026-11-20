SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- ----------------------------------------------------------------------------
-- 3. Tabla Principal de Contribuyentes Vigentes (Estado Actual Relacional)
-- ----------------------------------------------------------------------------
-- Contiene las publicaciones vigentes de contribuyentes. Un contribuyente
-- puede poseer múltiples registros en un mismo listado (p. ej. diversos
-- créditos cancelados, multas reducidas o condonaciones con distintos montos/fechas).
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ContribuyenteVigente' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ContribuyenteVigente (
        ContribuyenteId             bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Rfc                         nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
        RazonSocial                 nvarchar(2000) NOT NULL,
        Articulo                    varchar(16) NOT NULL REFERENCES dbo.Articulo(Clave),
        Listado                     varchar(100) NOT NULL,
        Situacion                   nvarchar(2000) NOT NULL,
        FechaEfectiva               date NULL,
        OrigenUltimaModificacion    varchar(8) NOT NULL CHECK (OrigenUltimaModificacion IN ('SAT', 'DOF')),
        FechaModificacion           datetime2(3) NOT NULL DEFAULT SYSDATETIME(),
        
        -- Atributos Relacionales SAT General (Artículo 69 y genéricos)
        TipoPersona                 varchar(20) NULL,
        EntidadFederativa           nvarchar(1000) NULL,
        Monto                       decimal(18,2) NULL,
        Ejercicio                   varchar(50) NULL,
        Periodo                     varchar(100) NULL,
        Motivo                      nvarchar(2000) NULL,
        FechaCancelacion            date NULL,
        FechaAutorizacion           date NULL,
        Administracion              nvarchar(500) NULL,
        NumeroOficioSAT             nvarchar(200) NULL,
        FechaOficioSAT              date NULL,
        NumeroOficioDOF             nvarchar(200) NULL,
        FechaOficioDOF              date NULL,

        -- Atributos Relacionales Etapas 69-B y 69-B Bis
        PresuncionOficioSAT         nvarchar(200) NULL,
        PresuncionFechaSAT          date NULL,
        PresuncionOficioDOF         nvarchar(200) NULL,
        PresuncionFechaDOF          date NULL,
        DesvirtuadoOficioSAT        nvarchar(200) NULL,
        DesvirtuadoFechaSAT         date NULL,
        DesvirtuadoOficioDOF        nvarchar(200) NULL,
        DesvirtuadoFechaDOF         date NULL,
        DefinitivoOficioSAT         nvarchar(200) NULL,
        DefinitivoFechaSAT          date NULL,
        DefinitivoOficioDOF         nvarchar(200) NULL,
        DefinitivoFechaDOF          date NULL,
        SentenciaOficioSAT          nvarchar(200) NULL,
        SentenciaFechaSAT           date NULL,
        SentenciaOficioDOF          nvarchar(200) NULL,
        SentenciaFechaDOF           date NULL,

        -- Trazabilidad DOF de origen vigente
        CodNotaDOF                  bigint NULL,
        UrlOrigenDOF                nvarchar(2048) NULL,

        -- Control de Delta y Comparativa
        HashFila                    binary(32) NOT NULL,
        UltimaDescarga              datetime2(3) NOT NULL DEFAULT SYSDATETIME()
    );
END;
GO

-- Asegurar columnas de trazabilidad DOF si la tabla ya existía
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ContribuyenteVigente') AND name = 'CodNotaDOF')
BEGIN
    ALTER TABLE dbo.ContribuyenteVigente ADD CodNotaDOF bigint NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ContribuyenteVigente') AND name = 'UrlOrigenDOF')
BEGIN
    ALTER TABLE dbo.ContribuyenteVigente ADD UrlOrigenDOF nvarchar(2048) NULL;
END;
GO

-- Asegurar eliminación de restricción UNIQUE si fue creada previamente
IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_ContribuyenteVigente_RfcArticuloListado')
BEGIN
    ALTER TABLE dbo.ContribuyenteVigente DROP CONSTRAINT UQ_ContribuyenteVigente_RfcArticuloListado;
END;
GO

-- Índices de Alto Rendimiento para ContribuyenteVigente
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteVigente_Rfc' AND object_id = OBJECT_ID('dbo.ContribuyenteVigente'))
BEGIN
    CREATE INDEX IX_ContribuyenteVigente_Rfc 
    ON dbo.ContribuyenteVigente(Rfc)
    INCLUDE (RazonSocial, Articulo, Listado, Situacion, FechaEfectiva, OrigenUltimaModificacion);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteVigente_CodNotaDOF' AND object_id = OBJECT_ID('dbo.ContribuyenteVigente'))
BEGIN
    CREATE INDEX IX_ContribuyenteVigente_CodNotaDOF 
    ON dbo.ContribuyenteVigente(CodNotaDOF)
    WHERE CodNotaDOF IS NOT NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteVigente_ArticuloListado' AND object_id = OBJECT_ID('dbo.ContribuyenteVigente'))
BEGIN
    CREATE INDEX IX_ContribuyenteVigente_ArticuloListado 
    ON dbo.ContribuyenteVigente(Articulo, Listado)
    INCLUDE (Rfc, Situacion, FechaEfectiva);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteVigente_ArticuloListadoHash' AND object_id = OBJECT_ID('dbo.ContribuyenteVigente'))
BEGIN
    CREATE INDEX IX_ContribuyenteVigente_ArticuloListadoHash 
    ON dbo.ContribuyenteVigente(Articulo, Listado, HashFila);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ContribuyenteVigente_FechaEfectiva' AND object_id = OBJECT_ID('dbo.ContribuyenteVigente'))
BEGIN
    CREATE INDEX IX_ContribuyenteVigente_FechaEfectiva 
    ON dbo.ContribuyenteVigente(FechaEfectiva DESC)
    INCLUDE (Rfc, Articulo, Listado, Situacion);
END;
GO

