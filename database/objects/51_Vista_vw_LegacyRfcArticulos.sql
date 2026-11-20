SET XACT_ABORT ON;
GO

-- MonitorRFC | Objeto independiente extraído de database/bootstrap.sql.
-- Ejecute los archivos en orden ascendente de prefijo numérico.

-- Un renglón por RFC con los artículos en que aparece, concatenados:
-- "69-b, 69 (Cancelados), 49-bis" o "69 (Cancelados, Exigibles)". El artículo 69 lleva entre paréntesis sus listados del SAT
-- siempre que el RFC aparezca en al menos un listado del SAT del 69 (uno o varios).
CREATE OR ALTER VIEW dbo.vw_LegacyRfcArticulos
AS
WITH L AS (
    SELECT DISTINCT v.Rfc, v.Articulo, v.Listado FROM dbo.ContribuyenteVigente v
), N AS (
    SELECT l.Rfc, l.Articulo,
           NombreListados = STRING_AGG(CAST(COALESCE(c.Nombre, l.Listado) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY COALESCE(c.Nombre, l.Listado)),
           TotalListados  = COUNT(*)
    FROM L l
    LEFT JOIN dbo.ControlFuenteSAT c ON c.Articulo = l.Articulo AND c.Listado = l.Listado AND c.EsDocumento = 0
    WHERE l.Listado <> 'publicaciones-dof'
    GROUP BY l.Rfc, l.Articulo
), A AS (
    SELECT l.Rfc, l.Articulo,
           Orden = CASE l.Articulo WHEN '69-b' THEN 1 WHEN '69-b-bis' THEN 2 WHEN '69' THEN 3 ELSE 4 END
    FROM L l
    GROUP BY l.Rfc, l.Articulo
)
SELECT a.Rfc,
       Articulo = STRING_AGG(CAST(CASE WHEN a.Articulo = '69' AND n.NombreListados IS NOT NULL
                                       THEN a.Articulo + N' (' + n.NombreListados + N')'
                                       ELSE a.Articulo END AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY a.Orden)
FROM A a
LEFT JOIN N n ON n.Rfc = a.Rfc AND n.Articulo = a.Articulo
GROUP BY a.Rfc;
GO

