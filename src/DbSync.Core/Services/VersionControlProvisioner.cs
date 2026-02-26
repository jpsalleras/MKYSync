using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DbSync.Core.Services;

/// <summary>
/// Crea la infraestructura de version control directamente en la base de datos del cliente.
/// Incluye: tabla ObjectChangeHistory, DDL trigger y SPs de consulta.
/// Trackea Stored Procedures, Views y Functions.
/// </summary>
public class VersionControlProvisioner
{
    private readonly ILogger<VersionControlProvisioner> _logger;

    public VersionControlProvisioner(ILogger<VersionControlProvisioner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Resultado detallado del provisioning.
    /// </summary>
    public class ProvisionResult
    {
        public bool Success { get; set; }
        public List<string> Steps { get; set; } = new();
        public string? Error { get; set; }
    }

    /// <summary>
    /// Verifica el estado actual de version control en la base del cliente.
    /// </summary>
    public async Task<(bool TableExists, bool TriggerExists, string Message)> CheckStatusAsync(
        string connectionString, CancellationToken ct = default)
    {
        bool tableExists = false, triggerExists = false;

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            // Verificar tabla
            await using var tableCmd = new SqlCommand(
                "SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'ObjectChangeHistory'", conn);
            tableExists = await tableCmd.ExecuteScalarAsync(ct) != null;

            // Verificar trigger
            await using var trigCmd = new SqlCommand(
                "SELECT 1 FROM sys.triggers WHERE name = 'trg_ObjectVersionControl' AND parent_class = 0", conn);
            triggerExists = await trigCmd.ExecuteScalarAsync(ct) != null;

            var status = (tableExists, triggerExists) switch
            {
                (true, true) => "Completo: tabla y trigger OK",
                (true, false) => "Falta trigger",
                (false, _) => "Falta tabla ObjectChangeHistory",
            };

            return (tableExists, triggerExists, status);
        }
        catch (Exception ex)
        {
            return (tableExists, triggerExists, $"Error verificando: {ex.Message}");
        }
    }

    /// <summary>
    /// Ejecuta el provisioning completo en la base del cliente: crea tabla, SPs y trigger.
    /// Drop y recrea SPs y trigger para asegurar última versión.
    /// </summary>
    public async Task<ProvisionResult> ProvisionAsync(
        string connectionString, CancellationToken ct = default)
    {
        var result = new ProvisionResult();

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            // 1. Crear tabla ObjectChangeHistory si no existe
            await using (var checkCmd = new SqlCommand(
                "SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'ObjectChangeHistory'", conn))
            {
                if (await checkCmd.ExecuteScalarAsync(ct) == null)
                {
                    await using var createCmd = new SqlCommand(SqlCreateTable, conn);
                    createCmd.CommandTimeout = 30;
                    await createCmd.ExecuteNonQueryAsync(ct);
                    result.Steps.Add("Tabla ObjectChangeHistory creada");
                    _logger.LogInformation("Tabla ObjectChangeHistory creada en {Db}", conn.Database);
                }
                else
                {
                    result.Steps.Add("Tabla ObjectChangeHistory ya existe");
                }
            }

            // 2. Crear SPs de consulta (drop + create para actualizar)
            await CreateStoredProceduresAsync(conn, result, ct);

            // 3. Crear DDL trigger (drop + create para actualizar)
            await CreateTriggerAsync(conn, result, ct);

            result.Success = true;
            _logger.LogInformation("Provisioning completado para {Db}", conn.Database);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger.LogError(ex, "Error en provisioning");
        }

