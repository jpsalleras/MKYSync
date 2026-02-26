using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using DbSync.Core.Models;

namespace DbSync.Core.Services;

/// <summary>
/// Compara los objetos de dos ambientes y genera diffs.
/// </summary>
public class DbComparer
{
    private readonly DbObjectExtractor _extractor;

    public DbComparer(DbObjectExtractor extractor)
    {
        _extractor = extractor;
    }

    /// <summary>
    /// Compara todos los objetos entre dos ambientes de un cliente.
    /// </summary>
    public async Task<ComparisonSummary> CompareAsync(
        Cliente cliente,
        Ambiente ambienteOrigen,
        Ambiente ambienteDestino,
        Func<string, string>? decryptPassword = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var csOrigen = cliente.Ambientes
            .First(a => a.Ambiente == ambienteOrigen)
            .GetConnectionString(decryptPassword);

        var csDestino = cliente.Ambientes
            .First(a => a.Ambiente == ambienteDestino)
            .GetConnectionString(decryptPassword);

        // Extraer objetos de ambos ambientes en paralelo
        var sourceTask = _extractor.ExtractAllAsync(csOrigen, ct);
        var targetTask = _extractor.ExtractAllAsync(csDestino, ct);
        await Task.WhenAll(sourceTask, targetTask);

        var sourceObjects = sourceTask.Result.ToDictionary(o => o.FullName, StringComparer.OrdinalIgnoreCase);
        var targetObjects = targetTask.Result.ToDictionary(o => o.FullName, StringComparer.OrdinalIgnoreCase);

        // Set de objetos custom del cliente
        var customObjects = cliente.ObjetosCustom
            .Select(c => c.NombreObjeto)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<CompareResult>();

        // Objetos en origen
        foreach (var (fullName, sourceObj) in sourceObjects)
        {
            var isCustom = customObjects.Contains(fullName) || IsCustomByConvention(fullName, cliente.Codigo);

            if (targetObjects.TryGetValue(fullName, out var targetObj))
            {
                // Existe en ambos: comparar
                if (sourceObj.DefinitionHash == targetObj.DefinitionHash)
                {
                    results.Add(new CompareResult
                    {
                        ObjectFullName = fullName,
                        ObjectType = sourceObj.ObjectType,
                        Status = CompareStatus.Equal,
                        IsCustom = isCustom,
                        Source = sourceObj,
                        Target = targetObj
                    });
                }
                else
                {
                    var (diffHtml, added, removed) = GenerateDiff(sourceObj.NormalizedDefinition, targetObj.NormalizedDefinition);
                    results.Add(new CompareResult
                    {
                        ObjectFullName = fullName,
                        ObjectType = sourceObj.ObjectType,
                        Status = CompareStatus.Modified,
                        IsCustom = isCustom,
                        Source = sourceObj,
                        Target = targetObj,
                        DiffHtml = diffHtml,
                        LinesAdded = added,
                        LinesRemoved = removed
                    });
                }
            }
            else
            {
                // Solo en origen
                results.Add(new CompareResult
                {
                    ObjectFullName = fullName,
                    ObjectType = sourceObj.ObjectType,
                    Status = CompareStatus.OnlyInSource,
                    IsCustom = isCustom,
                    Source = sourceObj
                });
            }
        }

        // Objetos solo en destino
        foreach (var (fullName, targetObj) in targetObjects)
        {
            if (!sourceObjects.ContainsKey(fullName))
            {
                var isCustom = customObjects.Contains(fullName) || IsCustomByConvention(fullName, cliente.Codigo);
                results.Add(new CompareResult
                {
                    ObjectFullName = fullName,
                    ObjectType = targetObj.ObjectType,
                    Status = CompareStatus.OnlyInTarget,
                    IsCustom = isCustom,
                    Target = targetObj
                });
            }
        }

        sw.Stop();

        return new ComparisonSummary
        {
            ClienteId = cliente.Id,
            ClienteNombre = cliente.Nombre,
            AmbienteOrigen = ambienteOrigen,
            AmbienteDestino = ambienteDestino,
            FechaComparacion = DateTime.Now,
            Duracion = sw.Elapsed,
            Results = results.OrderBy(r => r.Status).ThenBy(r => r.ObjectFullName).ToList()
        };
    }

    /// <summary>
    /// Compara un objeto específico entre dos ambientes.
    /// </summary>
    public async Task<CompareResult?> CompareSingleAsync(
        string connectionStringOrigen,
        string connectionStringDestino,
        string schemaName,
        string objectName,
        CancellationToken ct = default)
    {
        var sourceTask = _extractor.ExtractSingleAsync(connectionStringOrigen, schemaName, objectName, ct);
        var targetTask = _extractor.ExtractSingleAsync(connectionStringDestino, schemaName, objectName, ct);
        await Task.WhenAll(sourceTask, targetTask);

        var source = sourceTask.Result;
        var target = targetTask.Result;

        if (source == null && target == null) return null;

        var fullName = $"{schemaName}.{objectName}";

        if (source != null && target == null)
        {
            return new CompareResult
            {
                ObjectFullName = fullName,
                ObjectType = source.ObjectType,
                Status = CompareStatus.OnlyInSource,
                Source = source
            };
        }

        if (source == null && target != null)
        {
            return new CompareResult
            {
                ObjectFullName = fullName,
                ObjectType = target.ObjectType,
                Status = CompareStatus.OnlyInTarget,
                Target = target
            };
        }

        if (source!.DefinitionHash == target!.DefinitionHash)
        {
            return new CompareResult
            {
                ObjectFullName = fullName,
                ObjectType = source.ObjectType,
                Status = CompareStatus.Equal,
                Source = source,
                Target = target
            };
        }

        var (diffHtml, added, removed) = GenerateDiff(source.NormalizedDefinition, target.NormalizedDefinition);
        return new CompareResult
        {
            ObjectFullName = fullName,
            ObjectType = source.ObjectType,
            Status = CompareStatus.Modified,
            Source = source,
            Target = target,
            DiffHtml = diffHtml,
            LinesAdded = added,
            LinesRemoved = removed
        };
    }

