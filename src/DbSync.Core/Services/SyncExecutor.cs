using Microsoft.Data.SqlClient;
using DbSync.Core.Models;
using DbSync.Core.Data;

namespace DbSync.Core.Services;

/// <summary>
/// Ejecuta la sincronización de objetos contra SQL Server.
/// </summary>
public class SyncExecutor
{
    private readonly ScriptGenerator _scriptGenerator;
    private readonly DbObjectExtractor _extractor;
    private readonly AppDbContext _dbContext;

    public SyncExecutor(ScriptGenerator scriptGenerator, DbObjectExtractor extractor, AppDbContext dbContext)
    {
        _scriptGenerator = scriptGenerator;
        _extractor = extractor;
        _dbContext = dbContext;
    }

    /// <summary>
    /// Ejecuta la sincronización de un objeto específico.
    /// Hace backup de la definición anterior antes de modificar.
    /// </summary>
    public async Task<SyncResult> ExecuteSingleAsync(
        CompareResult compareResult,
        string connectionStringDestino,
        int clienteId,
        Ambiente ambienteOrigen,
        Ambiente ambienteDestino,
        string? usuario = null,
        CancellationToken ct = default)
    {
        var result = new SyncResult { ObjectFullName = compareResult.ObjectFullName };

        try
        {
            // Generar script
            var script = _scriptGenerator.GenerateSyncScript(compareResult);

            // Backup de definición anterior si existe en destino
            string? definicionAnterior = null;
            if (compareResult.Target != null)
            {
                definicionAnterior = compareResult.Target.Definition;
            }

            // Ejecutar contra SQL Server
            await ExecuteScriptAsync(connectionStringDestino, script, ct);

            // Determinar acción
            var accion = compareResult.Status switch
            {
                CompareStatus.OnlyInSource => "CREATE",
                CompareStatus.Modified => "ALTER",
                CompareStatus.OnlyInTarget => "DROP",
                _ => "NONE"
            };

            // Registrar en historial
            var history = new SyncHistory
            {
                ClienteId = clienteId,
                AmbienteOrigen = ambienteOrigen,
                AmbienteDestino = ambienteDestino,
                NombreObjeto = compareResult.ObjectFullName,
                TipoObjeto = compareResult.ObjectType.ToShortCode(),
                AccionRealizada = accion,
                ScriptEjecutado = script,
                DefinicionAnterior = definicionAnterior,
                FechaEjecucion = DateTime.Now,
                Usuario = usuario,
                Exitoso = true
            };

            _dbContext.SyncHistory.Add(history);
            await _dbContext.SaveChangesAsync(ct);

            result.Success = true;
            result.Action = accion;
            result.Message = $"{accion} ejecutado exitosamente para {compareResult.ObjectFullName}";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Error sincronizando {compareResult.ObjectFullName}: {ex.Message}";
            result.Error = ex;

            // Registrar error en historial
            var history = new SyncHistory
            {
                ClienteId = clienteId,
                AmbienteOrigen = ambienteOrigen,
                AmbienteDestino = ambienteDestino,
                NombreObjeto = compareResult.ObjectFullName,
                TipoObjeto = compareResult.ObjectType.ToShortCode(),
                AccionRealizada = "ERROR",
                ScriptEjecutado = _scriptGenerator.GenerateSyncScript(compareResult),
                FechaEjecucion = DateTime.Now,
                Usuario = usuario,
                Exitoso = false,
                Error = ex.Message
            };

            _dbContext.SyncHistory.Add(history);
            await _dbContext.SaveChangesAsync(ct);
        }

        return result;
    }

    /// <summary>
    /// Ejecuta la sincronización de múltiples objetos.
    /// </summary>
    public async Task<List<SyncResult>> ExecuteBatchAsync(
        IEnumerable<CompareResult> compareResults,
        string connectionStringDestino,
        int clienteId,
        Ambiente ambienteOrigen,
        Ambiente ambienteDestino,
        string? usuario = null,
        CancellationToken ct = default)
    {
        var results = new List<SyncResult>();

        foreach (var compareResult in compareResults)
        {
            if (ct.IsCancellationRequested) break;

            var result = await ExecuteSingleAsync(
                compareResult, connectionStringDestino,
                clienteId, ambienteOrigen, ambienteDestino,
                usuario, ct);

            results.Add(result);

            // Si hay error, detenemos? Depende de la estrategia
            // Por ahora continuamos e informamos todos los resultados
        }

        return results;
    }