        return result;
    }

    /// <summary>
    /// Desactiva el versionado: elimina el DDL trigger.
    /// La tabla y SPs se mantienen para conservar el historial.
    /// </summary>
    public async Task<ProvisionResult> DeactivateAsync(
        string connectionString, CancellationToken ct = default)
    {
        var result = new ProvisionResult();

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            await using var checkCmd = new SqlCommand(
                "SELECT 1 FROM sys.triggers WHERE name = 'trg_ObjectVersionControl' AND parent_class = 0", conn);

            if (await checkCmd.ExecuteScalarAsync(ct) != null)
            {
                await using var dropCmd = new SqlCommand(
                    "DROP TRIGGER [trg_ObjectVersionControl] ON DATABASE", conn);
                dropCmd.CommandTimeout = 15;
                await dropCmd.ExecuteNonQueryAsync(ct);
                result.Steps.Add("DDL Trigger eliminado");
                _logger.LogInformation("Trigger eliminado en {Db}", conn.Database);
            }
            else
            {
                result.Steps.Add("El trigger no existia");
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger.LogError(ex, "Error al desactivar versionado");
        }

        return result;
    }

    private async Task CreateTriggerAsync(SqlConnection conn, ProvisionResult result, CancellationToken ct)
    {
        // Drop trigger si existe (para recrear con última versión)
        await using (var dropCmd = new SqlCommand(
            @"IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = 'trg_ObjectVersionControl' AND parent_class = 0)
              DROP TRIGGER [trg_ObjectVersionControl] ON DATABASE", conn))
        {
            dropCmd.CommandTimeout = 15;
            await dropCmd.ExecuteNonQueryAsync(ct);
        }

        await using var createCmd = new SqlCommand(SqlTrigger, conn);
        createCmd.CommandTimeout = 30;
        await createCmd.ExecuteNonQueryAsync(ct);
        result.Steps.Add("DDL Trigger trg_ObjectVersionControl creado");
        _logger.LogInformation("Trigger creado en {Db}", conn.Database);
    }

    private async Task CreateStoredProceduresAsync(SqlConnection conn, ProvisionResult result, CancellationToken ct)
    {
        var sps = new (string Name, string Sql)[]
        {
            ("usp_GetObjectHistory", SqlGetObjectHistory),
            ("usp_GetObjectVersion", SqlGetObjectVersion),
            ("usp_GetVersionControlStats", SqlGetVersionControlStats),
            ("usp_CompareObjectVersions", SqlCompareObjectVersions),
            ("usp_RestoreObjectVersion", SqlRestoreObjectVersion)
        };

        foreach (var (name, sql) in sps)
        {
            // Drop y recrear para asegurar última versión
            await using var dropCmd = new SqlCommand(
                $"IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_NAME = '{name}') DROP PROCEDURE [{name}]", conn);
            dropCmd.CommandTimeout = 15;
            await dropCmd.ExecuteNonQueryAsync(ct);

            await using var createCmd = new SqlCommand(sql, conn);
            createCmd.CommandTimeout = 15;
            await createCmd.ExecuteNonQueryAsync(ct);
            result.Steps.Add($"SP {name} creado");
        }
    }

    // ---- SQL Scripts ----

    private const string SqlCreateTable = @"
CREATE TABLE dbo.ObjectChangeHistory (
    HistoryID INT IDENTITY(1,1) PRIMARY KEY,
    DatabaseName NVARCHAR(128) NOT NULL,
    SchemaName NVARCHAR(128) NOT NULL,
    ObjectName NVARCHAR(128) NOT NULL,
    ObjectType NVARCHAR(20) NOT NULL,
    EventType NVARCHAR(50) NOT NULL,
    ObjectDefinition NVARCHAR(MAX) NULL,
    ModifiedBy NVARCHAR(128) NOT NULL,
    ModifiedDate DATETIME2 NOT NULL DEFAULT GETDATE(),
    HostName NVARCHAR(128) NULL,
    ApplicationName NVARCHAR(128) NULL,
    VersionNumber INT NOT NULL,
    EventData XML NULL
);

CREATE NONCLUSTERED INDEX IX_ObjectHistory_DB_Schema_Name
ON dbo.ObjectChangeHistory (DatabaseName, SchemaName, ObjectName);

CREATE NONCLUSTERED INDEX IX_ObjectHistory_ModifiedDate
ON dbo.ObjectChangeHistory (ModifiedDate DESC);

CREATE NONCLUSTERED INDEX IX_ObjectHistory_ObjectType
ON dbo.ObjectChangeHistory (ObjectType);
";

    private const string SqlTrigger = @"
CREATE TRIGGER [trg_ObjectVersionControl]
ON DATABASE
FOR CREATE_PROCEDURE, ALTER_PROCEDURE, DROP_PROCEDURE,
    CREATE_VIEW, ALTER_VIEW, DROP_VIEW,
    CREATE_FUNCTION, ALTER_FUNCTION, DROP_FUNCTION
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EventData XML = EVENTDATA();
    DECLARE @EventType NVARCHAR(50) = @EventData.value('(/EVENT_INSTANCE/EventType)[1]', 'NVARCHAR(50)');
    DECLARE @SchemaName NVARCHAR(128) = @EventData.value('(/EVENT_INSTANCE/SchemaName)[1]', 'NVARCHAR(128)');
    DECLARE @ObjectName NVARCHAR(128) = @EventData.value('(/EVENT_INSTANCE/ObjectName)[1]', 'NVARCHAR(128)');
    DECLARE @DatabaseName NVARCHAR(128) = DB_NAME();
    DECLARE @LoginName NVARCHAR(128) = @EventData.value('(/EVENT_INSTANCE/LoginName)[1]', 'NVARCHAR(128)');
    DECLARE @HostName NVARCHAR(128) = HOST_NAME();
    DECLARE @AppName NVARCHAR(128) = APP_NAME();

    -- Determinar tipo de objeto desde el EventType
    DECLARE @ObjectType NVARCHAR(20) = CASE
        WHEN @EventType LIKE '%PROCEDURE%' THEN 'PROCEDURE'
        WHEN @EventType LIKE '%VIEW%' THEN 'VIEW'
        WHEN @EventType LIKE '%FUNCTION%' THEN 'FUNCTION'
    END;

    DECLARE @Definition NVARCHAR(MAX) = NULL;
    DECLARE @NextVersion INT = 1;

    -- Obtener la definicion actual (solo para CREATE/ALTER, no DROP)
    -- sys.sql_modules funciona para SP, VIEW y FUNCTION
    IF @EventType NOT LIKE 'DROP%'
    BEGIN
        SELECT @Definition = sm.definition
        FROM sys.sql_modules sm
        INNER JOIN sys.objects o ON sm.object_id = o.object_id
        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
        WHERE s.name = @SchemaName AND o.name = @ObjectName;
    END

    -- Calcular proximo numero de version por objeto y tipo
    SELECT @NextVersion = ISNULL(MAX(VersionNumber), 0) + 1
    FROM dbo.ObjectChangeHistory
    WHERE DatabaseName = @DatabaseName
      AND SchemaName = @SchemaName
      AND ObjectName = @ObjectName
      AND ObjectType = @ObjectType;

    -- Insertar en historial
    INSERT INTO dbo.ObjectChangeHistory
        (DatabaseName, SchemaName, ObjectName, ObjectType, EventType,
         ObjectDefinition, ModifiedBy, ModifiedDate,
         HostName, ApplicationName, VersionNumber, EventData)
    VALUES
        (@DatabaseName, @SchemaName, @ObjectName, @ObjectType, @EventType,
         @Definition, @LoginName, GETDATE(),
         @HostName, @AppName, @NextVersion, @EventData);
END;
";

    private const string SqlGetObjectHistory = @"
CREATE PROCEDURE dbo.usp_GetObjectHistory
    @DatabaseName NVARCHAR(128) = NULL,
    @SchemaName NVARCHAR(128) = NULL,
    @ObjectName NVARCHAR(128) = NULL,
    @ObjectType NVARCHAR(20) = NULL,
    @MaxResults INT = 100
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@MaxResults)
        HistoryID, DatabaseName, SchemaName, ObjectName, ObjectType,
        EventType, ObjectDefinition, ModifiedBy, ModifiedDate,
        HostName, ApplicationName, VersionNumber
    FROM dbo.ObjectChangeHistory
    WHERE (@DatabaseName IS NULL OR DatabaseName = @DatabaseName)
      AND (@SchemaName IS NULL OR SchemaName = @SchemaName)
      AND (@ObjectName IS NULL OR ObjectName = @ObjectName)
      AND (@ObjectType IS NULL OR ObjectType = @ObjectType)
    ORDER BY ModifiedDate DESC;
END;
";

    private const string SqlGetObjectVersion = @"
CREATE PROCEDURE dbo.usp_GetObjectVersion
    @HistoryID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT HistoryID, DatabaseName, SchemaName, ObjectName, ObjectType,
           EventType, ObjectDefinition, ModifiedBy, ModifiedDate,
           HostName, ApplicationName, VersionNumber
    FROM dbo.ObjectChangeHistory
    WHERE HistoryID = @HistoryID;
END;
";

    private const string SqlGetVersionControlStats = @"
CREATE PROCEDURE dbo.usp_GetVersionControlStats
    @DatabaseName NVARCHAR(128) = NULL,
    @ObjectType NVARCHAR(20) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT DatabaseName, SchemaName, ObjectName, ObjectType,
           COUNT(*) AS TotalVersions,
           MAX(VersionNumber) AS CurrentVersion,
           MIN(ModifiedDate) AS FirstModified,
           MAX(ModifiedDate) AS LastModified,
           COUNT(DISTINCT ModifiedBy) AS TotalContributors
    FROM dbo.ObjectChangeHistory
    WHERE (@DatabaseName IS NULL OR DatabaseName = @DatabaseName)
      AND (@ObjectType IS NULL OR ObjectType = @ObjectType)
    GROUP BY DatabaseName, SchemaName, ObjectName, ObjectType
    ORDER BY MAX(ModifiedDate) DESC;
END;
";

    private const string SqlCompareObjectVersions = @"
CREATE PROCEDURE dbo.usp_CompareObjectVersions
    @HistoryID1 INT,
    @HistoryID2 INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT HistoryID, DatabaseName, SchemaName, ObjectName, ObjectType,
           EventType, ObjectDefinition, ModifiedBy, ModifiedDate,
           VersionNumber
    FROM dbo.ObjectChangeHistory
    WHERE HistoryID IN (@HistoryID1, @HistoryID2)
    ORDER BY HistoryID;
END;
";

    private const string SqlRestoreObjectVersion = @"
CREATE PROCEDURE dbo.usp_RestoreObjectVersion
    @HistoryID INT,
    @ExecutedBy NVARCHAR(128) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Definition NVARCHAR(MAX);
    DECLARE @SchemaName NVARCHAR(128);
    DECLARE @ObjectName NVARCHAR(128);
    DECLARE @ObjectType NVARCHAR(20);
    DECLARE @DatabaseName NVARCHAR(128);

    SELECT @Definition = ObjectDefinition,
           @SchemaName = SchemaName,
           @ObjectName = ObjectName,
           @ObjectType = ObjectType,
           @DatabaseName = DatabaseName
    FROM dbo.ObjectChangeHistory
    WHERE HistoryID = @HistoryID;

    IF @Definition IS NULL
    BEGIN
        RAISERROR('Version %d no encontrada o sin definicion', 16, 1, @HistoryID);
        RETURN;
    END

    -- Retornar el script para que el caller lo ejecute en la base target
    SELECT @Definition AS RestoreScript,
           @SchemaName AS SchemaName,
           @ObjectName AS ObjectName,
           @ObjectType AS ObjectType,
           @DatabaseName AS DatabaseName,
           @HistoryID AS SourceHistoryID,
           @ExecutedBy AS ExecutedBy;
END;
";
}
