-- Consultas públicas encapsuladas, sin permisos directos sobre tablas.
CREATE OR ALTER PROCEDURE dbo.EstadoFuentes AS
BEGIN
 SET NOCOUNT ON;
 SELECT (SELECT JSON_QUERY((SELECT f.FuenteKey fuente,f.Origen origen,f.Articulo articulo,f.Listado listado,
 e.UltimoIntentoUtc ultimoIntentoUtc,e.UltimoExitoUtc ultimoExitoUtc,e.Resultado resultado,e.ErrorSeguro error
 FROM dbo.Fuente f LEFT JOIN dbo.FuenteEstado e ON e.FuenteKey=f.FuenteKey ORDER BY f.FuenteKey FOR JSON PATH)) fuentes,
 JSON_QUERY((SELECT Fecha fecha,Completa completa,ComprobadaUtc comprobadaUtc,Detalle detalle FROM dbo.CoberturaDof ORDER BY Fecha FOR JSON PATH)) cobertura,
 JSON_QUERY((SELECT CodNota codNota,Fecha fecha,Estado estado,Motivo motivo FROM dbo.NotaPendiente ORDER BY Fecha,CodNota FOR JSON PATH)) pendientes,
 JSON_QUERY((SELECT TOP(1) EjecucionId ejecucionId,InicioUtc inicioUtc,FinUtc finUtc,Resultado resultado FROM dbo.Ejecucion ORDER BY FinUtc DESC FOR JSON PATH)) ultimaEjecucion
 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER) EstadoJson;
END;
GO
CREATE OR ALTER PROCEDURE dbo.DocumentoTecnico @GeneracionId bigint AS
BEGIN
 SET NOCOUNT ON;
 SELECT c.VersionId,c.HojaOrdinal,c.FilaOrdinal,c.ColumnaOrdinal,c.Hoja,c.Valor
 FROM dbo.GeneracionVersion v JOIN dbo.DocumentoCelda c ON c.VersionId=v.VersionId
 WHERE v.GeneracionId=@GeneracionId AND v.EsActiva=1
 ORDER BY c.VersionId,c.HojaOrdinal,c.FilaOrdinal,c.ColumnaOrdinal;
END;
GO
IF NOT EXISTS(SELECT 1 FROM dbo.MigracionEsquema WHERE Version=3) INSERT dbo.MigracionEsquema(Version) VALUES(3);
GO
