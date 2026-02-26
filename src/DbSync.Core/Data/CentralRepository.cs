using System.Data;
using Microsoft.Data.SqlClient;
using DbSync.Core.Models;

namespace DbSync.Core.Data;

/// <summary>
/// Acceso a datos para la base central DbSyncCentral en SQL Server.
/// Usa raw SqlCommand, consistente con DbObjectExtractor y VersionHistoryReader.
/// </summary>
public class CentralRepository
{
    private readonly string _connectionString;

    public CentralRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    // ---- Provisioning ----

    /// <summary>
    /// Crea la base de datos y todas las tablas/índices/vista si no existen.
    /// Seguro para llamar en cada inicio — usa IF NOT EXISTS en todo.
    /// </summary>
    public async Task EnsureDatabaseAsync(CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString);
        var databaseName = builder.InitialCatalog;

        // 1. Conectar a master para crear la base si no existe
        builder.InitialCatalog = "master";
        await using (var masterConn = new SqlConnection(builder.ConnectionString))
        {
            await masterConn.OpenAsync(ct);
            var createDbSql = $@"
                IF DB_ID(@DbName) IS NULL
                BEGIN
                    EXEC('CREATE DATABASE [' + @DbName + ']')
                END";
            await using var cmd = new SqlCommand(createDbSql, masterConn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@DbName", databaseName);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 2. Conectar a la base y crear tablas/índices/vista
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string schemaSql = @"
            -- ScanLogs
            IF OBJECT_ID('dbo.ScanLogs', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.ScanLogs (
                    Id              INT IDENTITY(1,1) PRIMARY KEY,
                    StartedAt       DATETIME2 NOT NULL,
                    CompletedAt     DATETIME2 NULL,
                    Status          VARCHAR(30) NOT NULL DEFAULT 'Running',
                    TriggerType     VARCHAR(20) NOT NULL DEFAULT 'Scheduled',
                    TriggeredBy     NVARCHAR(100) NULL,
                    TotalClientes   INT NOT NULL DEFAULT 0,
                    TotalAmbientes  INT NOT NULL DEFAULT 0,
                    TotalObjectsScanned   INT NOT NULL DEFAULT 0,
                    TotalChangesDetected  INT NOT NULL DEFAULT 0,
                    TotalErrors     INT NOT NULL DEFAULT 0,
                    ErrorSummary    NVARCHAR(MAX) NULL
                );
                CREATE INDEX IX_ScanLogs_StartedAt ON dbo.ScanLogs(StartedAt DESC);
            END

            -- ScanLogEntries
            IF OBJECT_ID('dbo.ScanLogEntries', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.ScanLogEntries (
                    Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
                    ScanLogId       INT NOT NULL REFERENCES dbo.ScanLogs(Id),
                    ClienteId       INT NOT NULL,
                    ClienteCodigo   VARCHAR(50) NOT NULL,
                    Ambiente        VARCHAR(10) NOT NULL,
                    StartedAt       DATETIME2 NOT NULL,
                    CompletedAt     DATETIME2 NULL,
                    Success         BIT NOT NULL DEFAULT 1,
                    ObjectsFound    INT NOT NULL DEFAULT 0,
                    ObjectsNew      INT NOT NULL DEFAULT 0,
                    ObjectsModified INT NOT NULL DEFAULT 0,
                    ObjectsDeleted  INT NOT NULL DEFAULT 0,
                    ErrorMessage    NVARCHAR(MAX) NULL,
                    DurationSeconds FLOAT NOT NULL DEFAULT 0
                );
                CREATE INDEX IX_ScanLogEntries_ScanLogId ON dbo.ScanLogEntries(ScanLogId);
                CREATE INDEX IX_ScanLogEntries_ClienteId ON dbo.ScanLogEntries(ClienteId, Ambiente);
            END

            -- ObjectSnapshots
            IF OBJECT_ID('dbo.ObjectSnapshots', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.ObjectSnapshots (
                    Id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
                    ScanLogId           INT NOT NULL REFERENCES dbo.ScanLogs(Id),
                    ClienteId           INT NOT NULL,
                    ClienteNombre       NVARCHAR(200) NOT NULL,
                    ClienteCodigo       VARCHAR(50) NOT NULL,
                    Ambiente            VARCHAR(10) NOT NULL,
                    ObjectFullName      NVARCHAR(300) NOT NULL,
                    SchemaName          NVARCHAR(128) NOT NULL,
                    ObjectName          NVARCHAR(128) NOT NULL,
                    ObjectType          VARCHAR(5) NOT NULL,
                    DefinitionHash      VARCHAR(64) NOT NULL,
                    ObjectLastModified  DATETIME2 NOT NULL,
                    SnapshotDate        DATETIME2 NOT NULL,
                    IsCustom            BIT NOT NULL DEFAULT 0
                );
                CREATE INDEX IX_ObjectSnapshots_Lookup ON dbo.ObjectSnapshots(ClienteId, Ambiente, SnapshotDate DESC);
                CREATE INDEX IX_ObjectSnapshots_Object ON dbo.ObjectSnapshots(ObjectFullName, ClienteId, Ambiente, SnapshotDate DESC);
                CREATE INDEX IX_ObjectSnapshots_ScanLog ON dbo.ObjectSnapshots(ScanLogId);
                CREATE INDEX IX_ObjectSnapshots_Hash ON dbo.ObjectSnapshots(DefinitionHash);
            END

            -- ObjectSnapshotDefinitions
            IF OBJECT_ID('dbo.ObjectSnapshotDefinitions', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.ObjectSnapshotDefinitions (
                    Id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
                    ObjectSnapshotId    BIGINT NOT NULL REFERENCES dbo.ObjectSnapshots(Id),
                    Definition          NVARCHAR(MAX) NOT NULL
                );
                CREATE UNIQUE INDEX IX_OSD_SnapshotId ON dbo.ObjectSnapshotDefinitions(ObjectSnapshotId);
            END

            -- DetectedChanges
            IF OBJECT_ID('dbo.DetectedChanges', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.DetectedChanges (
                    Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
                    ScanLogId       INT NOT NULL REFERENCES dbo.ScanLogs(Id),
                    ClienteId       INT NOT NULL,
                    ClienteCodigo   VARCHAR(50) NOT NULL,
                    Ambiente        VARCHAR(10) NOT NULL,
                    ObjectFullName  NVARCHAR(300) NOT NULL,
                    ObjectType      VARCHAR(5) NOT NULL,
                    ChangeType      VARCHAR(20) NOT NULL,
                    PreviousHash    VARCHAR(64) NULL,
                    CurrentHash     VARCHAR(64) NULL,
                    DetectedAt      DATETIME2 NOT NULL,
                    NotificationSent BIT NOT NULL DEFAULT 0
                );
                CREATE INDEX IX_DetectedChanges_ScanLog ON dbo.DetectedChanges(ScanLogId);
                CREATE INDEX IX_DetectedChanges_Cliente ON dbo.DetectedChanges(ClienteId, Ambiente, DetectedAt DESC);
                CREATE INDEX IX_DetectedChanges_Pending ON dbo.DetectedChanges(NotificationSent) WHERE NotificationSent = 0;
            END

            -- BaseVersions
            IF OBJECT_ID('dbo.BaseVersions', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.BaseVersions (
                    Id                  INT IDENTITY(1,1) PRIMARY KEY,
                    VersionName         NVARCHAR(100) NOT NULL,
                    Description         NVARCHAR(500) NULL,
                    SourceClienteId     INT NOT NULL,
                    SourceClienteNombre NVARCHAR(200) NOT NULL,
                    SourceClienteCodigo VARCHAR(50) NOT NULL,
                    SourceAmbiente      VARCHAR(10) NOT NULL,
                    TotalObjects        INT NOT NULL DEFAULT 0,
                    CreatedAt           DATETIME2 NOT NULL,
                    CreatedBy           NVARCHAR(100) NULL
                );
                CREATE UNIQUE INDEX IX_BaseVersions_Name ON dbo.BaseVersions(VersionName);
                CREATE INDEX IX_BaseVersions_CreatedAt ON dbo.BaseVersions(CreatedAt DESC);
            END

            -- BaseVersionObjects
            IF OBJECT_ID('dbo.BaseVersionObjects', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.BaseVersionObjects (
                    Id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
                    BaseVersionId       INT NOT NULL REFERENCES dbo.BaseVersions(Id) ON DELETE CASCADE,
                    ObjectFullName      NVARCHAR(300) NOT NULL,
                    SchemaName          NVARCHAR(128) NOT NULL,
                    ObjectName          NVARCHAR(128) NOT NULL,
                    ObjectType          VARCHAR(5) NOT NULL,
                    DefinitionHash      VARCHAR(64) NOT NULL,
                    SourceSnapshotId    BIGINT NOT NULL
                );
                CREATE INDEX IX_BVO_VersionId ON dbo.BaseVersionObjects(BaseVersionId);
                CREATE INDEX IX_BVO_ObjectName ON dbo.BaseVersionObjects(ObjectFullName, BaseVersionId);
            END

            -- BaseVersionObjectDefinitions
            IF OBJECT_ID('dbo.BaseVersionObjectDefinitions', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.BaseVersionObjectDefinitions (
                    Id                      BIGINT IDENTITY(1,1) PRIMARY KEY,
                    BaseVersionObjectId     BIGINT NOT NULL REFERENCES dbo.BaseVersionObjects(Id) ON DELETE CASCADE,
                    Definition              NVARCHAR(MAX) NOT NULL
                );
                CREATE UNIQUE INDEX IX_BVOD_ObjectId ON dbo.BaseVersionObjectDefinitions(BaseVersionObjectId);
            END";

        await using var schemaCmd = new SqlCommand(schemaSql, conn) { CommandTimeout = 60 };
        await schemaCmd.ExecuteNonQueryAsync(ct);

        // Vista: debe recrearse siempre (CREATE OR ALTER)
        const string viewSql = @"
            CREATE OR ALTER VIEW dbo.vw_LatestSnapshots AS
            WITH Ranked AS (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY ClienteId, Ambiente, ObjectFullName
                           ORDER BY SnapshotDate DESC
                       ) AS rn
                FROM dbo.ObjectSnapshots
            )
            SELECT Id, ScanLogId, ClienteId, ClienteNombre, ClienteCodigo, Ambiente,
                   ObjectFullName, SchemaName, ObjectName, ObjectType,
                   DefinitionHash, ObjectLastModified, SnapshotDate, IsCustom
            FROM Ranked WHERE rn = 1;";

        await using var viewCmd = new SqlCommand(viewSql, conn) { CommandTimeout = 15 };
        await viewCmd.ExecuteNonQueryAsync(ct);
    }

    // ---- Test ----

    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand(
                "SELECT DB_NAME(), @@SERVERNAME", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                return (true, $"Conectado a {reader.GetString(1)} / {reader.GetString(0)}");
            }

            return (true, "Conexión exitosa");
        }
        catch (Exception ex)
        {
            return (false, $"Error conectando al repositorio central: {ex.Message}");
        }
    }

    // ---- ScanLog ----

    public async Task<int> CreateScanLogAsync(ScanLog log, CancellationToken ct = default)
    {
        const string query = @"
            INSERT INTO dbo.ScanLogs
                (StartedAt, Status, TriggerType, TriggeredBy, TotalClientes, TotalAmbientes)
            OUTPUT INSERTED.Id
            VALUES
                (@StartedAt, @Status, @TriggerType, @TriggeredBy, @TotalClientes, @TotalAmbientes)";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };

        cmd.Parameters.AddWithValue("@StartedAt", log.StartedAt);
        cmd.Parameters.AddWithValue("@Status", log.Status.ToString());
        cmd.Parameters.AddWithValue("@TriggerType", log.Trigger.ToString());
        cmd.Parameters.AddWithValue("@TriggeredBy", (object?)log.TriggeredBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TotalClientes", log.TotalClientes);
        cmd.Parameters.AddWithValue("@TotalAmbientes", log.TotalAmbientes);

        var id = (int)(await cmd.ExecuteScalarAsync(ct))!;
        log.Id = id;
        return id;
    }

    public async Task UpdateScanLogAsync(ScanLog log, CancellationToken ct = default)
    {
        const string query = @"
            UPDATE dbo.ScanLogs SET
                CompletedAt = @CompletedAt,
                Status = @Status,
                TotalObjectsScanned = @TotalObjectsScanned,
                TotalChangesDetected = @TotalChangesDetected,
                TotalErrors = @TotalErrors,
                ErrorSummary = @ErrorSummary
            WHERE Id = @Id";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };

        cmd.Parameters.AddWithValue("@Id", log.Id);
        cmd.Parameters.AddWithValue("@CompletedAt", (object?)log.CompletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Status", log.Status.ToString());
        cmd.Parameters.AddWithValue("@TotalObjectsScanned", log.TotalObjectsScanned);
        cmd.Parameters.AddWithValue("@TotalChangesDetected", log.TotalChangesDetected);
        cmd.Parameters.AddWithValue("@TotalErrors", log.TotalErrors);
        cmd.Parameters.AddWithValue("@ErrorSummary", (object?)log.ErrorSummary ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ScanLog?> GetScanLogAsync(int id, CancellationToken ct = default)
    {
        const string query = @"
            SELECT Id, StartedAt, CompletedAt, Status, TriggerType, TriggeredBy,
                   TotalClientes, TotalAmbientes, TotalObjectsScanned,
                   TotalChangesDetected, TotalErrors, ErrorSummary
            FROM dbo.ScanLogs WHERE Id = @Id";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@Id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return MapScanLog(reader);
    }

    public async Task<List<ScanLog>> GetRecentScanLogsAsync(int top = 20, CancellationToken ct = default)
    {
        var query = $@"
            SELECT TOP ({top})
                   Id, StartedAt, CompletedAt, Status, TriggerType, TriggeredBy,
                   TotalClientes, TotalAmbientes, TotalObjectsScanned,
                   TotalChangesDetected, TotalErrors, ErrorSummary
            FROM dbo.ScanLogs
            ORDER BY StartedAt DESC";

        var results = new List<ScanLog>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapScanLog(reader));
        }

        return results;
    }

    // ---- ScanLogEntry ----

    public async Task<long> CreateScanLogEntryAsync(ScanLogEntry entry, CancellationToken ct = default)
    {
        const string query = @"
            INSERT INTO dbo.ScanLogEntries
                (ScanLogId, ClienteId, ClienteCodigo, Ambiente, StartedAt, Success)
            OUTPUT INSERTED.Id
            VALUES
                (@ScanLogId, @ClienteId, @ClienteCodigo, @Ambiente, @StartedAt, @Success)";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };

        cmd.Parameters.AddWithValue("@ScanLogId", entry.ScanLogId);
        cmd.Parameters.AddWithValue("@ClienteId", entry.ClienteId);
        cmd.Parameters.AddWithValue("@ClienteCodigo", entry.ClienteCodigo);
        cmd.Parameters.AddWithValue("@Ambiente", entry.Ambiente.ToString());
        cmd.Parameters.AddWithValue("@StartedAt", entry.StartedAt);
        cmd.Parameters.AddWithValue("@Success", entry.Success);

        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        entry.Id = id;
        return id;
    }

    public async Task UpdateScanLogEntryAsync(ScanLogEntry entry, CancellationToken ct = default)
    {
        const string query = @"
            UPDATE dbo.ScanLogEntries SET
                CompletedAt = @CompletedAt,
                Success = @Success,
                ObjectsFound = @ObjectsFound,
                ObjectsNew = @ObjectsNew,
                ObjectsModified = @ObjectsModified,
                ObjectsDeleted = @ObjectsDeleted,
                ErrorMessage = @ErrorMessage,
                DurationSeconds = @DurationSeconds
            WHERE Id = @Id";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };

        cmd.Parameters.AddWithValue("@Id", entry.Id);
        cmd.Parameters.AddWithValue("@CompletedAt", (object?)entry.CompletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Success", entry.Success);
        cmd.Parameters.AddWithValue("@ObjectsFound", entry.ObjectsFound);
        cmd.Parameters.AddWithValue("@ObjectsNew", entry.ObjectsNew);
        cmd.Parameters.AddWithValue("@ObjectsModified", entry.ObjectsModified);
        cmd.Parameters.AddWithValue("@ObjectsDeleted", entry.ObjectsDeleted);
        cmd.Parameters.AddWithValue("@ErrorMessage", (object?)entry.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DurationSeconds", entry.DurationSeconds);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<ScanLogEntry>> GetScanLogEntriesAsync(int scanLogId, CancellationToken ct = default)
    {
        const string query = @"
            SELECT Id, ScanLogId, ClienteId, ClienteCodigo, Ambiente, StartedAt, CompletedAt,
                   Success, ObjectsFound, ObjectsNew, ObjectsModified, ObjectsDeleted,
                   ErrorMessage, DurationSeconds
            FROM dbo.ScanLogEntries
            WHERE ScanLogId = @ScanLogId
            ORDER BY ClienteCodigo, Ambiente";

        var results = new List<ScanLogEntry>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@ScanLogId", scanLogId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapScanLogEntry(reader));
        }

        return results;
    }

    // ---- ObjectSnapshot ----

    /// <summary>
    /// Inserta masivamente snapshots y definiciones usando SqlBulkCopy.
    /// </summary>
    public async Task BulkInsertSnapshotsAsync(
        int scanLogId,
        List<ObjectSnapshot> snapshots,
        List<string> definitions,
        CancellationToken ct = default)
    {
        if (snapshots.Count == 0) return;
        if (snapshots.Count != definitions.Count)
            throw new ArgumentException("snapshots y definitions deben tener la misma cantidad de elementos");

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // 1. Bulk insert snapshots
        var snapshotTable = new DataTable();
        snapshotTable.Columns.Add("ScanLogId", typeof(int));
        snapshotTable.Columns.Add("ClienteId", typeof(int));
        snapshotTable.Columns.Add("ClienteNombre", typeof(string));
        snapshotTable.Columns.Add("ClienteCodigo", typeof(string));
        snapshotTable.Columns.Add("Ambiente", typeof(string));
        snapshotTable.Columns.Add("ObjectFullName", typeof(string));
        snapshotTable.Columns.Add("SchemaName", typeof(string));
        snapshotTable.Columns.Add("ObjectName", typeof(string));
        snapshotTable.Columns.Add("ObjectType", typeof(string));
        snapshotTable.Columns.Add("DefinitionHash", typeof(string));
        snapshotTable.Columns.Add("ObjectLastModified", typeof(DateTime));
        snapshotTable.Columns.Add("SnapshotDate", typeof(DateTime));
        snapshotTable.Columns.Add("IsCustom", typeof(bool));

        foreach (var s in snapshots)
        {
            snapshotTable.Rows.Add(
                scanLogId, s.ClienteId, s.ClienteNombre, s.ClienteCodigo,
                s.Ambiente.ToString(), s.ObjectFullName, s.SchemaName, s.ObjectName,
                s.ObjectType, s.DefinitionHash, s.ObjectLastModified,
                s.SnapshotDate, s.IsCustom);
        }

        using var bulkCopy = new SqlBulkCopy(conn)
        {
            DestinationTableName = "dbo.ObjectSnapshots",
            BulkCopyTimeout = 120
        };

        bulkCopy.ColumnMappings.Add("ScanLogId", "ScanLogId");
        bulkCopy.ColumnMappings.Add("ClienteId", "ClienteId");
        bulkCopy.ColumnMappings.Add("ClienteNombre", "ClienteNombre");
        bulkCopy.ColumnMappings.Add("ClienteCodigo", "ClienteCodigo");
        bulkCopy.ColumnMappings.Add("Ambiente", "Ambiente");
        bulkCopy.ColumnMappings.Add("ObjectFullName", "ObjectFullName");
        bulkCopy.ColumnMappings.Add("SchemaName", "SchemaName");
        bulkCopy.ColumnMappings.Add("ObjectName", "ObjectName");
        bulkCopy.ColumnMappings.Add("ObjectType", "ObjectType");
        bulkCopy.ColumnMappings.Add("DefinitionHash", "DefinitionHash");
        bulkCopy.ColumnMappings.Add("ObjectLastModified", "ObjectLastModified");
        bulkCopy.ColumnMappings.Add("SnapshotDate", "SnapshotDate");
        bulkCopy.ColumnMappings.Add("IsCustom", "IsCustom");

        await bulkCopy.WriteToServerAsync(snapshotTable, ct);

        // 2. Recuperar IDs generados para mapear definiciones
        var idQuery = @"
            SELECT Id, ObjectFullName
            FROM dbo.ObjectSnapshots
            WHERE ScanLogId = @ScanLogId
              AND ClienteId = @ClienteId
              AND Ambiente = @Ambiente
            ORDER BY Id";

        await using var idCmd = new SqlCommand(idQuery, conn) { CommandTimeout = 30 };
        idCmd.Parameters.AddWithValue("@ScanLogId", scanLogId);
        idCmd.Parameters.AddWithValue("@ClienteId", snapshots[0].ClienteId);
        idCmd.Parameters.AddWithValue("@Ambiente", snapshots[0].Ambiente.ToString());

        var idMap = new Dictionary<string, long>();
        await using var idReader = await idCmd.ExecuteReaderAsync(ct);
        while (await idReader.ReadAsync(ct))
        {
            var id = idReader.GetInt64(0);
            var fullName = idReader.GetString(1);
            idMap[fullName] = id;
        }
        await idReader.CloseAsync();

        // 3. Bulk insert definiciones
        var defTable = new DataTable();
        defTable.Columns.Add("ObjectSnapshotId", typeof(long));
        defTable.Columns.Add("Definition", typeof(string));

        for (int i = 0; i < snapshots.Count; i++)
        {
            if (idMap.TryGetValue(snapshots[i].ObjectFullName, out var snapshotId))
            {
                defTable.Rows.Add(snapshotId, definitions[i]);
            }
        }

        using var defBulkCopy = new SqlBulkCopy(conn)
        {
            DestinationTableName = "dbo.ObjectSnapshotDefinitions",
            BulkCopyTimeout = 120
        };

        defBulkCopy.ColumnMappings.Add("ObjectSnapshotId", "ObjectSnapshotId");
        defBulkCopy.ColumnMappings.Add("Definition", "Definition");

        await defBulkCopy.WriteToServerAsync(defTable, ct);
    }

    /// <summary>
    /// Obtiene el último snapshot de cada objeto para un cliente/ambiente.
    /// </summary>
    public async Task<List<ObjectSnapshot>> GetLatestSnapshotsAsync(
        int clienteId, string ambiente, CancellationToken ct = default)
    {
        const string query = @"
            SELECT Id, ScanLogId, ClienteId, ClienteNombre, ClienteCodigo, Ambiente,
                   ObjectFullName, SchemaName, ObjectName, ObjectType,
                   DefinitionHash, ObjectLastModified, SnapshotDate, IsCustom
            FROM dbo.vw_LatestSnapshots
            WHERE ClienteId = @ClienteId AND Ambiente = @Ambiente";

        var results = new List<ObjectSnapshot>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@ClienteId", clienteId);
        cmd.Parameters.AddWithValue("@Ambiente", ambiente);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapObjectSnapshot(reader));
        }

        return results;
    }

    /// <summary>
    /// Obtiene el último snapshot de un objeto específico en todos los clientes/ambientes.
    /// Usado para comparación cruzada.
    /// </summary>
    public async Task<List<ObjectSnapshot>> GetLatestSnapshotForObjectAsync(
        string objectFullName, CancellationToken ct = default)
    {
        const string query = @"
            SELECT Id, ScanLogId, ClienteId, ClienteNombre, ClienteCodigo, Ambiente,
                   ObjectFullName, SchemaName, ObjectName, ObjectType,
                   DefinitionHash, ObjectLastModified, SnapshotDate, IsCustom
            FROM dbo.vw_LatestSnapshots
            WHERE ObjectFullName = @ObjectFullName
            ORDER BY ClienteCodigo, Ambiente";

        var results = new List<ObjectSnapshot>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@ObjectFullName", objectFullName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapObjectSnapshot(reader));
        }

        return results;
    }

    /// <summary>
    /// Obtiene la definición de un snapshot específico.
    /// </summary>
    public async Task<string?> GetSnapshotDefinitionAsync(long snapshotId, CancellationToken ct = default)
    {
        const string query = @"
            SELECT Definition
            FROM dbo.ObjectSnapshotDefinitions
            WHERE ObjectSnapshotId = @SnapshotId";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@SnapshotId", snapshotId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    // ---- DetectedChange ----

    public async Task BulkInsertChangesAsync(List<DetectedChange> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0) return;

        var table = new DataTable();
        table.Columns.Add("ScanLogId", typeof(int));
        table.Columns.Add("ClienteId", typeof(int));
        table.Columns.Add("ClienteCodigo", typeof(string));
        table.Columns.Add("Ambiente", typeof(string));
        table.Columns.Add("ObjectFullName", typeof(string));
        table.Columns.Add("ObjectType", typeof(string));
        table.Columns.Add("ChangeType", typeof(string));
        table.Columns.Add("PreviousHash", typeof(string));
        table.Columns.Add("CurrentHash", typeof(string));
        table.Columns.Add("DetectedAt", typeof(DateTime));
        table.Columns.Add("NotificationSent", typeof(bool));

        foreach (var c in changes)
        {
            table.Rows.Add(
                c.ScanLogId, c.ClienteId, c.ClienteCodigo, c.Ambiente.ToString(),
                c.ObjectFullName, c.ObjectType, c.ChangeType.ToString(),
                (object?)c.PreviousHash ?? DBNull.Value,
                (object?)c.CurrentHash ?? DBNull.Value,
                c.DetectedAt, c.NotificationSent);
        }

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var bulkCopy = new SqlBulkCopy(conn)
        {
            DestinationTableName = "dbo.DetectedChanges",
            BulkCopyTimeout = 30
        };

        bulkCopy.ColumnMappings.Add("ScanLogId", "ScanLogId");
        bulkCopy.ColumnMappings.Add("ClienteId", "ClienteId");
        bulkCopy.ColumnMappings.Add("ClienteCodigo", "ClienteCodigo");
        bulkCopy.ColumnMappings.Add("Ambiente", "Ambiente");
        bulkCopy.ColumnMappings.Add("ObjectFullName", "ObjectFullName");
        bulkCopy.ColumnMappings.Add("ObjectType", "ObjectType");
        bulkCopy.ColumnMappings.Add("ChangeType", "ChangeType");
        bulkCopy.ColumnMappings.Add("PreviousHash", "PreviousHash");
        bulkCopy.ColumnMappings.Add("CurrentHash", "CurrentHash");
        bulkCopy.ColumnMappings.Add("DetectedAt", "DetectedAt");
        bulkCopy.ColumnMappings.Add("NotificationSent", "NotificationSent");

        await bulkCopy.WriteToServerAsync(table, ct);
    }

    public async Task<List<DetectedChange>> GetRecentChangesAsync(
        int? clienteId = null, int top = 50, CancellationToken ct = default)
    {
        var query = $@"
            SELECT TOP ({top})
                   Id, ScanLogId, ClienteId, ClienteCodigo, Ambiente,
                   ObjectFullName, ObjectType, ChangeType,
                   PreviousHash, CurrentHash, DetectedAt, NotificationSent
            FROM dbo.DetectedChanges
            WHERE (@ClienteId IS NULL OR ClienteId = @ClienteId)
            ORDER BY DetectedAt DESC";

        var results = new List<DetectedChange>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@ClienteId", (object?)clienteId ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapDetectedChange(reader));
        }

        return results;
    }

    /// <summary>
    /// Obtiene los últimos cambios detectados filtrados por lista de clientes.
    /// </summary>
    public async Task<List<DetectedChange>> GetRecentChangesForClientsAsync(
        List<int> clienteIds, int top = 50, CancellationToken ct = default)
    {
        if (clienteIds.Count == 0) return new List<DetectedChange>();

        var paramNames = clienteIds.Select((_, i) => $"@cid{i}").ToArray();
        var query = $@"
            SELECT TOP ({top})
                   Id, ScanLogId, ClienteId, ClienteCodigo, Ambiente,
                   ObjectFullName, ObjectType, ChangeType,
                   PreviousHash, CurrentHash, DetectedAt, NotificationSent
            FROM dbo.DetectedChanges
            WHERE ClienteId IN ({string.Join(",", paramNames)})
            ORDER BY DetectedAt DESC";

        var results = new List<DetectedChange>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };

        for (int i = 0; i < clienteIds.Count; i++)
            cmd.Parameters.AddWithValue($"@cid{i}", clienteIds[i]);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapDetectedChange(reader));

        return results;
    }

    /// <summary>
    /// Cuenta cambios detectados desde una fecha, filtrados por clientes.
    /// </summary>
    public async Task<int> GetChangesCountForClientsSinceAsync(
        List<int> clienteIds, DateTime since, CancellationToken ct = default)
    {
        if (clienteIds.Count == 0) return 0;

        var paramNames = clienteIds.Select((_, i) => $"@cid{i}").ToArray();
        var query = $@"
            SELECT COUNT(*)
            FROM dbo.DetectedChanges
            WHERE ClienteId IN ({string.Join(",", paramNames)})
              AND DetectedAt >= @Since";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@Since", since);

        for (int i = 0; i < clienteIds.Count; i++)
            cmd.Parameters.AddWithValue($"@cid{i}", clienteIds[i]);

        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Obtiene stats de un scan agregadas solo para los clientes indicados.
    /// </summary>
    public async Task<(int TotalClientes, int TotalObjects, int TotalChanges, int TotalErrors)>
        GetScanStatsForClientsAsync(int scanLogId, List<int> clienteIds, CancellationToken ct = default)
    {
        if (clienteIds.Count == 0) return (0, 0, 0, 0);

        var paramNames = clienteIds.Select((_, i) => $"@cid{i}").ToArray();
        var inClause = string.Join(",", paramNames);
        var query = $@"
            SELECT
                COUNT(DISTINCT e.ClienteId) AS TotalClientes,
                ISNULL(SUM(e.ObjectsFound), 0) AS TotalObjects,
                (SELECT COUNT(*) FROM dbo.DetectedChanges
                 WHERE ScanLogId = @ScanLogId AND ClienteId IN ({inClause})) AS TotalChanges,
                ISNULL(SUM(CASE WHEN e.Success = 0 THEN 1 ELSE 0 END), 0) AS TotalErrors
            FROM dbo.ScanLogEntries e
            WHERE e.ScanLogId = @ScanLogId AND e.ClienteId IN ({inClause})";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@ScanLogId", scanLogId);

        for (int i = 0; i < clienteIds.Count; i++)
            cmd.Parameters.AddWithValue($"@cid{i}", clienteIds[i]);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return (
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3)
            );
        }

        return (0, 0, 0, 0);
    }

    public async Task<List<DetectedChange>> GetPendingNotificationsAsync(CancellationToken ct = default)
    {
        const string query = @"
            SELECT Id, ScanLogId, ClienteId, ClienteCodigo, Ambiente,
                   ObjectFullName, ObjectType, ChangeType,
                   PreviousHash, CurrentHash, DetectedAt, NotificationSent
            FROM dbo.DetectedChanges
            WHERE NotificationSent = 0
            ORDER BY DetectedAt";

        var results = new List<DetectedChange>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapDetectedChange(reader));
        }

