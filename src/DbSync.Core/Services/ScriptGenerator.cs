using DbSync.Core.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace DbSync.Core.Services;

/// <summary>
/// Genera scripts SQL para sincronizar objetos entre ambientes.
/// </summary>
public class ScriptGenerator
{
    /// <summary>
    /// Genera el script para sincronizar un objeto del origen al destino.
    /// </summary>
    public string GenerateSyncScript(CompareResult compareResult)
    {
        return compareResult.Status switch
        {
            CompareStatus.OnlyInSource => GenerateCreateScript(compareResult.Source!),
            CompareStatus.Modified => GenerateAlterScript(compareResult.Source!, compareResult.Target!),
            CompareStatus.OnlyInTarget => GenerateDropScript(compareResult),
            _ => $"-- No se requiere acción para {compareResult.ObjectFullName}"
        };
    }

    /// <summary>
    /// Genera scripts para múltiples objetos.
    /// Nota: GO es un separador de batch y no puede estar dentro de TRY/CATCH.
    /// La transacción se maneja programáticamente en SyncExecutor.
    /// </summary>
    public string GenerateBatchSyncScript(IEnumerable<CompareResult> results, bool wrapInTransaction = true)
    {
        var sb = new StringBuilder();

        sb.AppendLine("-- =====================================================");
        sb.AppendLine($"-- Script de sincronización generado: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (wrapInTransaction)
        {
            sb.AppendLine("-- Ejecutado dentro de una transacción (ROLLBACK completo si hay error)");
        }
        sb.AppendLine("-- =====================================================");
        sb.AppendLine();

        foreach (var result in results)
        {
            sb.AppendLine($"-- [{result.Status}] {result.ObjectFullName} ({result.ObjectType.ToDisplayName()})");
            sb.AppendLine(GenerateSyncScript(result));
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Genera el script CREATE usando la definición del origen.
    /// El objeto no existe en destino, así que usamos CREATE directamente.
    /// </summary>
    private string GenerateCreateScript(DbObject source)
    {
        return EnsureVerb(source.Definition, "CREATE");
    }

    /// <summary>
    /// Genera el script ALTER para actualizar un objeto existente en destino.
    /// Convierte el CREATE del origen en ALTER.
    /// </summary>
    private string GenerateAlterScript(DbObject source, DbObject target)
    {
        return EnsureVerb(source.Definition, "ALTER");
    }

    /// <summary>
    /// Genera script DROP con validación de existencia.
    /// </summary>
    private string GenerateDropScript(CompareResult result)
    {
        var target = result.Target!;
        var dropKeyword = target.ObjectType switch
        {
            DbObjectType.StoredProcedure => "PROCEDURE",
            DbObjectType.View => "VIEW",
            _ => "FUNCTION"
        };

        return $@"IF OBJECT_ID('{target.FullName}', '{target.ObjectType.ToSqlType()}') IS NOT NULL
    DROP {dropKeyword} [{target.SchemaName}].[{target.ObjectName}];";
    }

    /// <summary>
    /// Convierte la definición para que comience con el verbo indicado (CREATE o ALTER).
    /// Funciona con SPs, Views y Functions.
    /// </summary>
    private static string EnsureVerb(string definition, string verb)
    {
        if (string.IsNullOrWhiteSpace(definition))
            return "-- Definición vacía";

        // Patrones: CREATE PROCEDURE, CREATE VIEW, CREATE FUNCTION, ALTER PROCEDURE, CREATE OR ALTER, etc.
        var pattern = @"(?i)^\s*(CREATE\s+OR\s+ALTER|ALTER|CREATE)\s+(PROCEDURE|PROC|VIEW|FUNCTION)";
        var match = Regex.Match(definition, pattern, RegexOptions.Multiline);

        if (!match.Success)
            return $"-- No se pudo parsear la definición. Revisar manualmente.\n-- {definition[..Math.Min(100, definition.Length)]}...";

        var keyword = match.Groups[2].Value.ToUpper();
        if (keyword == "PROC") keyword = "PROCEDURE";

        return Regex.Replace(definition, pattern, $"{verb} {keyword}", RegexOptions.Multiline);
    }

    /// <summary>
    /// Genera un script de backup (solo la definición actual del destino, como comentario o script separado).
    /// </summary>
    public string GenerateBackupScript(DbObject targetObject)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- =====================================================");
        sb.AppendLine($"-- BACKUP: {targetObject.FullName}");
        sb.AppendLine($"-- Tipo: {targetObject.ObjectType.ToDisplayName()}");
        sb.AppendLine($"-- Fecha backup: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"-- Última modificación: {targetObject.LastModified:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("-- =====================================================");
        sb.AppendLine();
        sb.AppendLine(targetObject.Definition);
        return sb.ToString();
    }
}
