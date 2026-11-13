# Guía para Agentes de IA - MonitorRFC

> **IMPORTANTE**: Este repositorio (`MonitorRFC`) es el proyecto principal y activo.

---

## 1. Contexto del Proyecto y Origen

Este proyecto es el resultado de la **migración y consolidación funcional** de dos proyectos legados:

1. **`EdosSatScraper`**
   - *Ruta local histórica:* `C:\Users\iavalos\source\repos\EdosSatScraper`
   - *Responsabilidad original:* Descarga y scraping de listas públicas del SAT (Artículos 69, 69-B, 69-B Bis), integración con SIDOF (DOF), extracción, procesamiento de archivos (CSV/XLS/HTML) y persistencia/notificaciones.
2. **`RfcStatusApi`**
   - *Ruta local histórica:* `C:\Users\iavalos\source\repos\RfcStatusApi`
   - *Responsabilidad original:* API REST para la consulta del estatus de contribuyentes (RFCs) y listados oficiales ante el SAT.

---

## 2. Directiva Principal para Agentes

> **REGLA FUNDAMENTAL:**
> **Toda instrucción, tarea, corrección, refactorización o nueva característica DEBE realizarse exclusivamente sobre el repositorio `MonitorRFC` y enfocado en la solución `RFCMonitor`.**

- **No modificar** los proyectos en `C:\Users\iavalos\source\repos\EdosSatScraper` ni en `C:\Users\iavalos\source\repos\RfcStatusApi`. Dichas rutas únicamente fungirán como referencia histórica o consulta si se requiere revisar comportamiento previo.
- Todo código nuevo, migración de lógica pendiente o ajustes de arquitectura pertenecen a la solución **`RFCMonitor`** dentro de este repositorio.

---

## 3. Colección de Apidog y Regla de Integración Obligatoria

Existe una colección local de Apidog para pruebas y documentación de las APIs en la ruta:
`C:\Users\iavalos\AppData\Roaming\apidog\Collections\RFC`

> **REGLA DE INTEGRACIÓN EN APIDOG:**
> **Tras agregar, modificar o renombrar cualquier endpoint en la API, es OBLIGATORIO integrarlo de inmediato a la colección de Apidog**, creando o actualizando el archivo correspondiente con formato `.http.yaml` dentro de la carpeta mencionada.

Formato base de los archivos `.http.yaml`:
```yaml
method: get # o post, put, delete, etc.
url: /api/v1/ejemplo/{parametro}
parameters:
  path:
    - name: parametro
      value: valor_ejemplo
```

---

## 4. Endpoints Actuales de la API (`RFCMonitor`)

A continuación se documenta el catálogo completo de endpoints expuestos por la API REST:

### 4.1. Diagnóstico y Salud (Health Checks)
| Método | Ruta | Descripción | Parámetros / Body |
| :--- | :--- | :--- | :--- |
| `GET` | `/` | Chequeo básico de servicio y estado online. | Ninguno. |
| `GET` | `/health/live` | Sonda de vida (Liveness probe). | Ninguno. |
| `GET` | `/health/ready` | Sonda de disponibilidad (Readiness probe). Valida si existe una generación publicada en base de datos. Retorna 503 si aún no está lista. | Ninguno. |

