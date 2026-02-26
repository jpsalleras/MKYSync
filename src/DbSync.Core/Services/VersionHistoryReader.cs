using Microsoft.Data.SqlClient;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using DbSync.Core.Models;

namespace DbSync.Core.Services;

/// <summary>
/// Lee la tabla ObjectChangeHistory directamente de la base de datos del cliente.
/// Soporta Stored Procedures, Views y Functions.
/// </summary>
public class VersionHistoryReader
{
    /// <summary>
    /// Verifica si la tabla ObjectChangeHistory existe en la base.
    /// </summary>
    public async Task<(bool Exists, string Message)> CheckVersionControlDbAsync(
        string connectionString, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            const string query = @"
                SELECT CASE WHEN OBJECT_ID('dbo.ObjectChangeHistory', 'U') IS NOT NULL
                       THEN 1 ELSE 0 END";

            await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 10 };
            var result = (int)(await cmd.ExecuteScalarAsync(ct))!;

            return result == 1
                ? (true, "Tabla ObjectChangeHistory encontrada")
                : (false, "La tabla ObjectChangeHistory no existe en esta base");
        }
        catch (Exception ex)
        {
            return (false, $"No se pudo conectar: {ex.Message}");
        }
    }

    /// <summary>
    /// Obtiene estadísticas generales: lista de objetos con cantidad de versiones.
    /// </summary>
    public async Task<List<ObjectVersionSummary>> GetStatsAsync(
        string connectionString, string? databaseFilter = null, CancellationToken ct = default)
    {
        var results = new List<ObjectVersionSummary>();

        const string query = @"
            SELECT
                DatabaseName,
                SchemaName,
                ObjectName,
                ObjectType,
                COUNT(*) AS TotalVersions,
                MAX(VersionNumber) AS CurrentVersion,
                MIN(ModifiedDate) AS FirstModified,
                MAX(ModifiedDate) AS LastModified,
                COUNT(DISTINCT ModifiedBy) AS TotalContributors
            FROM dbo.ObjectChangeHistory
            WHERE (@DatabaseFilter IS NULL OR DatabaseName = @DatabaseFilter)
            GROUP BY DatabaseName, SchemaName, ObjectName, ObjectType
            ORDER BY MAX(ModifiedDate) DESC";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@DatabaseFilter", (object?)databaseFilter ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ObjectVersionSummary
            {
                DatabaseName = reader.GetString(0),
                SchemaName = reader.GetString(1),
                ObjectName = reader.GetString(2),
                ObjectType = reader.GetString(3),
                TotalVersions = reader.GetInt32(4),
                CurrentVersion = reader.GetInt32(5),
                FirstModified = reader.GetDateTime(6),
                LastModified = reader.GetDateTime(7),
                TotalContributors = reader.GetInt32(8)
            });
        }

        return results;
    }

    /// <summary>
    /// Obtiene el historial de versiones de un objeto específico.
    /// </summary>
    public async Task<List<ObjectVersionEntry>> GetHistoryAsync(
        string connectionString,
        string? databaseName = null,
        string? schemaName = null,
        string? objectName = null,
        string? objectType = null,
        int? maxResults = null,
        CancellationToken ct = default)
    {
        var results = new List<ObjectVersionEntry>();

        const string query = @"
            SELECT
                HistoryID,
                DatabaseName,
                SchemaName,
                ObjectName,
                ObjectType,
                EventType,
                ObjectDefinition,
                ModifiedBy,
                ModifiedDate,
                HostName,
                ApplicationName,
                VersionNumber,
                LEN(ObjectDefinition) AS DefinitionLength
            FROM dbo.ObjectChangeHistory
            WHERE (@DatabaseName IS NULL OR DatabaseName = @DatabaseName)
              AND (@SchemaName IS NULL OR SchemaName = @SchemaName)
              AND (@ObjectName IS NULL OR ObjectName = @ObjectName)
              AND (@ObjectType IS NULL OR ObjectType = @ObjectType)
            ORDER BY ModifiedDate DESC";

        var finalQuery = maxResults.HasValue
            ? query.Replace("SELECT", $"SELECT TOP ({maxResults.Value})")
            : query;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(finalQuery, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@DatabaseName", (object?)databaseName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SchemaName", (object?)schemaName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ObjectName", (object?)objectName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ObjectType", (object?)objectType ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapVersionEntry(reader));
        }

        return results;
    }

    /// <summary>
    /// Obtiene una versión específica por su HistoryID.
    /// </summary>
    public async Task<ObjectVersionEntry?> GetVersionAsync(
        string connectionString, int historyId, CancellationToken ct = default)
    {
        const string query = @"
            SELECT
                HistoryID, DatabaseName, SchemaName, ObjectName, ObjectType,
                EventType, ObjectDefinition, ModifiedBy, ModifiedDate,
                HostName, ApplicationName, VersionNumber,
                LEN(ObjectDefinition) AS DefinitionLength
            FROM dbo.ObjectChangeHistory
            WHERE HistoryID = @HistoryID";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@HistoryID", historyId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapVersionEntry(reader) : null;
    }

    /// <summary>
    /// Compara dos versiones por HistoryID y genera el diff HTML.
    /// </summary>
    public async Task<VersionCompareResult?> CompareVersionsAsync(
        string connectionString, int historyId1, int historyId2, CancellationToken ct = default)
    {
        var v1Task = GetVersionAsync(connectionString, historyId1, ct);
        var v2Task = GetVersionAsync(connectionString, historyId2, ct);
        await Task.WhenAll(v1Task, v2Task);

        var v1 = v1Task.Result;
        var v2 = v2Task.Result;

        if (v1 == null || v2 == null) return null;

        var def1 = DbObject.NormalizeDefinition(v1.ObjectDefinition ?? "");
        var def2 = DbObject.NormalizeDefinition(v2.ObjectDefinition ?? "");

        if (def1 == def2)
        {
            return new VersionCompareResult
            {
                Version1 = v1,
                Version2 = v2,
                AreEqual = true,
                DiffHtml = "<p class='text-success p-3'>Las versiones son idénticas.</p>"
            };
        }

        var (html, added, removed) = GenerateDiff(def1, def2, v1.ShortDescription, v2.ShortDescription);

        return new VersionCompareResult
        {
            Version1 = v1,
            Version2 = v2,
            AreEqual = false,
            DiffHtml = html,
            LinesAdded = added,
            LinesRemoved = removed
        };
    }

    /// <summary>
    /// Compara una versión histórica contra la definición actual en un ambiente.
    /// </summary>
    public async Task<VersionCompareResult?> CompareVersionVsCurrentAsync(
        string connectionString, int historyId,
        string targetAmbienteCs, CancellationToken ct = default)
    {
        var version = await GetVersionAsync(connectionString, historyId, ct);
        if (version == null) return null;

        // Obtener definición actual del ambiente destino
        var extractor = new DbObjectExtractor();
        var current = await extractor.ExtractSingleAsync(
            targetAmbienteCs, version.SchemaName, version.ObjectName, ct);

        var defVersion = DbObject.NormalizeDefinition(version.ObjectDefinition ?? "");
        var defCurrent = DbObject.NormalizeDefinition(current?.Definition ?? "");

        var currentAsVersion = new ObjectVersionEntry
        {
            HistoryID = -1,
            DatabaseName = version.DatabaseName,
            SchemaName = version.SchemaName,
            ObjectName = version.ObjectName,
            ObjectType = version.ObjectType,
            EventType = "CURRENT",
            ObjectDefinition = current?.Definition,
            ModifiedBy = "Actual en ambiente",
            ModifiedDate = current?.LastModified ?? DateTime.Now,
            VersionNumber = 0
        };

        if (defVersion == defCurrent)
        {
            return new VersionCompareResult
            {
                Version1 = version,
                Version2 = currentAsVersion,
                AreEqual = true,
                DiffHtml = "<p class='text-success p-3'>La versión histórica coincide con la actual.</p>"
            };
        }

        var (html, added, removed) = GenerateDiff(defVersion, defCurrent,
            version.ShortDescription, $"Actual en ambiente ({current?.LastModified:dd/MM HH:mm})");

        return new VersionCompareResult
        {
            Version1 = version,
            Version2 = currentAsVersion,
            AreEqual = false,
            DiffHtml = html,
            LinesAdded = added,
            LinesRemoved = removed
        };
    }

    /// <summary>
    /// Obtiene las últimas N modificaciones (cualquier objeto) para un timeline.
    /// </summary>
    public async Task<List<ObjectVersionEntry>> GetRecentActivityAsync(
        string connectionString, int top = 50, string? databaseFilter = null, CancellationToken ct = default)
    {
        var results = new List<ObjectVersionEntry>();

        var query = $@"
            SELECT TOP ({top})
                HistoryID, DatabaseName, SchemaName, ObjectName, ObjectType,
                EventType, NULL AS ObjectDefinition, ModifiedBy, ModifiedDate,
                HostName, ApplicationName, VersionNumber,
                0 AS DefinitionLength
            FROM dbo.ObjectChangeHistory
            WHERE (@DatabaseFilter IS NULL OR DatabaseName = @DatabaseFilter)
            ORDER BY ModifiedDate DESC";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(query, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@DatabaseFilter", (object?)databaseFilter ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapVersionEntry(reader));
        }

        return results;
    }

    // ---- Helpers ----

    private static ObjectVersionEntry MapVersionEntry(SqlDataReader reader)
    {
        return new ObjectVersionEntry
        {
            HistoryID = reader.GetInt32(reader.GetOrdinal("HistoryID")),
            DatabaseName = reader.GetString(reader.GetOrdinal("DatabaseName")),
            SchemaName = reader.GetString(reader.GetOrdinal("SchemaName")),
            ObjectName = reader.GetString(reader.GetOrdinal("ObjectName")),
            ObjectType = reader.GetString(reader.GetOrdinal("ObjectType")),
            EventType = reader.GetString(reader.GetOrdinal("EventType")),
            ObjectDefinition = reader.IsDBNull(reader.GetOrdinal("ObjectDefinition"))
                ? null
                : reader.GetString(reader.GetOrdinal("ObjectDefinition")),
            ModifiedBy = reader.GetString(reader.GetOrdinal("ModifiedBy")),
            ModifiedDate = reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
            HostName = reader.IsDBNull(reader.GetOrdinal("HostName")) ? null : reader.GetString(reader.GetOrdinal("HostName")),
            ApplicationName = reader.IsDBNull(reader.GetOrdinal("ApplicationName")) ? null : reader.GetString(reader.GetOrdinal("ApplicationName")),
            VersionNumber = reader.GetInt32(reader.GetOrdinal("VersionNumber")),
            DefinitionLength = reader.IsDBNull(reader.GetOrdinal("DefinitionLength")) ? null : (int)reader.GetInt64(reader.GetOrdinal("DefinitionLength"))
        };
    }

    private static (string Html, int Added, int Removed) GenerateDiff(
        string text1, string text2, string label1, string label2)
    {
        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        var diff = diffBuilder.BuildDiffModel(text1, text2, ignoreWhitespace: false);

        int added = 0, removed = 0;
        var html = new System.Text.StringBuilder();

        html.AppendLine("<table class='diff-table'>");
        html.AppendLine("<colgroup><col style='width:35px'><col style='width:calc(50% - 45px)'>");
        html.AppendLine("<col style='width:35px'><col style='width:calc(50% - 45px)'></colgroup>");
        html.AppendLine($"<thead><tr><th>#</th><th>{System.Net.WebUtility.HtmlEncode(label1)}</th>");
        html.AppendLine($"<th>#</th><th>{System.Net.WebUtility.HtmlEncode(label2)}</th></tr></thead>");
        html.AppendLine("<tbody>");

        var maxLines = Math.Max(diff.OldText.Lines.Count, diff.NewText.Lines.Count);
        for (int i = 0; i < maxLines; i++)
        {
            var oldLine = i < diff.OldText.Lines.Count ? diff.OldText.Lines[i] : null;
            var newLine = i < diff.NewText.Lines.Count ? diff.NewText.Lines[i] : null;

            var oldClass = oldLine?.Type switch
            {
                ChangeType.Deleted => "diff-deleted",
                ChangeType.Modified => "diff-modified",
                ChangeType.Imaginary => "diff-imaginary",
                _ => ""
            };
            var newClass = newLine?.Type switch
            {
                ChangeType.Inserted => "diff-inserted",
                ChangeType.Modified => "diff-modified",
                ChangeType.Imaginary => "diff-imaginary",
                _ => ""
            };

            if (oldLine?.Type is ChangeType.Deleted or ChangeType.Modified) removed++;
            if (newLine?.Type is ChangeType.Inserted or ChangeType.Modified) added++;

            var oldNum = oldLine?.Type != ChangeType.Imaginary ? oldLine?.Position?.ToString() ?? "" : "";
            var newNum = newLine?.Type != ChangeType.Imaginary ? newLine?.Position?.ToString() ?? "" : "";

            html.AppendLine("<tr>");
            html.AppendLine($"  <td class='line-num'>{oldNum}</td>");
            html.AppendLine($"  <td class='diff-content {oldClass}'><pre>{System.Net.WebUtility.HtmlEncode(oldLine?.Text ?? "")}</pre></td>");
            html.AppendLine($"  <td class='line-num'>{newNum}</td>");
            html.AppendLine($"  <td class='diff-content {newClass}'><pre>{System.Net.WebUtility.HtmlEncode(newLine?.Text ?? "")}</pre></td>");
            html.AppendLine("</tr>");
        }

        html.AppendLine("</tbody></table>");
        return (html.ToString(), added, removed);
    }
}