    /// <summary>
    /// Ejecuta la sincronización de múltiples objetos dentro de una transacción.
    /// Si alguno falla, hace ROLLBACK de todo.
    /// </summary>
    public async Task<List<SyncResult>> ExecuteBatchTransactionalAsync(
        IEnumerable<CompareResult> compareResults,
        string connectionStringDestino,
        int clienteId,
        Ambiente ambienteOrigen,
        Ambiente ambienteDestino,
        string? usuario = null,
        CancellationToken ct = default)
    {
        var items = compareResults.ToList();
        var results = new List<SyncResult>();
        var fechaEjecucion = DateTime.Now;

        await using var conn = new SqlConnection(connectionStringDestino);
        await conn.OpenAsync(ct);
        await using var tran = conn.BeginTransaction();

        try
        {
            foreach (var compareResult in items)
            {
                if (ct.IsCancellationRequested) break;

                var script = _scriptGenerator.GenerateSyncScript(compareResult);
                var accion = compareResult.Status switch
                {
                    CompareStatus.OnlyInSource => "CREATE",
                    CompareStatus.Modified => "ALTER",
                    CompareStatus.OnlyInTarget => "DROP",
                    _ => "NONE"
                };

                // Ejecutar dentro de la transacción
                await ExecuteScriptWithTransactionAsync(conn, tran, script, ct);

                results.Add(new SyncResult
                {
                    ObjectFullName = compareResult.ObjectFullName,
                    Success = true,
                    Action = accion,
                    Message = $"{accion} ejecutado exitosamente para {compareResult.ObjectFullName}"
                });
            }

            // Todo OK: COMMIT
            await tran.CommitAsync(ct);

            // Registrar todo en historial como exitoso
            foreach (var (compareResult, syncResult) in items.Zip(results))
            {
                _dbContext.SyncHistory.Add(new SyncHistory
                {
                    ClienteId = clienteId,
                    AmbienteOrigen = ambienteOrigen,
                    AmbienteDestino = ambienteDestino,
                    NombreObjeto = compareResult.ObjectFullName,
                    TipoObjeto = compareResult.ObjectType.ToShortCode(),
                    AccionRealizada = syncResult.Action,
                    ScriptEjecutado = _scriptGenerator.GenerateSyncScript(compareResult),
                    DefinicionAnterior = compareResult.Target?.Definition,
                    FechaEjecucion = fechaEjecucion,
                    Usuario = usuario,
                    Exitoso = true
                });
            }

            await _dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // ROLLBACK
            try { await tran.RollbackAsync(ct); } catch { /* rollback best-effort */ }

            // Marcar el objeto que falló
            var failedIndex = results.Count;
            var failedItem = failedIndex < items.Count ? items[failedIndex] : null;

            // Los que ya se ejecutaron se revirtieron por el rollback
            foreach (var r in results)
            {
                r.Success = false;
                r.Message = $"ROLLBACK - {r.Action} revertido por error en otro objeto";
                r.Action = "ROLLBACK";
            }

            // Agregar el que falló
            if (failedItem != null)
            {
                results.Add(new SyncResult
                {
                    ObjectFullName = failedItem.ObjectFullName,
                    Success = false,
                    Action = "ERROR",
                    Message = $"Error: {ex.Message}",
                    Error = ex
                });
            }

            // Registrar en historial
            var errorScript = failedItem != null ? _scriptGenerator.GenerateSyncScript(failedItem) : "";
            _dbContext.SyncHistory.Add(new SyncHistory
            {
                ClienteId = clienteId,
                AmbienteOrigen = ambienteOrigen,
                AmbienteDestino = ambienteDestino,
                NombreObjeto = failedItem?.ObjectFullName ?? "BATCH",
                TipoObjeto = failedItem?.ObjectType.ToShortCode() ?? "",
                AccionRealizada = "ERROR+ROLLBACK",
                ScriptEjecutado = errorScript,
                FechaEjecucion = fechaEjecucion,
                Usuario = usuario,
                Exitoso = false,
                Error = $"ROLLBACK completo. Error en {failedItem?.ObjectFullName}: {ex.Message}"
            });

            await _dbContext.SaveChangesAsync(ct);
        }

        return results;
    }

    /// <summary>
    /// Ejecuta un script SQL dentro de una conexión y transacción existentes.
    /// </summary>
    private async Task ExecuteScriptWithTransactionAsync(
        SqlConnection conn, SqlTransaction tran, string script, CancellationToken ct)
    {
        var batches = System.Text.RegularExpressions.Regex.Split(
            script,
            @"^\s*GO\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );

        foreach (var batch in batches)
        {
            var trimmed = batch.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (IsOnlyComments(trimmed)) continue;

            await using var cmd = new SqlCommand(trimmed, conn, tran);
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Ejecuta un script SQL contra SQL Server.
    /// Maneja la separación por GO.
    /// </summary>
    private async Task ExecuteScriptAsync(string connectionString, string script, CancellationToken ct)
    {
        // Separar por GO (que debe estar en su propia línea)
        var batches = System.Text.RegularExpressions.Regex.Split(
            script,
            @"^\s*GO\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        foreach (var batch in batches)
        {
            var trimmed = batch.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (IsOnlyComments(trimmed)) continue;

            await using var cmd = new SqlCommand(trimmed, conn);
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Verifica si un batch contiene solo comentarios (sin código SQL real).
    /// </summary>
    private static bool IsOnlyComments(string batch)
    {
        foreach (var line in batch.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0 && !t.StartsWith("--"))
                return false;
        }
        return true;
    }
}

public class SyncResult
{
    public string ObjectFullName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public Exception? Error { get; set; }
}
