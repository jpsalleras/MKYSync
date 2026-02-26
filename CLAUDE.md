# DbSync — Sistema de Sincronización de Objetos SQL Server

## Contexto del Proyecto

Sistema para mantener sincronizados Stored Procedures, Views y Functions entre ambientes DEV, QA y PR de más de 50 clientes. Cada cliente tiene su propia instancia de SQL Server con al menos 2 ambientes (QA y PR), algunos tienen DEV también.

### Problema que resuelve

- Los SPs se modifican en producción y no se pasan a QA (o viceversa)
- El desarrollo se hace sobre una base exclusiva y luego hay que mergear a QA y PR
- Cada cliente tiene objetos base (comunes a todos) y objetos custom (específicos)
- No hay versionado en Git de los objetos de base de datos
- Existe un sistema de version control con DDL triggers que registra cada cambio (CREATE/ALTER/DROP) de SPs, Views y Functions en una tabla `ObjectChangeHistory` en cada base de datos monitoreada

## Arquitectura

```
DbSync.sln
├── src/
│   ├── DbSync.Core/          # Biblioteca principal (.NET 8 class library)
│   │   ├── Models/            # Modelos de dominio
│   │   ├── Services/          # Lógica de negocio
│   │   └── Data/              # EF Core context (SQLite)
│   ├── DbSync.Web/            # Interfaz web (ASP.NET Core Razor Pages + Bootstrap 5)
│   │   └── Pages/
│   │       ├── Clientes/      # ABM de clientes
│   │       ├── Compare/       # Comparación entre ambientes con diff visual
│   │       ├── Versions/      # Lectura de historial de DBVersionControl
│   │       └── Sync/          # Historial de sincronizaciones ejecutadas
│   └── DbSync.Console/        # CLI (System.CommandLine)
└── db/
```

## Tecnologías

- .NET 8, C#
- ASP.NET Core Razor Pages con Bootstrap 5
- SQLite + EF Core para configuración local (clientes, ambientes, historial de sync)
- Microsoft.Data.SqlClient para conectar a SQL Server
- DiffPlex para generación de diffs side-by-side
- System.CommandLine para la CLI
- Bootstrap Icons para iconografía

## Modelos Principales

### Cliente
- Id, Nombre, Codigo (usado para detectar objetos custom por convención), Activo
- VersionControlDb: nombre de la base de version control (default "DBVersionControl")
- Ambientes: lista de ClienteAmbiente (DEV, QA, PR con Server, Database, UserId, Password)
- ObjetosCustom: lista de objetos marcados como custom de ese cliente

### DbObject
- Representa un SP/View/Function extraído de SQL Server via sys.sql_modules
- Tiene FullName (schema.nombre), Definition, NormalizedDefinition, DefinitionHash (SHA256)

### CompareResult / ComparisonSummary
- Resultado de comparar dos ambientes: Equal, Modified, OnlyInSource, OnlyInTarget
- Incluye DiffHtml generado con DiffPlex, LinesAdded, LinesRemoved, flag IsCustom

### ObjectVersionEntry / ObjectVersionSummary
- Mapeo de la tabla ObjectChangeHistory en cada base monitoreada
- HistoryID, DatabaseName, SchemaName, ObjectName, ObjectType, EventType, ObjectDefinition, VersionNumber, ModifiedBy, ModifiedDate, HostName, ApplicationName

### SyncHistory
- Registro local de cada sincronización ejecutada desde la herramienta
- Incluye script ejecutado, definición anterior (backup), resultado

## Servicios

### DbObjectExtractor
- ExtractAllAsync: extrae todos los objetos de un SQL Server via sys.objects + sys.sql_modules
- ExtractSingleAsync: extrae un objeto específico
- TestConnectionAsync: prueba conectividad

### DbComparer
- CompareAsync: compara todos los objetos entre dos ambientes de un cliente
- Genera diff HTML con DiffPlex (side-by-side)
- Detecta objetos custom por tabla y por convención de nombre (contiene código del cliente)

### ScriptGenerator
- GenerateSyncScript: genera CREATE OR ALTER para sincronizar un objeto
- GenerateBatchSyncScript: genera script batch con transacción
- GenerateBackupScript: backup de definición anterior
- Siempre usa CREATE OR ALTER (SQL Server 2016 SP1+)

### SyncExecutor
- ExecuteSingleAsync / ExecuteBatchAsync
- Ejecuta contra SQL Server, hace backup previo, registra en SyncHistory

### VersionHistoryReader
- Lee la tabla ObjectChangeHistory directamente de cada base de datos del cliente
- GetStatsAsync: lista SPs versionados con cantidad de versiones
- GetHistoryAsync: historial de un SP específico
- GetVersionAsync: obtiene una versión por HistoryID
- CompareVersionsAsync: diff entre dos versiones históricas
- CompareVersionVsCurrentAsync: diff entre versión histórica y definición actual en un ambiente
- CheckVersionControlDbAsync: verifica que existe la base y tabla

