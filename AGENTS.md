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
| `GET` | `/api/v1/articulos` | Devuelve la lista de artículos fiscales soportados (`69`, `69-b`, `69-b-bis`). | Ninguno. |
| `GET` | `/api/v1/articulos/{articulo}/listados` | Obtiene los listados y supuestos disponibles para un artículo dado (incluye orígenes SAT y Publicaciones DOF). | Path: `articulo` (ej. `69-b`). |
| `GET` | `/api/v1/fuentes/estado` | Devuelve el estado operativo y de certificación por fuente: filas leídas, aceptadas, rechazadas, última incorporación y cobertura SAT/SIDOF. | Ninguno. |

### 4.3. Consultas de Contribuyentes y RFCs
| Método | Ruta | Descripción | Parámetros / Body |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/rfcs` | Consulta consolidada de todos los RFCs y sus apariciones publicadas mediante streaming JSON de alta eficiencia. | Query opcional: `incluirHistorial` (bool, default `false`). |
| `POST` | `/api/v1/rfcs/consultar` | Consulta masiva filtrada para una lista específica de RFCs. | Query opcional: `incluirHistorial` (bool).<br>Body JSON: Array de strings `["RFC1", "RFC2"]`. |
| `GET` | `/api/v1/articulos/{articulo}/contribuyentes` | Consulta de contribuyentes asociados exclusivamente a un artículo específico. | Path: `articulo`.<br>Query opcional: `incluirHistorial` (bool). |
| `GET` | `/api/v1/articulos/{articulo}/listados/{listado}/contribuyentes` | Consulta de contribuyentes filtrando por artículo y listado concreto (ej. `definitivos`, `presuntos`, etc.). | Path: `articulo`, `listado`.<br>Query opcional: `incluirHistorial` (bool). |

### 4.4. Documentos Técnicos
| Método | Ruta | Descripción | Parámetros / Body |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/articulos/69/documento-tecnico`<br>*(Alias: `/api/v1/documentos/69/documento-tecnico`)* | Obtiene la matriz de celdas e información técnica del anexo/documento del artículo 69 para la generación actual. | Ninguno. |

### 4.5. Endpoints Heredados (Compatibilidad Retroactiva)
Se mantienen para garantizar compatibilidad con consumidores de las aplicaciones anteriores (`EdosSatScraper` y `RfcStatusApi`):
| Método | Ruta | Mapeo / Equivalente | Formato de Entrada |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/EdosEfos/all69` | `GET /api/v1/rfcs` | Query opcional: `incluirHistorial`. |
| `POST` | `/api/Rfc/Listado69` | `POST /api/v1/rfcs/consultar` | Body JSON: `{ "rfcs": ["RFC1", "RFC2"] }`.<br>Query opcional: `incluirHistorial`. |

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
