SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- ----------------------------------------------------------------------------
-- 10. Procedimiento Almacenado para Ingesta y Trazabilidad DOF
-- ----------------------------------------------------------------------------
-- Registra una publicación oficial del DOF en el historial del RFC y evalúa
-- si la fecha efectiva es más reciente que el estatus vigente actual para
-- actualizar ContribuyenteVigente.
-- ----------------------------------------------------------------------------

CREATE OR ALTER PROCEDURE dbo.AplicarPublicacionDOF
    @Rfc                nvarchar(100),
    @RazonSocial        nvarchar(2000),
    @Articulo           varchar(16),
    @Listado            varchar(100),
    @Situacion          nvarchar(2000),
    @FechaEfectiva      date,
    @CodNotaDOF         bigint,
    @UrlOrigen          nvarchar(2048),
    @NumeroOficioDOF    nvarchar(200) = NULL,
    @EntidadFederativa  nvarchar(1000) = NULL,
    @Motivo             nvarchar(2000) = NULL,
    @HashFila           binary(32)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @FechaHoraLocal datetime2(3) = SYSDATETIME();

    -- 1. Siempre registrar en el Historial para trazabilidad completa
    INSERT INTO dbo.ContribuyenteHistorial (
        Rfc, RazonSocial, Articulo, Listado, Situacion, FechaEfectiva,
        TipoEvento, Origen, FechaRegistro, CodNotaDOF, UrlOrigen,
        NumeroOficioSAT, FechaOficioSAT, NumeroOficioDOF, FechaOficioDOF,
        EntidadFederativa, Motivo, HashFila
    )
    VALUES (
        @Rfc, @RazonSocial, @Articulo, @Listado, @Situacion, @FechaEfectiva,
        'Publicacion DOF', 'DOF', @FechaHoraLocal, @CodNotaDOF, @UrlOrigen,
        NULL, NULL, @NumeroOficioDOF, @FechaEfectiva,
        @EntidadFederativa, @Motivo, @HashFila
    );

    -- 2. Evaluar y Actualizar/Insertar en ContribuyenteVigente
    IF NOT EXISTS (SELECT 1 FROM dbo.ContribuyenteVigente WHERE Rfc = @Rfc AND Articulo = @Articulo AND Listado = @Listado)
    BEGIN
        INSERT INTO dbo.ContribuyenteVigente (
            Rfc, RazonSocial, Articulo, Listado, Situacion, FechaEfectiva,
            OrigenUltimaModificacion, FechaModificacion, TipoPersona, EntidadFederativa,
            NumeroOficioDOF, FechaOficioDOF, CodNotaDOF, UrlOrigenDOF, HashFila, UltimaDescarga
        )
        VALUES (
            @Rfc, @RazonSocial, @Articulo, @Listado, @Situacion, @FechaEfectiva,
            'DOF', @FechaHoraLocal, NULL, @EntidadFederativa,
            @NumeroOficioDOF, @FechaEfectiva, @CodNotaDOF, @UrlOrigen, @HashFila, @FechaHoraLocal
        );
    END
    ELSE
    BEGIN
        -- Si existe, se actualiza solo si la fecha del DOF es más reciente o igual a la fecha efectiva registrada
        UPDATE dbo.ContribuyenteVigente
        SET 
            RazonSocial                 = @RazonSocial,
            Situacion                   = @Situacion,
            FechaEfectiva               = @FechaEfectiva,
            OrigenUltimaModificacion    = 'DOF',
            FechaModificacion           = @FechaHoraLocal,
            NumeroOficioDOF             = COALESCE(@NumeroOficioDOF, NumeroOficioDOF),
            FechaOficioDOF              = @FechaEfectiva,
            CodNotaDOF                  = @CodNotaDOF,
            UrlOrigenDOF                = @UrlOrigen,
            EntidadFederativa           = COALESCE(@EntidadFederativa, EntidadFederativa),
            HashFila                    = @HashFila
        WHERE Rfc = @Rfc 
          AND Articulo = @Articulo 
          AND Listado = @Listado
          AND (@FechaEfectiva >= FechaEfectiva OR FechaEfectiva IS NULL);
    END;
END;
GO

