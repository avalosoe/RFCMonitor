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

## Base de datos

Ejecute los scripts de `database/` en orden numérico sobre SQL Server. Todos los objetos se crean y utilizan en el esquema `dbo`. Consulte [database/README.md](database/README.md) para identidades y operación.

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

Para instalar el Worker, publique `RFCMonitor.Service`, copie sus binarios a una ubicación protegida y use el Manager ejecutado como administrador. La administración del servicio no se expone por el API público.