### 4.2. Catálogo y Configuración de Fuentes
| Método | Ruta | Descripción | Parámetros / Body |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/catalogos` | Catálogo jerárquico completo que agrupa Artículos (IDs 1 al 4) y sus Listados/Supuestos oficiales (IDs 1 al 24). | Ninguno. |
| `GET` | `/api/v1/catalogos/articulos` | Catálogo plano de Artículos Fiscales soportados con sus IDs numéricos oficiales (`1`: 69, `2`: 69-b, `3`: 69-b-bis, `4`: 49-bis). | Ninguno. |
| `GET` | `/api/v1/catalogos/listados` | Catálogo plano de Listados y Supuestos oficiales (IDs 1 al 24), con filtro opcional por artículo. | Query opcional: `articuloId` (int) o `articulo` (string). |
| `GET` | `/api/v1/articulos` | Devuelve la lista de claves de artículos fiscales soportados (`69`, `69-b`, `69-b-bis`, `49-bis`). | Ninguno. |
| `GET` | `/api/v1/articulos/{articulo}/listados` | Obtiene los listados y supuestos disponibles para un artículo dado (incluye orígenes SAT y Publicaciones DOF). | Path: `articulo` (ej. `69-b`). |
| `GET` | `/api/v1/tipos` | Expone el catálogo legacy de tipos fiscales numéricos (1 al N) asignados a cada artículo y listado. | Ninguno. |
| `GET` | `/api/v1/tipos/{tipoId}` | Consulta la definición y detalle de un tipo fiscal numérico específico (1..N). | Path: `tipoId` (int, ej. `15`). |
| `GET` | `/api/v1/fuentes/estado`<br>*(Alias: `/api/v1/descargas/estado`)* | Devuelve el estado operativo detallado de las descargas: última fecha de publicación portal SAT, último intento de ejecución, última descarga exitosa, métricas de registros (vigentes, nuevos, modificados, sin cambios) y cobertura DOF. | Ninguno. |

### 4.3. Consultas de Contribuyentes y RFCs
| Método | Ruta | Descripción | Parámetros / Body |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/contribuyentes`<br>*(Aliases: `/api/v1/contribuyentes/global`, `/api/v1/rfcs/global`)* | Listado jerárquico estructurado (`articulos` -> `listados` -> `contribuyentes`) con streaming JSON en O(1) de memoria. Expone todas las columnas relacionales reales (oficios SAT/DOF, etapas 69-B, metadatos y enlaces directos al DOF). Admite filtros opcionales por IDs numéricos o claves. | Query opcional: `articuloId` (int) o `articulo` (string), `listadoId` (int) o `listado` (string), `situacion`, `tipoId` (int). |
| `GET` | `/api/v1/tipos/{tipoId}/contribuyentes` | Consulta de contribuyentes asociados exclusivamente a un tipo fiscal numérico. | Path: `tipoId` (int, ej. `15`).<br>Query opcional: `incluirHistorial` (bool). |
| `POST` | `/api/v1/rfcs/estado` | Consulta masiva consolidada para un arreglo de RFCs; clasifica el diagnóstico de riesgo (`Critico`, `Presunto`, `Favorable`, `Informativo`, `Limpio`), expone en qué artículos aparece, fecha efectiva más reciente, folios/oficios SAT y DOF, montos, entidad federativa, etapas fiscales y metadatos DOF integrados. | Query opcional: `articuloId` (int) o `articulo` (string), `listadoId` (int) o `listado` (string).<br>Body JSON: `["RFC1", "RFC2"]` o `{ "rfcs": ["RFC1"] }`. |
| `POST` | `/api/v1/rfcs/historial` | Historial cronológico masivo para un arreglo de RFCs, ordenado estrictamente en sentido **ascendente por fecha**, mostrando su trazabilidad completa de movimientos, cambios de situación, `articuloId`, `listadoId`, oficios y detalles DOF. | Query opcional: `articuloId` (int) o `articulo` (string), `listadoId` (int) o `listado` (string).<br>Body JSON: `["RFC1", "RFC2"]` o `{ "rfcs": ["RFC1"] }`. |
| `GET` | `/api/v1/rfcs` | Consulta consolidada de todos los RFCs y sus apariciones publicadas mediante streaming JSON. | Query opcional: `incluirHistorial` (bool, default `false`). |
| `POST` | `/api/v1/rfcs/consultar` | Consulta masiva consolidada; puede limitarse a un artículo o listado. Soporta array directo o formato objeto legacy. | Query opcional: `articulo`, `listado`, `incluirHistorial` (bool).<br>Body JSON: `["RFC1", "RFC2"]` o `{ "rfcs": ["RFC1"] }`. |
| `GET` | `/api/v1/rfcs/{rfc}/estatus` | Estatus general vigente de un RFC en todos los artículos y fuentes certificadas. | Path: `rfc`. |
| `GET` | `/api/v1/rfcs/{rfc}/historial` | Historial de apariciones SAT y DOF de un RFC, ordenado cronológicamente ascendente. | Path: `rfc`.<br>Query opcional: `articulo`, `listado`. |
| `GET` | `/api/v1/articulos/{articulo}/contribuyentes` | Consulta de contribuyentes asociados exclusivamente a un artículo específico. | Path: `articulo`.<br>Query opcional: `incluirHistorial` (bool). |
| `GET` | `/api/v1/articulos/{articulo}/listados/{listado}/contribuyentes` | Consulta de contribuyentes filtrando por artículo y listado concreto (ej. `definitivos`, `presuntos`, etc.). | Path: `articulo`, `listado`.<br>Query opcional: `incluirHistorial` (bool). |

