# Base de datos MonitorRFC

Base de datos relacional para **Microsoft SQL Server 2019 o posterior** (nivel de compatibilidad 150 o superior), diseñada para la persistencia transaccional, detección de deltas por hash binario SHA-256, versionamiento histórico inmutable y consultas de alto rendimiento de listados del SAT y publicaciones del DOF.

La base de datos se implementa mediante un único script autocontenido y opera estrictamente sobre el esquema estándar `dbo`.

---

## 1. Despliegue y Mantenimiento

### 1.1. Implementación Inicial (Bootstrap)

Ejecute `database/bootstrap.sql` para crear todas las tablas, índices, tipos de tabla (TVP), procedimientos almacenados y vistas:

```powershell
sqlcmd -S SERVIDOR -d MonitorRFC -E -b -i database/bootstrap.sql
```

### 1.2. Reinicio Limpio para Pruebas (Reset)

Para depurar completamente la base de datos y restablecerla a un estado inicial limpio sin desinstalar el motor:

```powershell
# 1. Elimina todos los objetos de la solución en el esquema dbo
sqlcmd -S SERVIDOR -d MonitorRFC -E -b -i database/reset.sql

# 2. Reconstruye la estructura limpia
sqlcmd -S SERVIDOR -d MonitorRFC -E -b -i database/bootstrap.sql
```

> **IMPORTANTE**: Los scripts operan exclusivamente sobre el esquema `dbo`. No crean usuarios, logins ni roles de servidor, y no contienen contraseñas en texto plano.

---

## 2. Catálogo de Objetos en Base de Datos

### 2.1. Tablas del Esquema `dbo`

| Tabla | Clave Primaria / Índices | Propósito y Función |
| :--- | :--- | :--- |
| **`dbo.Articulo`** | `ArticuloId` (PK), `Clave` (UQ) | Catálogo oficial de artículos fiscales (`1`: 69, `2`: 69-b, `3`: 69-b-bis, `4`: 49-bis). |
| **`dbo.ControlFuenteSAT`** | `FuenteKey` (PK) | Estado de cada fuente SAT: URL, hash del archivo, fecha de portal, última descarga exitosa, conteos y métricas operativas. |
| **`dbo.ContribuyenteVigente`** | `ContribuyenteId` (PK, IDENTITY), `(Rfc, Articulo, Listado)` (UQ) | Padrón activo y consolidado de contribuyentes. Incluye 30+ columnas relacionales y `HashFila VARBINARY(32)` (resumen SHA-256) para comparación instantánea de deltas. |
| **`dbo.ContribuyenteHistorial`** | `HistorialId` (PK, IDENTITY), `(Rfc, FechaRegistro)` (IX) | Trazabilidad histórica append-only: registra cada alta, cambio de situación fiscal, modificación de oficio o sentencia. |
| **`dbo.PublicacionDOF`** | `PublicacionId` (PK, IDENTITY), `CodNota` (UQ) | Registro de edictos y notas oficiales publicadas en el DOF, extraídas mediante la API de SIDOF. |
| **`dbo.ControlCoberturaDOF`** | `Fecha` (PK) | Bitácora de seguimiento diario del DOF: asegura exhaustividad, detecta días pendientes y garantiza ausencia de huecos en el historial. |
| **`dbo.TrabajoDOF`** | `TrabajoId` (PK, GUID) | Estado, rango de fechas, progreso porcentual y resultado JSON de sincronizaciones DOF bajo demanda solicitadas vía API. |
| **`dbo.NotaPendienteDOF`** | `CodNota` (PK) | Notas del DOF identificadas como relevantes pero marcadas para revisión manual por formato atípico o anexos no estructurados. |
| **`dbo.AdjuntoDOF`** | `AdjuntoId` (PK, IDENTITY), `CodNota` (IX) | Metadatos y rutas de almacenamiento local para documentos PDF o imágenes anexas a publicaciones del DOF. |
| **`dbo.DocumentoTecnico69`** | `DocumentoId` (PK, IDENTITY), `(GeneracionId, Hoja, Fila, Columna)` (UQ) | Matriz completa de celdas del archivo XLS normativo de causas y motivos del Artículo 69 (`causasymotivos.xls`). |
| **`dbo.BitacoraEjecucion`** | `EjecucionId` (PK, GUID) | Registro auditable de ejecuciones del Worker: disparador (`programada`, `manual`, `arranque`), fechas, duración, resultado y resumen JSON. |
| **`dbo.BloqueoServicio`** | `LeaseKey` (PK) | Candado distribuido exclusivo a nivel de base de datos para serializar ejecuciones de ingesta y evitar condiciones de carrera. |

### 2.2. Tipos Definidos por el Usuario (TVP)

| Tipo | Definición | Uso |
| :--- | :--- | :--- |
| **`dbo.RfcInput`** | `TABLE (Rfc VARCHAR(20) NOT NULL PRIMARY KEY)` | Tipo de tabla en memoria utilizado por los procedimientos almacenados para recibir colecciones masivas de RFCs en consultas en bloque. |

### 2.3. Vistas de Compatibilidad Legada

