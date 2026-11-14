# RFCMonitor

Solución .NET 9 para recolectar los listados públicos del SAT y las publicaciones de producción del Diario Oficial de la Federación (SIDOF), conservar su evidencia e historial en SQL Server y consultar coincidencias por RFC.

## Proyectos

La solución contiene exactamente tres proyectos:

| Proyecto | Responsabilidad |
| --- | --- |
| `RFCMonitor` | API ASP.NET Core y las reglas, contratos, persistencia SQL y conectores compartidos. |
| `RFCMonitor.Service` | Worker Windows que descarga, interpreta, persiste y publica generaciones. |
| `RFCMonitor.Agent.Manager` | Aplicación WPF elevada para configurar, instalar, iniciar, detener, reiniciar y reconfigurar el Worker. |

Todos usan .NET 9 (`net9.0`; Worker y Manager usan `net9.0-windows`). No hay proyectos de pruebas en la solución.

La programación configurada en el Manager se ejecuta en el Worker: admite intervalo, diaria a una hora, semanal, cron avanzado y diaria por varias horas. La opción predeterminada es diaria a las `08:00` y `20:00`; el servicio valida la programación antes de aceptar la configuración.

## Funcionalidad migrada

- Descarga del catálogo de producción del SAT y de sus CSV/XLS para los artículos 69, 69-B y 69-B Bis.
- Conexión a producción SIDOF: `https://sidof.segob.gob.mx/dof/sidof`; no se usa el ambiente QA.
- Descubrimiento diario, descarga segura, evidencia con hash, parsers CSV/XLS/HTML, carga SQL Server, versiones e historial.
- Generaciones consistentes para las consultas, cobertura y estado operativo de las fuentes.
- API de consulta consolidada: `GET /api/v1/rfcs`, `POST /api/v1/rfcs/consultar`, rutas por artículo/listado y estado de fuentes. Se mantienen los alias heredados `GET /api/EdosEfos/all69` y `POST /api/Rfc/Listado69`.
- Servicio Windows con programación, ejecución manual por named pipe, reintentos, correo opcional y bloqueo exclusivo de ingesta.
- Manager local con configuración protegida por DPAPI y administración del servicio.

## Flujo operativo

El servicio Worker se ejecuta por calendario o por una orden manual del Manager. Obtiene un bloqueo exclusivo en SQL Server, carga el catálogo de fuentes y consulta el portal oficial del SAT. Para cada listado valida el enlace, la sección del artículo y la fecha visible de actualización; descarga el archivo, calcula su hash y conserva la evidencia local.

El parser localiza el encabezado que contiene `RFC`, aunque el CSV tenga filas informativas previas. Cada fila y cada columna se guarda como fue publicada. Los RFC con comillas, apóstrofes, acentos agudos, espacios invisibles o BOM se normalizan antes de consultarse. Los RFC incompletos o suprimidos por el SAT quedan en `FilaFuente`, con su número físico de fila y motivo, y se excluyen de las consultas por RFC sin detener la actualización. Un cambio estructural del archivo, de su cabecera o de la fuente sí impide publicar una generación nueva. Cuando todas las fuentes habilitadas son estructuralmente correctas, el Worker publica una generación consistente. También consulta SIDOF por día, conserva publicaciones DOF y registra las notas pendientes de revisión.

La API sólo lee la última generación publicada. Devuelve metadatos de fuentes, cobertura y totales junto con las apariciones de cada RFC. `incluirHistorial=true` añade versiones anteriores; el comportamiento normal devuelve únicamente las apariciones activas de la generación.

## Endpoints

| Método | Ruta | Uso |
|---|---|---|
| GET | `/` | Confirma que la API está en línea. |
| GET | `/health/live` | Sonda de vida. |
| GET | `/health/ready` | Sonda de disponibilidad; responde 503 sin generación publicada. |
| GET | `/api/v1/articulos` | Artículos soportados. |
| GET | `/api/v1/articulos/{articulo}/listados` | Listados SAT y DOF disponibles para el artículo. |
| GET | `/api/v1/fuentes/estado` | Estado, certificación, conteos y cobertura de fuentes. |
| GET | `/api/v1/rfcs` | Todos los RFC consolidados de la generación publicada. |
| POST | `/api/v1/rfcs/consultar` | Consulta masiva de RFCs; admite `articulo`, `listado` e `incluirHistorial`. |
| GET | `/api/v1/rfcs/{rfc}/estatus` | Estatus vigente de un RFC. |
| GET | `/api/v1/rfcs/{rfc}/historial` | Historial SAT y DOF de un RFC. |
| GET | `/api/v1/articulos/{articulo}/contribuyentes` | Contribuyentes de un artículo. |
| GET | `/api/v1/articulos/{articulo}/listados/{listado}/contribuyentes` | Contribuyentes de un listado. |
| GET | `/api/v1/articulos/69/documento-tecnico` | Celdas del documento técnico del artículo 69. |
| GET | `/api/v1/documentos/69/documento-tecnico` | Alias del documento técnico. |
| GET | `/api/EdosEfos/all69` | Consulta global heredada, paginada por número de página; máximo 10,000 registros y filtro opcional `tipoId`. |
| POST | `/api/Rfc/Listado69` | Alias heredado de consulta masiva. |

## Base de datos

Ejecute `database/bootstrap.sql` sobre SQL Server. Para reiniciar la base después de una prueba, ejecute `database/reset.sql` y después el bootstrap. Todos los objetos se crean y utilizan en el esquema `dbo`. Consulte [database/README.md](database/README.md) para identidades y operación.

Configure la API con `ConnectionStrings__MonitorRFC`. En el Manager, la conexión del Worker se captura como servidor, base de datos, usuario, contraseña, cifrado opcional y confianza del certificado. La confianza del certificado inicia desmarcada; al indicar la contraseña el Manager construye la cadena, la cifra con DPAPI y la guarda en `C:\Servicios\MonitorRFC\config\agentsettings.json`. Las evidencias, ejecuciones y correo se conservan en `C:\Servicios\MonitorRFC\data`. Nunca guarde secretos reales en `appsettings.json`.

## Bitácoras

La API escribe en `log\api-AAAAmmdd.log` junto a su publicación y el Worker en `C:\Servicios\MonitorRFC\log\service-AAAAmmdd.log`. Cada entrada incluye fecha UTC, nivel, componente, operación y la excepción completa cuando existe. Al iniciar un nuevo día, el archivo del día anterior se comprime como `.zip`; se conservan únicamente los últimos 15 días (incluido el actual).

## Compilación

```powershell
Set-Location C:\RepositorioGIT\MonitorRFC\RFCMonitor
dotnet restore .\RFCMonitor.sln
dotnet build .\RFCMonitor.sln --configuration Release
dotnet run --project .\RFCMonitor\RFCMonitor.csproj
```

Para instalar el Worker, publique `RFCMonitor.Service`, copie sus binarios a una ubicación protegida y use el Manager ejecutado como administrador. Para actualizarlo, seleccione el ejecutable publicado `RFCMonitor.Service.exe` en el Manager y use `Actualizar servicio`; la operación conserva la configuración y los datos, reemplaza los binarios y reinicia el servicio. La administración del servicio no se expone por el API público.
