using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using DbSync.Core.Data;
using DbSync.Core.Models;

namespace DbSync.Core.Services;

/// <summary>
/// Compara objetos entre diferentes clientes/ambientes usando datos del repositorio central.
/// No requiere conexión live a SQL Server — trabaja con snapshots almacenados.
/// </summary>
public class CrossClientComparer
{
    private readonly CentralRepository _centralRepo;

    public CrossClientComparer(CentralRepository centralRepo)
    {
        _centralRepo = centralRepo;
    }

    /// <summary>
    /// Compara todos los objetos entre dos clientes/ambientes usando los últimos snapshots.
    /// </summary>
    public async Task<CrossComparisonResult> CompareAsync(
        int clienteIdA, Ambiente ambienteA,
        int clienteIdB, Ambiente ambienteB,
        string? filterObjectType = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Obtener últimos snapshots de ambos lados en paralelo
        var taskA = _centralRepo.GetLatestSnapshotsAsync(clienteIdA, ambienteA.ToString(), ct);
        var taskB = _centralRepo.GetLatestSnapshotsAsync(clienteIdB, ambienteB.ToString(), ct);
        await Task.WhenAll(taskA, taskB);

        var snapshotsA = taskA.Result;
        var snapshotsB = taskB.Result;

        var dictA = snapshotsA.ToDictionary(s => s.ObjectFullName, StringComparer.OrdinalIgnoreCase);
        var dictB = snapshotsB.ToDictionary(s => s.ObjectFullName, StringComparer.OrdinalIgnoreCase);

        var results = new List<CrossCompareItem>();

        // Objetos en A
        foreach (var (fullName, snapA) in dictA)
        {
            if (!string.IsNullOrEmpty(filterObjectType) && snapA.ObjectType != filterObjectType)
                continue;

            if (dictB.TryGetValue(fullName, out var snapB))
            {
                if (snapA.DefinitionHash == snapB.DefinitionHash)
                {
                    results.Add(new CrossCompareItem
                    {
                        ObjectFullName = fullName,
                        ObjectType = snapA.ObjectType,
                        Status = CompareStatus.Equal,
                        IsCustomA = snapA.IsCustom,
                        IsCustomB = snapB.IsCustom,
                        SnapshotIdA = snapA.Id,
                        SnapshotIdB = snapB.Id
                    });
                }
                else
                {
                    results.Add(new CrossCompareItem
                    {
                        ObjectFullName = fullName,
                        ObjectType = snapA.ObjectType,
                        Status = CompareStatus.Modified,
                        IsCustomA = snapA.IsCustom,
                        IsCustomB = snapB.IsCustom,
                        SnapshotIdA = snapA.Id,
                        SnapshotIdB = snapB.Id
                    });
                }
            }
            else
            {
                results.Add(new CrossCompareItem
                {
                    ObjectFullName = fullName,
                    ObjectType = snapA.ObjectType,
                    Status = CompareStatus.OnlyInSource,
                    IsCustomA = snapA.IsCustom,
                    SnapshotIdA = snapA.Id
                });
            }
        }

        // Objetos solo en B
        foreach (var (fullName, snapB) in dictB)
        {
            if (!string.IsNullOrEmpty(filterObjectType) && snapB.ObjectType != filterObjectType)
                continue;

            if (!dictA.ContainsKey(fullName))
            {
                results.Add(new CrossCompareItem
                {
                    ObjectFullName = fullName,
                    ObjectType = snapB.ObjectType,
                    Status = CompareStatus.OnlyInTarget,
                    IsCustomB = snapB.IsCustom,
                    SnapshotIdB = snapB.Id
                });
            }
        }

        sw.Stop();

        return new CrossComparisonResult
        {
            ClienteCodigoA = snapshotsA.FirstOrDefault()?.ClienteCodigo ?? "",
            ClienteNombreA = snapshotsA.FirstOrDefault()?.ClienteNombre ?? "",
            AmbienteA = ambienteA,
            ClienteCodigoB = snapshotsB.FirstOrDefault()?.ClienteCodigo ?? "",
            ClienteNombreB = snapshotsB.FirstOrDefault()?.ClienteNombre ?? "",
            AmbienteB = ambienteB,
            TotalSnapshotsA = snapshotsA.Count,
            TotalSnapshotsB = snapshotsB.Count,
            Duration = sw.Elapsed,
            Items = results.OrderBy(r => r.Status).ThenBy(r => r.ObjectFullName).ToList()
        };
    }

    /// <summary>
    /// Genera el diff HTML entre dos snapshots específicos, cargando las definiciones bajo demanda.
    /// </summary>
    public async Task<CrossCompareDiffResult?> GetDiffAsync(
        long snapshotIdA, long snapshotIdB, CancellationToken ct = default)
    {
        var defATask = _centralRepo.GetSnapshotDefinitionAsync(snapshotIdA, ct);
        var defBTask = _centralRepo.GetSnapshotDefinitionAsync(snapshotIdB, ct);
        await Task.WhenAll(defATask, defBTask);

        var defA = defATask.Result;
        var defB = defBTask.Result;

        if (defA == null && defB == null) return null;

        var normalizedA = defA != null ? DbObject.NormalizeDefinition(defA) : "";
        var normalizedB = defB != null ? DbObject.NormalizeDefinition(defB) : "";

        var (html, added, removed) = GenerateDiff(normalizedA, normalizedB);

        return new CrossCompareDiffResult
        {
            DiffHtml = html,
            LinesAdded = added,
            LinesRemoved = removed
        };
    }

    private static (string Html, int Added, int Removed) GenerateDiff(string sourceText, string targetText)
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
}

/// <summary>
/// Resultado de una comparación cruzada entre clientes.
/// </summary>
public class CrossComparisonResult
{
    public string ClienteCodigoA { get; set; } = string.Empty;
    public string ClienteNombreA { get; set; } = string.Empty;
    public Ambiente AmbienteA { get; set; }
    public string ClienteCodigoB { get; set; } = string.Empty;
    public string ClienteNombreB { get; set; } = string.Empty;
    public Ambiente AmbienteB { get; set; }
    public int TotalSnapshotsA { get; set; }
    public int TotalSnapshotsB { get; set; }
    public TimeSpan Duration { get; set; }
    public List<CrossCompareItem> Items { get; set; } = new();

    public int TotalEqual => Items.Count(i => i.Status == CompareStatus.Equal);
    public int TotalModified => Items.Count(i => i.Status == CompareStatus.Modified);
    public int TotalOnlyInA => Items.Count(i => i.Status == CompareStatus.OnlyInSource);
    public int TotalOnlyInB => Items.Count(i => i.Status == CompareStatus.OnlyInTarget);
    public int TotalObjects => Items.Count;
    public bool HasDifferences => TotalModified > 0 || TotalOnlyInA > 0 || TotalOnlyInB > 0;
}

/// <summary>
/// Un ítem de comparación cruzada.
/// </summary>
public class CrossCompareItem
{
    public string ObjectFullName { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public CompareStatus Status { get; set; }
    public bool IsCustomA { get; set; }
    public bool IsCustomB { get; set; }
    public long? SnapshotIdA { get; set; }
    public long? SnapshotIdB { get; set; }

    /// <summary>Diff HTML cargado bajo demanda.</summary>
    public string? DiffHtml { get; set; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
}

/// <summary>
/// Resultado de un diff entre dos snapshots.
/// </summary>
public class CrossCompareDiffResult
{
    public string DiffHtml { get; set; } = string.Empty;
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
}