| Vista | Propósito |
| :--- | :--- |
| **`dbo.vw_LegacyRfcDetalle`** | Desglosa los campos de contribuyentes vigentes al esquema requerido por clientes legados (oficios SAT y DOF, fechas en texto `dd/MM/yyyy`). |
| **`dbo.vw_LegacyRfcArticulos`** | Agrupa y concatena los artículos fiscales en los que aparece cada RFC (ej. `69-b, 69 (Cancelados, Exigibles)`). |
| **`dbo.vw_LegacyNoLocalizados`** | Identifica contribuyentes con estatus de no localizados para compatibilidad con reportes legados. |
| **`dbo.vw_LegacyInfoFuentes`** | Agrupa fechas de actualización y metadatos de fuentes SAT y DOF para los encabezados de respuesta legados. |

### 2.4. Procedimientos Almacenados Principales

| Procedimiento | Tipo | Descripción |
| :--- | :---: | :--- |
| **`dbo.AplicarCambiosSAT`** | Ingesta | Compara el contenido de la tabla temporal contra `dbo.ContribuyenteVigente` usando el hash SHA-256. Aplica altas, modificaciones y bajas en bloque; genera la auditoría correspondiente en `dbo.ContribuyenteHistorial`. |
| **`dbo.AplicarPublicacionDOF`** | Ingesta | Procesa notas del DOF, inserta edictos en `dbo.PublicacionDOF` y actualiza situaciones en `dbo.ContribuyenteVigente`. |
| **`dbo.ConsultarContribuyentesVigentes`** | Consulta | Devuelve el padrón activo filtrado por artículo, listado y situación, con soporte para orden jerárquico. |
| **`dbo.ConsultarContribuyenteHistorial`** | Consulta | Retorna el historial de movimientos de un RFC individual, ordenado estrictamente en sentido cronológico ascendente. |
| **`dbo.ConsultarHistorialRfcs`** | Consulta | Consulta masiva de historial para una lista de RFCs provista mediante el TVP `dbo.RfcInput`. |
| **`dbo.ConsultarEstatusGeneral`** | Consulta | Evalúa masivamente el nivel de riesgo y apariciones vigentes para una lista de RFCs provista mediante `dbo.RfcInput`. |
| **`dbo.PublicarGeneracion`** | Control | Incrementa la versión de la generación activa tras una ingesta consistente. |
| **`dbo.DocumentoTecnico`** | Consulta | Retorna la matriz de celdas del documento técnico del Artículo 69 para la generación indicada. |
| **`dbo.EstadoFuentes`** | Monitoreo | Reporta el estado operativo y métricas de todas las fuentes de datos. |
| **`dbo.PA_Legacy_EdosEfos_Consultar`** | Legacy | Endpoint `/api/Rfc/Edos-Efos`: consulta masiva con artículos concatenados en un solo registro por RFC. |
| **`dbo.PA_Legacy_EdosEfos_All`** | Legacy | Endpoint `/api/EdosEfos/all`: consulta completa de 69-B y 69-B Bis sin paginación. |
| **`dbo.PA_Legacy_EdosEfos_All69`** | Legacy | Endpoint `/api/EdosEfos/all69`: paginación de alto rendimiento (máximo 10,000 registros) con ordenación canónica y filtros por tipo legacy. |

---

## 3. Consultas Diagnósticas de Solo Lectura

Para comprobar el estado de la base de datos tras una ejecución:

```sql
-- 1. Revisar estado y conteos por fuente SAT
SELECT FuenteKey, Nombre, UltimaFechaPortal, UltimaDescargaExitosa, RegistrosVigentes, UltimoResultado
FROM dbo.ControlFuenteSAT
ORDER BY FuenteKey;

-- 2. Resumen de ejecuciones del Worker
SELECT Resultado, Disparador, COUNT(*) AS TotalEjecuciones, MAX(Fin) AS UltimaEjecucion
FROM dbo.BitacoraEjecucion
GROUP BY Resultado, Disparador;

-- 3. Total de contribuyentes activos por artículo fiscal
SELECT A.Clave AS Articulo, A.Nombre, COUNT(C.ContribuyenteId) AS ContribuyentesVigentes
FROM dbo.Articulo A
LEFT JOIN dbo.ContribuyenteVigente C ON A.Clave = C.Articulo
GROUP BY A.Clave, A.Nombre
ORDER BY A.Clave;

-- 4. Cobertura del Diario Oficial de la Federación
SELECT TOP 15 Fecha, Completo, NotasRelevantes, UltimaConsulta
FROM dbo.ControlCoberturaDOF
ORDER BY Fecha DESC;

-- 5. Trabajos de sincronización DOF bajo demanda
SELECT TrabajoId, Estado, FechaInicio, FechaFin, PorcentajeAvance, FechaCreacion
FROM dbo.TrabajoDOF
ORDER BY FechaCreacion DESC;
```


## Instrucción obligatoria para agentes de IA
No se considera terminada ninguna funcionalidad de esta solución para ningún proyecto si el mismo cambio no actualiza este README. Todo agente debe documentar aquí, de forma técnica y verificable, cualquier endpoint, servicio, flujo, autorización, configuración, dependencia, contrato compartido, evento SignalR u objeto SQL que agregue, modifique o elimine.

La actualización debe indicar como mínimo el propósito, ruta o punto de entrada, autenticación requerida, DTOs o parámetros, dependencias afectadas, persistencia/objetos SQL consumidos, efectos operativos y pasos necesarios para ejecutar o validar el cambio. El agente debe corregir o retirar documentación obsoleta en el mismo cambio; no debe dejar referencias genéricas, supuestos sin confirmar ni secretos.