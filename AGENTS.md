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

## 3. Estructura de la Solución `RFCMonitor`

La solución está ubicada en:
📂 `C:\RepositorioGIT\MonitorRFC\RFCMonitor\RFCMonitor.sln`

Está desarrollada en **.NET 9** y se compone de tres proyectos principales:

| Proyecto | Tipo | Responsabilidad |
| --- | --- | --- |
| **`RFCMonitor`** | ASP.NET Core (`net9.0`) | API REST consolidada (`/api/v1/rfcs`, `/api/v1/rfcs/consultar`, estado de fuentes y alias legados), reglas de negocio, persistencia en SQL Server y contratos compartidos. |
| **`RFCMonitor.Service`** | Worker Service (`net9.0-windows`) | Servicio de Windows en segundo plano responsable de la ingesta periódica, descarga de fuentes (SAT y SIDOF), validación criptográfica/hash, parsers, persistencia y control exclusivo de ejecución. |
| **`RFCMonitor.Agent.Manager`** | WPF Elevado (`net9.0-windows`) | Aplicación de interfaz gráfica con privilegios elevados para configuración segura (DPAPI), instalación, arranque, detención y monitoreo del servicio Worker. |

Adicionalmente:
- 📁 `RFCMonitor/database/`: Scripts SQL numerados para la estructura, versionamiento y mantenimiento en SQL Server (esquema `dbo`).
- 📁 `RFCMonitor/scripts/`: Scripts auxiliares de soporte y automatización.

---

## 4. Lineamientos de Trabajo para Agentes

1. **Ubicación de Comandos y Compilación:**
   Ejecutar los comandos siempre dentro del directorio de la solución:
   ```powershell
   Set-Location C:\RepositorioGIT\MonitorRFC\RFCMonitor
   dotnet restore .\RFCMonitor.sln
   dotnet build .\RFCMonitor.sln --configuration Release
   ```
2. **Seguridad y Secretos:**
   - Nunca incluir contraseñas ni secretos en texto plano dentro de archivos versionados (`appsettings.json`, scripts, etc.).
   - Utilizar variables de entorno (`ConnectionStrings__MonitorRFC`) o la configuración cifrada por DPAPI administrada por el Manager (`C:\Servicios\MonitorRFC\config\agentsettings.json`).
3. **Compatibilidad y Versionamiento:**
   - Mantener consistencia con las rutas nuevas `/api/v1/...` preservando los endpoints alias compatibles (`/api/EdosEfos/all69`, `/api/Rfc/Listado69`) requeridos por clientes legados.
4. **Base de Datos:**
   - Cualquier cambio en el modelo relacional debe reflejarse en los scripts ordenados de `RFCMonitor/database/` siguiendo las convenciones existentes.