### 4.4. Sincronización On-Demand del DOF
| Método | Ruta | Descripción | Parámetros / Body |
| :--- | :--- | :--- | :--- |
| `POST` | `/api/v1/dof/sincronizar`<br>*(Alias: `/api/v1/dof/descargar`)* | Encola en segundo plano (dentro de la propia API, independiente del Worker) la sincronización DOF del periodo y responde de inmediato `202` con `trabajoId` y `consultarEstadoEn`. El trabajo consulta SIDOF, compara de forma masiva (bulk + set-based) contra la BD y evita duplicados. Con `esperar=true` se ejecuta de forma síncrona y devuelve el resumen (no recomendado para rangos largos). Un trabajo a la vez; el estado persiste en `dbo.TrabajoDOF`. | Query o Body JSON: `fechaInicio` (obligatorio, `yyyy-MM-dd`), `fechaFin` (obligatorio, `yyyy-MM-dd`), `articulo` (opcional), `esperar` (query, bool, default `false`). |
| `GET` | `/api/v1/dof/sincronizar/{trabajoId}`<br>*(Alias: `/api/v1/dof/descargar/{trabajoId}`)* | Estado y avance del trabajo (`en_cola`, `en_curso`, `completado`, `error`, `interrumpido`), `diaActual`/`totalDias`, `porcentajeAvance` y, al completarse, `resultado` con el resumen por tipo (encontrados, nuevos, actualizados, sin cambios) y `advertencias`. Un trabajo interrumpido por reinicio de la API se relanza con el mismo rango (sin duplicar datos). | Path: `trabajoId` (GUID). |

### 4.5. Documentos Técnicos
| Método | Ruta | Descripción | Parámetros / Body |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/articulos/69/documento-tecnico`<br>*(Alias: `/api/v1/documentos/69/documento-tecnico`)* | Obtiene la matriz de celdas e información técnica del anexo/documento del artículo 69 para la generación actual. | Ninguno. |

### 4.6. Endpoints Heredados (Compatibilidad Retroactiva)
Se mantienen para garantizar compatibilidad con consumidores de las aplicaciones anteriores (`EdosSatScraper` y `RfcStatusApi`):
| Método | Ruta | Mapeo / Equivalente | Formato de Entrada |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/EdosEfos/all69` | `GET /api/v1/rfcs` | Query opcional: `incluirHistorial`. |
| `POST` | `/api/Rfc/Listado69` | `POST /api/v1/rfcs/consultar` o `/api/v1/rfcs/estado` | Body JSON: `{ "rfcs": ["RFC1", "RFC2"] }` o array directo.<br>Query opcional: `incluirHistorial`. |

---

## 5. Estructura de la Solución `RFCMonitor`

La solución está ubicada en:
`C:\RepositorioGIT\MonitorRFC\RFCMonitor\RFCMonitor.sln`

Está desarrollada en **.NET 9** y se compone de tres proyectos principales:

| Proyecto | Tipo | Responsabilidad |
| --- | --- | --- |
| **`RFCMonitor`** | ASP.NET Core (`net9.0`) | API REST consolidada, reglas de negocio, persistencia en SQL Server y contratos compartidos. |
| **`RFCMonitor.Service`** | Worker Service (`net9.0-windows`) | Servicio de Windows en segundo plano responsable de la ingesta periódica, descarga de fuentes (SAT y SIDOF), validación criptográfica/hash, parsers, persistencia y control exclusivo de ejecución. |
| **`RFCMonitor.Agent.Manager`** | WPF Elevado (`net9.0-windows`) | Aplicación de interfaz gráfica con privilegios elevados para configuración segura (DPAPI), instalación, arranque, detención y monitoreo del servicio Worker. |

Adicionalmente:
- `RFCMonitor/database/`: Scripts SQL numerados para la estructura, versionamiento y mantenimiento en SQL Server (esquema `dbo`).
- `RFCMonitor/scripts/`: Scripts auxiliares de soporte y automatización.

---

## 6. Lineamientos de Trabajo para Agentes

1. **Ubicación de Comandos y Compilación:**
   Ejecutar los comandos siempre dentro del directorio de la solución:
   ```powershell
   Set-Location C:\RepositorioGIT\MonitorRFC\RFCMonitor
   dotnet restore .\RFCMonitor.sln
   dotnet build .\RFCMonitor.sln --configuration Release
   ```
2. **Actualización de Documentación y Pruebas:**
   - Cada vez que se agregue o modifique un endpoint en el controlador `RfcsController` (o nuevos controladores), **actualizar la sección 4 de este documento y agregar el archivo correspondiente en la carpeta de Apidog**.
3. **Seguridad y Secretos:**
   - Nunca incluir contraseñas ni secretos en texto plano dentro de archivos versionados (`appsettings.json`, scripts, etc.).
   - Utilizar variables de entorno (`ConnectionStrings__MonitorRFC`) o la configuración cifrada por DPAPI administrada por el Manager (`C:\Servicios\MonitorRFC\config\agentsettings.json`).
4. **Compatibilidad y Versionamiento:**
   - Mantener consistencia con las rutas nuevas `/api/v1/...` preservando los endpoints alias compatibles (`/api/EdosEfos/all69`, `/api/Rfc/Listado69`) requeridos por clientes legados.
5. **Base de Datos:**
   - Cualquier cambio en el modelo relacional debe reflejarse en los scripts ordenados de `RFCMonitor/database/` siguiendo las convenciones existentes.
