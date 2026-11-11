# Base de datos MonitorRFC

SQL Server 2019 o posterior, compatibilidad 150 o superior. Los scripts numerados se aplican en orden a una base **nueva elegida por el operador**. Son reejecutables y todos los objetos se crean en el esquema `dbo`; no requieren objetos de los proyectos originales.

```powershell
sqlcmd -S SERVIDOR -d MonitorRFC -E -b -i database/001-schema.sql
sqlcmd -S SERVIDOR -d MonitorRFC -E -b -i database/002-procedures.sql
sqlcmd -S SERVIDOR -d MonitorRFC -E -b -i database/003-operations.sql
sqlcmd -S SERVIDOR -d MonitorRFC -E -b -i database/004-generation-metadata.sql
sqlcmd -S SERVIDOR -d MonitorRFC -E -b -i database/005-observations.sql
sqlcmd -S SERVIDOR -d MonitorRFC -E -b -i database/006-source-file-audit.sql
sqlcmd -S SERVIDOR -d MonitorRFC -E -b -i database/007-remove-unused-objects.sql
sqlcmd -S SERVIDOR -d MonitorRFC -E -b -i database/008-remove-obsolete-roles.sql
```

Los scripts crean y usan únicamente objetos del esquema `dbo`; no crean roles, usuarios ni logins y no almacenan contraseñas.

| Esquema | Objetos y función |
|---|---|
| dbo | Articulo, Fuente, DocumentoCelda; catálogo inicializado por el Worker y celdas XLS |
| dbo | Publicacion, PublicacionVersion, RegistroFuente, ObservacionPublicacion; versiones inmutables y observaciones incluso sin cambios |
| dbo | FilaFuente y CampoFuente; copia fiel de cada fila/columna del archivo, incluidas filas en cuarentena |
| dbo | Ejecucion, FuenteEstado, CoberturaDof, ObjetivoCobertura, Lease, MigracionEsquema |
| dbo | Generacion, GeneracionVersion, GeneracionActiva; TVP RfcInput, función FilasGeneracion y vista Vigentes |
| dbo | Encabezado, Apariciones, Conteos, EstadoFuentes y DocumentoTecnico; procedimientos públicos parametrizados |

`dbo.PublicarGeneracion` exige un bloqueo de sesión y su fencing token. Cada importación usa transacción y BulkCopy con comprobación de restricciones. Un fallo conserva la versión previa. A→B→A crea una revisión nueva; un hash y parser idénticos a la versión activa generan una observación, no duplican el detalle.

Las generaciones son inmutables y se conservan sin purga automática: una lectura en curso nunca pierde sus versiones. Este modo aumenta almacenamiento; cualquier retención futura debe añadir coordinación con lectores antes de borrar datos. Actualmente la preparación de una versión utiliza una transacción durante el parsing/BulkCopy; mida el log y los bloqueos con su volumen.

Un único lease SQL de ingesta serializa las fuentes, aun con varios procesos. La conexión que tiene el bloqueo también escribe y no se recupera silenciosamente tras perderlo. Una generación referencia las revisiones activas e históricas al publicarse. `incluirHistorial` decide cuáles consulta, sin reinterpretar un evento DOF como el único estado legal vigente.

Verificaciones de diagnóstico de solo lectura:

```sql
SELECT * FROM dbo.MigracionEsquema ORDER BY Version;
SELECT * FROM dbo.Fuente ORDER BY FuenteKey;
EXEC dbo.Encabezado;
EXEC dbo.EstadoFuentes;
SELECT Resultado, COUNT(*) Ejecuciones FROM dbo.Ejecucion GROUP BY Resultado;
```

No basta con compilar: ejecutar scripts y una primera carga válida antes de consumir consultas. Antes de esa carga, el API devuelve 503 y no una lista vacía presentada como actualizada.