## Tabla ObjectChangeHistory (en cada base monitoreada)

```sql
CREATE TABLE dbo.ObjectChangeHistory (
    HistoryID INT IDENTITY(1,1) PRIMARY KEY,
    DatabaseName NVARCHAR(128) NOT NULL,
    SchemaName NVARCHAR(128) NOT NULL,
    ObjectName NVARCHAR(128) NOT NULL,
    ObjectType NVARCHAR(20) NOT NULL,       -- PROCEDURE, VIEW, FUNCTION
    EventType NVARCHAR(50) NOT NULL,        -- CREATE_PROCEDURE, ALTER_VIEW, DROP_FUNCTION, etc.
    ObjectDefinition NVARCHAR(MAX) NULL,
    ModifiedBy NVARCHAR(128) NOT NULL,
    ModifiedDate DATETIME2 NOT NULL DEFAULT GETDATE(),
    HostName NVARCHAR(128) NULL,
    ApplicationName NVARCHAR(128) NULL,
    VersionNumber INT NOT NULL,
    EventData XML NULL
);
```

Se llena automáticamente via DDL trigger `trg_ObjectVersionControl` en cada base monitoreada.
Trackea SPs, Views y Functions (9 eventos DDL).

Los SPs de consulta existentes en esa base son:
- usp_GetObjectHistory: historial filtrable (por nombre, tipo, schema)
- usp_GetObjectVersion: versión específica por HistoryID
- usp_CompareObjectVersions: compara dos versiones
- usp_RestoreObjectVersion: restaura una versión anterior
- usp_GetVersionControlStats: estadísticas generales

## Páginas Web

| Ruta | Función |
|------|---------|
| / | Dashboard con stats generales |
| /Clientes | Lista de clientes |
| /Clientes/Edit?id=N | Crear/editar cliente con sus ambientes |
| /Compare?clienteId=N&origen=DEV&destino=QA | Comparar ambientes con diff visual |
| /Versions?clienteId=N&ambiente=PR | Historial de versiones desde DBVersionControl |
| /Versions/Compare?clienteId=N&historyId1=X&historyId2=Y | Diff entre versiones |
| /Versions/Compare?clienteId=N&historyId1=X&vsActual=true | Diff versión vs actual |
| /Versions/Detail?clienteId=N&historyId=X | Ver definición de una versión |
| /Sync/History | Historial de sincronizaciones ejecutadas |

## Comandos CLI

```bash
# Comparar ambientes
dbsync compare --cliente HOSP-CENTRAL --origen DEV --destino QA --tipo SP

# Exportar objetos a archivos .sql
dbsync export --cliente HOSP-CENTRAL --ambiente PR --output ./export

# Probar conexión
dbsync test --cliente HOSP-CENTRAL

# Historial de versiones (stats generales)
dbsync history --cliente HOSP-CENTRAL --ambiente PR

# Historial de un SP específico
dbsync history --cliente HOSP-CENTRAL --ambiente PR --sp dbo.usp_MiProcedimiento

# Diff de las últimas 2 versiones en consola
dbsync history --cliente HOSP-CENTRAL --sp dbo.usp_MiProcedimiento --compare-last
```

## Convenciones

- Los objetos custom se detectan de dos formas:
  1. Registro explícito en la tabla ObjetosCustom (SQLite local)
  2. Convención de nombre: si el nombre del objeto contiene el código del cliente (ej: `usp_HOSP_ReporteCustom`)
- Los custom se muestran en comparaciones pero NO se sincronizan automáticamente
- Siempre se hace backup de la definición anterior antes de modificar
- Las passwords se guardan en SQLite (pendiente: implementar encriptación con DPAPI/AES)

## Pendientes / Próximos Pasos

- [ ] Funcionalidad de sincronización desde la web con preview y confirmación
- [ ] Encriptación de passwords en SQLite
- [ ] Exportación de SPs a Git para tener historial de cambios completo
- [ ] Comparación cruzada entre ambientes de distintos clientes (para detectar si un SP base divergió)
- [ ] Notificaciones o alertas cuando se detectan diferencias
- [ ] Página de objetos custom por cliente (ABM)
- [ ] Soporte para comparar también la tabla ObjectChangeHistory entre ambientes (ver qué cambios se hicieron en PR que no están en QA)
- [ ] Restaurar una versión desde la web (usando usp_RestoreProcedureVersion)
- [ ] Tests unitarios

## Stack del Desarrollador

El desarrollador trabaja en software de salud (SaaS para clínicas y centros de salud) hace 20 años. Conocimientos: VB6, VB.NET, C# .NET Core, jQuery, Razor, SQL Server (stored procedures, views, functions), Reporting Services, HTML, CSS, Bootstrap. Conocimientos muy básicos de Angular.