    /// <summary>
    /// Compara dos diccionarios de objetos pre-cargados (sin extracción live).
    /// Usado para comparar versión base vs ambiente live.
    /// </summary>
    public ComparisonSummary CompareFromDictionaries(
        Dictionary<string, DbObject> sourceObjects,
        Dictionary<string, DbObject> targetObjects,
        HashSet<string> customObjects,
        string? codigoCliente = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<CompareResult>();

        foreach (var (fullName, sourceObj) in sourceObjects)
        {
            var isCustom = customObjects.Contains(fullName)
                || IsCustomByConvention(fullName, codigoCliente);

            if (targetObjects.TryGetValue(fullName, out var targetObj))
            {
                if (sourceObj.DefinitionHash == targetObj.DefinitionHash)
                {
                    results.Add(new CompareResult
                    {
                        ObjectFullName = fullName,
                        ObjectType = sourceObj.ObjectType,
                        Status = CompareStatus.Equal,
                        IsCustom = isCustom,
                        Source = sourceObj,
                        Target = targetObj
                    });
                }
                else
                {
                    var (diffHtml, added, removed) = GenerateDiff(
                        sourceObj.NormalizedDefinition, targetObj.NormalizedDefinition);
                    results.Add(new CompareResult
                    {
                        ObjectFullName = fullName,
                        ObjectType = sourceObj.ObjectType,
                        Status = CompareStatus.Modified,
                        IsCustom = isCustom,
                        Source = sourceObj,
                        Target = targetObj,
                        DiffHtml = diffHtml,
                        LinesAdded = added,
                        LinesRemoved = removed
                    });
                }
            }
            else
            {
                results.Add(new CompareResult
                {
                    ObjectFullName = fullName,
                    ObjectType = sourceObj.ObjectType,
                    Status = CompareStatus.OnlyInSource,
                    IsCustom = isCustom,
                    Source = sourceObj
                });
            }
        }

        foreach (var (fullName, targetObj) in targetObjects)
        {
            if (!sourceObjects.ContainsKey(fullName))
            {
                var isCustom = customObjects.Contains(fullName)
                    || IsCustomByConvention(fullName, codigoCliente);
                results.Add(new CompareResult
                {
                    ObjectFullName = fullName,
                    ObjectType = targetObj.ObjectType,
                    Status = CompareStatus.OnlyInTarget,
                    IsCustom = isCustom,
                    Target = targetObj
                });
            }
        }

        sw.Stop();

        return new ComparisonSummary
        {
            Results = results.OrderBy(r => r.Status).ThenBy(r => r.ObjectFullName).ToList(),
            Duracion = sw.Elapsed,
            FechaComparacion = DateTime.Now
        };
    }

    /// <summary>
    /// Genera el diff HTML entre dos definiciones usando DiffPlex.
    /// </summary>
    public static (string Html, int Added, int Removed) GenerateDiff(string sourceText, string targetText)
    {
        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        var diff = diffBuilder.BuildDiffModel(sourceText, targetText, ignoreWhitespace: false);

        int added = 0, removed = 0;

        var html = new System.Text.StringBuilder();
        html.AppendLine("<table class='diff-table'>");
        html.AppendLine("<colgroup><col style='width:35px'><col style='width:calc(50% - 35px)'>");
        html.AppendLine("<col style='width:35px'><col style='width:calc(50% - 35px)'></colgroup>");
        html.AppendLine("<thead><tr><th>#</th><th>Origen</th><th>#</th><th>Destino</th></tr></thead>");
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

            if (oldLine?.Type == ChangeType.Deleted) removed++;
            if (oldLine?.Type == ChangeType.Modified) removed++;
            if (newLine?.Type == ChangeType.Inserted) added++;
            if (newLine?.Type == ChangeType.Modified) added++;

            var oldLineNum = oldLine?.Type != ChangeType.Imaginary ? oldLine?.Position?.ToString() ?? "" : "";
            var newLineNum = newLine?.Type != ChangeType.Imaginary ? newLine?.Position?.ToString() ?? "" : "";

            html.AppendLine($"<tr>");
            html.AppendLine($"  <td class='line-num'>{oldLineNum}</td>");
            html.AppendLine($"  <td class='diff-content {oldClass}'><pre>{System.Net.WebUtility.HtmlEncode(oldLine?.Text ?? "")}</pre></td>");
            html.AppendLine($"  <td class='line-num'>{newLineNum}</td>");
            html.AppendLine($"  <td class='diff-content {newClass}'><pre>{System.Net.WebUtility.HtmlEncode(newLine?.Text ?? "")}</pre></td>");
            html.AppendLine($"</tr>");
        }

        html.AppendLine("</tbody></table>");

        return (html.ToString(), added, removed);
    }

    /// <summary>
    /// Detecta si un objeto es custom basándose en convención de nombres.
    /// </summary>
    public static bool IsCustomByConvention(string fullName, string? codigoCliente)
    {
        if (string.IsNullOrEmpty(codigoCliente)) return false;

        // Convenciones: el nombre contiene el código del cliente
        // Ejemplos: dbo.usp_HOSP_ReporteCustom, dbo.vw_CLI01_VentasEspecial
        var name = fullName.Contains('.') ? fullName.Split('.')[1] : fullName;
        return name.Contains(codigoCliente, StringComparison.OrdinalIgnoreCase);
    }
}
