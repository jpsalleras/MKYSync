using Microsoft.Data.SqlClient;
using DbSync.Core.Models;

namespace DbSync.Core.Services;

/// <summary>
/// Extrae objetos (SPs, Views, Functions) desde una base de datos SQL Server.
/// </summary>
public class DbObjectExtractor
{
    private const string ExtractQuery = @"
        SELECT 
            o.name              AS ObjectName,
            s.name              AS SchemaName,
            o.type              AS ObjectType,
            m.definition        AS Definition,
            o.modify_date       AS LastModified
        FROM sys.objects o
        INNER JOIN sys.sql_modules m ON o.object_id = m.object_id
        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
        WHERE o.type IN ('P','V','FN','IF','TF')
          AND o.is_ms_shipped = 0
        ORDER BY o.type, s.name, o.name";

    private const string ExtractSingleQuery = @"
        SELECT 
            o.name              AS ObjectName,
            s.name              AS SchemaName,
            o.type              AS ObjectType,
            m.definition        AS Definition,
            o.modify_date       AS LastModified
        FROM sys.objects o
        INNER JOIN sys.sql_modules m ON o.object_id = m.object_id
        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
        WHERE o.is_ms_shipped = 0
          AND s.name = @SchemaName
          AND o.name = @ObjectName";

    /// <summary>
    /// Extrae todos los objetos de la base de datos.
    /// </summary>
    public async Task<List<DbObject>> ExtractAllAsync(string connectionString, CancellationToken ct = default)
    {
        var objects = new List<DbObject>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(ExtractQuery, conn);
        cmd.CommandTimeout = 60;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            objects.Add(MapFromReader(reader));
        }

        return objects;
    }

    /// <summary>
    /// Extrae un objeto específico por schema y nombre.
    /// </summary>
    public async Task<DbObject?> ExtractSingleAsync(string connectionString, string schemaName, string objectName, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(ExtractSingleQuery, conn);
        cmd.CommandTimeout = 30;
        cmd.Parameters.AddWithValue("@SchemaName", schemaName);
        cmd.Parameters.AddWithValue("@ObjectName", objectName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return MapFromReader(reader);
        }

        return null;
    }

    /// <summary>
    /// Extrae solo los nombres de objetos (sin definición) para comparación rápida por hash.
    /// </summary>
    public async Task<List<(string FullName, DbObjectType Type, DateTime LastModified)>> ExtractNamesAsync(
        string connectionString, CancellationToken ct = default)
    {
        var result = new List<(string, DbObjectType, DateTime)>();

        const string query = @"
            SELECT s.name + '.' + o.name AS FullName,
                   o.type AS ObjectType,
                   o.modify_date AS LastModified
            FROM sys.objects o
            INNER JOIN sys.sql_modules m ON o.object_id = m.object_id
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.type IN ('P','V','FN','IF','TF')
              AND o.is_ms_shipped = 0
            ORDER BY o.type, s.name, o.name";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            result.Add((
                reader.GetString(0),
                DbObjectTypeExtensions.FromSqlType(reader.GetString(1)),
                reader.GetDateTime(2)
            ));
        }

        return result;
    }

    /// <summary>
    /// Prueba la conexión a la base de datos.
    /// </summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand("SELECT DB_NAME(), @@SERVERNAME, @@VERSION", conn) { CommandTimeout = 10 };
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                var dbName = reader.GetString(0);
                var serverName = reader.GetString(1);
                return (true, $"Conectado a {serverName} / {dbName}");
            }

            return (true, "Conexión exitosa");
        }
        catch (Exception ex)
        {
            return (false, $"Error: {ex.Message}");
        }
    }

    private static DbObject MapFromReader(SqlDataReader reader)
    {
        return new DbObject
        {
            ObjectName = reader.GetString(reader.GetOrdinal("ObjectName")),
            SchemaName = reader.GetString(reader.GetOrdinal("SchemaName")),
            ObjectType = DbObjectTypeExtensions.FromSqlType(reader.GetString(reader.GetOrdinal("ObjectType"))),
            Definition = reader.IsDBNull(reader.GetOrdinal("Definition")) ? string.Empty : reader.GetString(reader.GetOrdinal("Definition")),
            LastModified = reader.GetDateTime(reader.GetOrdinal("LastModified"))
        };
    }
}