        return results;
    }

    public async Task MarkNotificationSentAsync(List<long> changeIds, CancellationToken ct = default)
    {
        if (changeIds.Count == 0) return;

        // Batch en grupos de 1000 para evitar límite de parámetros
        foreach (var batch in changeIds.Chunk(1000))
        {
            var paramNames = batch.Select((_, i) => $"@id{i}").ToArray();
            var query = $"UPDATE dbo.DetectedChanges SET NotificationSent = 1 WHERE Id IN ({string.Join(",", paramNames)})";

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 30 };

            for (int i = 0; i < batch.Length; i++)
            {
                cmd.Parameters.AddWithValue($"@id{i}", batch[i]);
            }

            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // ---- Mappers ----

    private static ScanLog MapScanLog(SqlDataReader reader)
    {
        return new ScanLog
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            StartedAt = reader.GetDateTime(reader.GetOrdinal("StartedAt")),
            CompletedAt = reader.IsDBNull(reader.GetOrdinal("CompletedAt"))
                ? null : reader.GetDateTime(reader.GetOrdinal("CompletedAt")),
            Status = Enum.Parse<ScanStatus>(reader.GetString(reader.GetOrdinal("Status"))),
            Trigger = Enum.Parse<ScanTrigger>(reader.GetString(reader.GetOrdinal("TriggerType"))),
            TriggeredBy = reader.IsDBNull(reader.GetOrdinal("TriggeredBy"))
                ? null : reader.GetString(reader.GetOrdinal("TriggeredBy")),
            TotalClientes = reader.GetInt32(reader.GetOrdinal("TotalClientes")),
            TotalAmbientes = reader.GetInt32(reader.GetOrdinal("TotalAmbientes")),
            TotalObjectsScanned = reader.GetInt32(reader.GetOrdinal("TotalObjectsScanned")),
            TotalChangesDetected = reader.GetInt32(reader.GetOrdinal("TotalChangesDetected")),
            TotalErrors = reader.GetInt32(reader.GetOrdinal("TotalErrors")),
            ErrorSummary = reader.IsDBNull(reader.GetOrdinal("ErrorSummary"))
                ? null : reader.GetString(reader.GetOrdinal("ErrorSummary"))
        };
    }

    private static ScanLogEntry MapScanLogEntry(SqlDataReader reader)
    {
        return new ScanLogEntry
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            ScanLogId = reader.GetInt32(reader.GetOrdinal("ScanLogId")),
            ClienteId = reader.GetInt32(reader.GetOrdinal("ClienteId")),
            ClienteCodigo = reader.GetString(reader.GetOrdinal("ClienteCodigo")),
            Ambiente = Enum.Parse<Ambiente>(reader.GetString(reader.GetOrdinal("Ambiente"))),
            StartedAt = reader.GetDateTime(reader.GetOrdinal("StartedAt")),
            CompletedAt = reader.IsDBNull(reader.GetOrdinal("CompletedAt"))
                ? null : reader.GetDateTime(reader.GetOrdinal("CompletedAt")),
            Success = reader.GetBoolean(reader.GetOrdinal("Success")),
            ObjectsFound = reader.GetInt32(reader.GetOrdinal("ObjectsFound")),
            ObjectsNew = reader.GetInt32(reader.GetOrdinal("ObjectsNew")),
            ObjectsModified = reader.GetInt32(reader.GetOrdinal("ObjectsModified")),
            ObjectsDeleted = reader.GetInt32(reader.GetOrdinal("ObjectsDeleted")),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage"))
                ? null : reader.GetString(reader.GetOrdinal("ErrorMessage")),
            DurationSeconds = reader.GetDouble(reader.GetOrdinal("DurationSeconds"))
        };
    }

    private static ObjectSnapshot MapObjectSnapshot(SqlDataReader reader)
    {
        return new ObjectSnapshot
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            ScanLogId = reader.GetInt32(reader.GetOrdinal("ScanLogId")),
            ClienteId = reader.GetInt32(reader.GetOrdinal("ClienteId")),
            ClienteNombre = reader.GetString(reader.GetOrdinal("ClienteNombre")),
            ClienteCodigo = reader.GetString(reader.GetOrdinal("ClienteCodigo")),
            Ambiente = Enum.Parse<Ambiente>(reader.GetString(reader.GetOrdinal("Ambiente"))),
            ObjectFullName = reader.GetString(reader.GetOrdinal("ObjectFullName")),
            SchemaName = reader.GetString(reader.GetOrdinal("SchemaName")),
            ObjectName = reader.GetString(reader.GetOrdinal("ObjectName")),
            ObjectType = reader.GetString(reader.GetOrdinal("ObjectType")),
            DefinitionHash = reader.GetString(reader.GetOrdinal("DefinitionHash")),
            ObjectLastModified = reader.GetDateTime(reader.GetOrdinal("ObjectLastModified")),
            SnapshotDate = reader.GetDateTime(reader.GetOrdinal("SnapshotDate")),
            IsCustom = reader.GetBoolean(reader.GetOrdinal("IsCustom"))
        };
    }

    private static DetectedChange MapDetectedChange(SqlDataReader reader)
    {
        return new DetectedChange
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            ScanLogId = reader.GetInt32(reader.GetOrdinal("ScanLogId")),
            ClienteId = reader.GetInt32(reader.GetOrdinal("ClienteId")),
            ClienteCodigo = reader.GetString(reader.GetOrdinal("ClienteCodigo")),
            Ambiente = Enum.Parse<Ambiente>(reader.GetString(reader.GetOrdinal("Ambiente"))),
            ObjectFullName = reader.GetString(reader.GetOrdinal("ObjectFullName")),
            ObjectType = reader.GetString(reader.GetOrdinal("ObjectType")),
            ChangeType = Enum.Parse<ObjectChangeType>(reader.GetString(reader.GetOrdinal("ChangeType"))),
            PreviousHash = reader.IsDBNull(reader.GetOrdinal("PreviousHash"))
                ? null : reader.GetString(reader.GetOrdinal("PreviousHash")),
            CurrentHash = reader.IsDBNull(reader.GetOrdinal("CurrentHash"))
                ? null : reader.GetString(reader.GetOrdinal("CurrentHash")),
            DetectedAt = reader.GetDateTime(reader.GetOrdinal("DetectedAt")),
            NotificationSent = reader.GetBoolean(reader.GetOrdinal("NotificationSent"))
        };
    }

    private static BaseVersion MapBaseVersion(SqlDataReader reader)
    {
        return new BaseVersion
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            VersionName = reader.GetString(reader.GetOrdinal("VersionName")),
            Description = reader.IsDBNull(reader.GetOrdinal("Description"))
                ? null : reader.GetString(reader.GetOrdinal("Description")),
            SourceClienteId = reader.GetInt32(reader.GetOrdinal("SourceClienteId")),
            SourceClienteNombre = reader.GetString(reader.GetOrdinal("SourceClienteNombre")),
            SourceClienteCodigo = reader.GetString(reader.GetOrdinal("SourceClienteCodigo")),
            SourceAmbiente = reader.GetString(reader.GetOrdinal("SourceAmbiente")),
            TotalObjects = reader.GetInt32(reader.GetOrdinal("TotalObjects")),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
            CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy"))
                ? null : reader.GetString(reader.GetOrdinal("CreatedBy"))
        };
    }

    private static BaseVersionObject MapBaseVersionObject(SqlDataReader reader)
    {
        return new BaseVersionObject
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            BaseVersionId = reader.GetInt32(reader.GetOrdinal("BaseVersionId")),
            ObjectFullName = reader.GetString(reader.GetOrdinal("ObjectFullName")),
            SchemaName = reader.GetString(reader.GetOrdinal("SchemaName")),
            ObjectName = reader.GetString(reader.GetOrdinal("ObjectName")),
            ObjectType = reader.GetString(reader.GetOrdinal("ObjectType")),
            DefinitionHash = reader.GetString(reader.GetOrdinal("DefinitionHash")),
            SourceSnapshotId = reader.GetInt64(reader.GetOrdinal("SourceSnapshotId"))
        };
    }

    // ---- BaseVersion ----

    public async Task<int> CreateBaseVersionAsync(BaseVersion version, CancellationToken ct = default)
    {
        const string query = @"
            INSERT INTO dbo.BaseVersions
                (VersionName, Description, SourceClienteId, SourceClienteNombre,
                 SourceClienteCodigo, SourceAmbiente, TotalObjects, CreatedAt, CreatedBy)
            OUTPUT INSERTED.Id
            VALUES
                (@VersionName, @Description, @SourceClienteId, @SourceClienteNombre,
                 @SourceClienteCodigo, @SourceAmbiente, @TotalObjects, @CreatedAt, @CreatedBy)";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };

        cmd.Parameters.AddWithValue("@VersionName", version.VersionName);
        cmd.Parameters.AddWithValue("@Description", (object?)version.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SourceClienteId", version.SourceClienteId);
        cmd.Parameters.AddWithValue("@SourceClienteNombre", version.SourceClienteNombre);
        cmd.Parameters.AddWithValue("@SourceClienteCodigo", version.SourceClienteCodigo);
        cmd.Parameters.AddWithValue("@SourceAmbiente", version.SourceAmbiente);
        cmd.Parameters.AddWithValue("@TotalObjects", version.TotalObjects);
        cmd.Parameters.AddWithValue("@CreatedAt", version.CreatedAt);
        cmd.Parameters.AddWithValue("@CreatedBy", (object?)version.CreatedBy ?? DBNull.Value);

        var id = (int)(await cmd.ExecuteScalarAsync(ct))!;
        version.Id = id;
        return id;
    }

    public async Task<List<BaseVersion>> GetAllBaseVersionsAsync(CancellationToken ct = default)
    {
        const string query = @"
            SELECT Id, VersionName, Description, SourceClienteId, SourceClienteNombre,
                   SourceClienteCodigo, SourceAmbiente, TotalObjects, CreatedAt, CreatedBy
            FROM dbo.BaseVersions
            ORDER BY CreatedAt DESC";

        var results = new List<BaseVersion>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapBaseVersion(reader));
        }

        return results;
    }

    public async Task<BaseVersion?> GetBaseVersionAsync(int id, CancellationToken ct = default)
    {
        const string query = @"
            SELECT Id, VersionName, Description, SourceClienteId, SourceClienteNombre,
                   SourceClienteCodigo, SourceAmbiente, TotalObjects, CreatedAt, CreatedBy
            FROM dbo.BaseVersions WHERE Id = @Id";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@Id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return MapBaseVersion(reader);
    }

    public async Task DeleteBaseVersionAsync(int id, CancellationToken ct = default)
    {
        const string query = "DELETE FROM dbo.BaseVersions WHERE Id = @Id";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@Id", id);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Congela los snapshots actuales de un cliente/ambiente como una versión base.
    /// Solo incluye objetos no-custom (IsCustom=0).
    /// </summary>
    public async Task<int> CreateBaseVersionFromSnapshotsAsync(
        int baseVersionId, int clienteId, string ambiente, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // 1. Leer snapshots + definiciones en una sola query
        const string snapshotQuery = @"
            SELECT s.Id AS SnapshotId, s.ObjectFullName, s.SchemaName, s.ObjectName,
                   s.ObjectType, s.DefinitionHash, d.Definition
            FROM dbo.vw_LatestSnapshots s
            INNER JOIN dbo.ObjectSnapshotDefinitions d ON d.ObjectSnapshotId = s.Id
            WHERE s.ClienteId = @ClienteId AND s.Ambiente = @Ambiente AND s.IsCustom = 0";

        var objects = new List<(long SnapshotId, string FullName, string Schema, string Name,
            string Type, string Hash, string Definition)>();

        await using (var cmd = new SqlCommand(snapshotQuery, conn) { CommandTimeout = 60 })
        {
            cmd.Parameters.AddWithValue("@ClienteId", clienteId);
            cmd.Parameters.AddWithValue("@Ambiente", ambiente);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                objects.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6)
                ));
            }
        }

        if (objects.Count == 0) return 0;

        // 2. Bulk insert BaseVersionObjects
        var objTable = new DataTable();
        objTable.Columns.Add("BaseVersionId", typeof(int));
        objTable.Columns.Add("ObjectFullName", typeof(string));
        objTable.Columns.Add("SchemaName", typeof(string));
        objTable.Columns.Add("ObjectName", typeof(string));
        objTable.Columns.Add("ObjectType", typeof(string));
        objTable.Columns.Add("DefinitionHash", typeof(string));
        objTable.Columns.Add("SourceSnapshotId", typeof(long));

        foreach (var obj in objects)
        {
            objTable.Rows.Add(baseVersionId, obj.FullName, obj.Schema, obj.Name,
                obj.Type, obj.Hash, obj.SnapshotId);
        }

        using var bulkCopy = new SqlBulkCopy(conn)
        {
            DestinationTableName = "dbo.BaseVersionObjects",
            BulkCopyTimeout = 120
        };

        bulkCopy.ColumnMappings.Add("BaseVersionId", "BaseVersionId");
        bulkCopy.ColumnMappings.Add("ObjectFullName", "ObjectFullName");
        bulkCopy.ColumnMappings.Add("SchemaName", "SchemaName");
        bulkCopy.ColumnMappings.Add("ObjectName", "ObjectName");
        bulkCopy.ColumnMappings.Add("ObjectType", "ObjectType");
        bulkCopy.ColumnMappings.Add("DefinitionHash", "DefinitionHash");
        bulkCopy.ColumnMappings.Add("SourceSnapshotId", "SourceSnapshotId");

        await bulkCopy.WriteToServerAsync(objTable, ct);

        // 3. Recuperar IDs generados
        const string idQuery = @"
            SELECT Id, ObjectFullName
            FROM dbo.BaseVersionObjects
            WHERE BaseVersionId = @VersionId
            ORDER BY Id";

        var idMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using (var idCmd = new SqlCommand(idQuery, conn) { CommandTimeout = 30 })
        {
            idCmd.Parameters.AddWithValue("@VersionId", baseVersionId);
            await using var idReader = await idCmd.ExecuteReaderAsync(ct);
            while (await idReader.ReadAsync(ct))
            {
                idMap[idReader.GetString(1)] = idReader.GetInt64(0);
            }
        }

        // 4. Bulk insert definiciones
        var defTable = new DataTable();
        defTable.Columns.Add("BaseVersionObjectId", typeof(long));
        defTable.Columns.Add("Definition", typeof(string));

        foreach (var obj in objects)
        {
            if (idMap.TryGetValue(obj.FullName, out var objId))
            {
                defTable.Rows.Add(objId, obj.Definition);
            }
        }

        using var defBulkCopy = new SqlBulkCopy(conn)
        {
            DestinationTableName = "dbo.BaseVersionObjectDefinitions",
            BulkCopyTimeout = 120
        };

        defBulkCopy.ColumnMappings.Add("BaseVersionObjectId", "BaseVersionObjectId");
        defBulkCopy.ColumnMappings.Add("Definition", "Definition");

        await defBulkCopy.WriteToServerAsync(defTable, ct);

        // 5. Actualizar TotalObjects
        const string updateQuery = "UPDATE dbo.BaseVersions SET TotalObjects = @Total WHERE Id = @Id";
        await using var updateCmd = new SqlCommand(updateQuery, conn) { CommandTimeout = 15 };
        updateCmd.Parameters.AddWithValue("@Total", objects.Count);
        updateCmd.Parameters.AddWithValue("@Id", baseVersionId);
        await updateCmd.ExecuteNonQueryAsync(ct);

        return objects.Count;
    }

    public async Task<List<BaseVersionObject>> GetBaseVersionObjectsAsync(
        int baseVersionId, CancellationToken ct = default)
    {
        const string query = @"
            SELECT Id, BaseVersionId, ObjectFullName, SchemaName, ObjectName,
                   ObjectType, DefinitionHash, SourceSnapshotId
            FROM dbo.BaseVersionObjects
            WHERE BaseVersionId = @VersionId
            ORDER BY ObjectFullName";

        var results = new List<BaseVersionObject>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@VersionId", baseVersionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapBaseVersionObject(reader));
        }

        return results;
    }

    public async Task<string?> GetBaseVersionObjectDefinitionAsync(
        long baseVersionObjectId, CancellationToken ct = default)
    {
        const string query = @"
            SELECT Definition
            FROM dbo.BaseVersionObjectDefinitions
            WHERE BaseVersionObjectId = @ObjectId";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@ObjectId", baseVersionObjectId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    /// <summary>
    /// Carga todos los objetos de una versión con sus definiciones (para comparación).
    /// </summary>
    public async Task<List<(BaseVersionObject Object, string Definition)>>
        GetBaseVersionObjectsWithDefinitionsAsync(int baseVersionId, CancellationToken ct = default)
    {
        const string query = @"
            SELECT o.Id, o.BaseVersionId, o.ObjectFullName, o.SchemaName, o.ObjectName,
                   o.ObjectType, o.DefinitionHash, o.SourceSnapshotId, d.Definition
            FROM dbo.BaseVersionObjects o
            INNER JOIN dbo.BaseVersionObjectDefinitions d ON d.BaseVersionObjectId = o.Id
            WHERE o.BaseVersionId = @VersionId
            ORDER BY o.ObjectFullName";

        var results = new List<(BaseVersionObject Object, string Definition)>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 60 };
        cmd.Parameters.AddWithValue("@VersionId", baseVersionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var obj = new BaseVersionObject
            {
                Id = reader.GetInt64(0),
                BaseVersionId = reader.GetInt32(1),
                ObjectFullName = reader.GetString(2),
                SchemaName = reader.GetString(3),
                ObjectName = reader.GetString(4),
                ObjectType = reader.GetString(5),
                DefinitionHash = reader.GetString(6),
                SourceSnapshotId = reader.GetInt64(7)
            };
            var definition = reader.GetString(8);
            results.Add((obj, definition));
        }

        return results;
    }
}
