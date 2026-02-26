using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DbSync.Core.Data;
using DbSync.Core.Models;

namespace DbSync.Core.Services;

/// <summary>
/// Orquesta el escaneo de todos los clientes activos, almacena snapshots
/// en el repositorio central y detecta cambios.
/// </summary>
public class SnapshotScanner
{
    private readonly AppDbContext _localDb;
    private readonly CentralRepository _centralRepo;
    private readonly DbObjectExtractor _extractor;
    private readonly NotificationService? _notificationService;
    private readonly CredentialEncryptor? _encryptor;
    private readonly ILogger<SnapshotScanner> _logger;

    public SnapshotScanner(
        AppDbContext localDb,
        CentralRepository centralRepo,
        DbObjectExtractor extractor,
        ILogger<SnapshotScanner> logger,
        NotificationService? notificationService = null,
        CredentialEncryptor? encryptor = null)
    {
        _localDb = localDb;
        _centralRepo = centralRepo;
        _extractor = extractor;
        _logger = logger;
        _notificationService = notificationService;
        _encryptor = encryptor;
    }

    /// <summary>
    /// Ejecuta un scan completo de todos los clientes activos y sus ambientes.
    /// Por defecto solo escanea objetos base (definidos en ObjetosBase).
    /// </summary>
    public async Task<ScanLog> RunFullScanAsync(
        ScanTrigger trigger = ScanTrigger.Scheduled,
        string? triggeredBy = null,
        int maxParallelClients = 5,
        bool scanAll = false,
        CancellationToken ct = default)
    {
        var scanLog = new ScanLog
        {
            StartedAt = DateTime.UtcNow,
            Status = ScanStatus.Running,
            Trigger = trigger,
            TriggeredBy = triggeredBy
        };

        try
        {
            // Cargar clientes activos con ambientes y custom objects
            var clientes = await _localDb.Clientes
                .Include(c => c.Ambientes)
                .Include(c => c.ObjetosCustom)
                .Where(c => c.Activo)
                .ToListAsync(ct);

            // Cargar objetos base para filtrado (si la tabla está vacía, escanear todo)
            List<ObjetoBase>? allBaseObjects = null;
            if (!scanAll)
            {
                var baseList = await _localDb.ObjetosBase.ToListAsync(ct);
                if (baseList.Count > 0)
                    allBaseObjects = baseList;
            }

            scanLog.TotalClientes = clientes.Count;
            scanLog.TotalAmbientes = clientes.Sum(c => c.Ambientes.Count);

            await _centralRepo.CreateScanLogAsync(scanLog, ct);
            _logger.LogInformation(
                "Scan {ScanId} iniciado: {Clientes} clientes, {Ambientes} ambientes, filtro objetos base: {FiltroBase}",
                scanLog.Id, scanLog.TotalClientes, scanLog.TotalAmbientes,
                allBaseObjects != null ? $"{allBaseObjects.Count} objetos" : "desactivado (scan completo)");

            int totalObjects = 0;
            int totalChanges = 0;
            int totalErrors = 0;
            var errorMessages = new List<string>();

            // Procesar clientes en paralelo con límite
            using var semaphore = new SemaphoreSlim(maxParallelClients);
            var tasks = clientes.Select(async cliente =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    // Filtro por cliente: global (ClienteId=null) + específicos del cliente
                    var objetosBaseCliente = BuildBaseFilterForCliente(allBaseObjects, cliente.Id);

                    foreach (var ambiente in cliente.Ambientes)
                    {
                        ct.ThrowIfCancellationRequested();

                        var (objects, changes, error) = await ScanClienteAmbienteAsync(
                            scanLog.Id, cliente, ambiente, objetosBaseCliente, ct);

                        Interlocked.Add(ref totalObjects, objects);
                        Interlocked.Add(ref totalChanges, changes);
                        if (error != null)
                        {
                            Interlocked.Increment(ref totalErrors);
                            lock (errorMessages) { errorMessages.Add(error); }
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            // Actualizar ScanLog
            scanLog.CompletedAt = DateTime.UtcNow;
            scanLog.TotalObjectsScanned = totalObjects;
            scanLog.TotalChangesDetected = totalChanges;
            scanLog.TotalErrors = totalErrors;
            scanLog.Status = totalErrors == 0
                ? ScanStatus.Completed
                : ScanStatus.CompletedWithErrors;

            if (errorMessages.Count > 0)
            {
                scanLog.ErrorSummary = string.Join("\n", errorMessages.Take(20));
            }

            await _centralRepo.UpdateScanLogAsync(scanLog, ct);

            _logger.LogInformation(
                "Scan {ScanId} completado: {Objects} objetos, {Changes} cambios, {Errors} errores. Duración: {Duration:F1}s",
                scanLog.Id, totalObjects, totalChanges, totalErrors,
                (scanLog.CompletedAt.Value - scanLog.StartedAt).TotalSeconds);

            // Enviar notificaciones
            if (_notificationService != null)
            {
                var entries = await _centralRepo.GetScanLogEntriesAsync(scanLog.Id, ct);
                await _notificationService.ProcessAfterScanAsync(scanLog, entries, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            scanLog.CompletedAt = DateTime.UtcNow;
            scanLog.Status = ScanStatus.Failed;
            scanLog.ErrorSummary = "Cancelado por el usuario";
            await _centralRepo.UpdateScanLogAsync(scanLog, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fatal en scan {ScanId}", scanLog.Id);
            scanLog.CompletedAt = DateTime.UtcNow;
            scanLog.Status = ScanStatus.Failed;
            scanLog.ErrorSummary = ex.Message;
            try { await _centralRepo.UpdateScanLogAsync(scanLog, CancellationToken.None); }
            catch { /* best effort */ }
        }

        return scanLog;
    }

    /// <summary>
    /// Escanea un solo cliente (todos sus ambientes, o uno específico).
    /// scanAll=false (default) filtra por objetos base; scanAll=true escanea todo.
    /// </summary>
    public async Task<ScanLog> ScanSingleAsync(
        int clienteId,
        Ambiente? ambiente = null,
        ScanTrigger trigger = ScanTrigger.OnDemand,
        string? triggeredBy = null,
        bool scanAll = false,
        CancellationToken ct = default)
    {
        var cliente = await _localDb.Clientes
            .Include(c => c.Ambientes)
            .Include(c => c.ObjetosCustom)
            .FirstOrDefaultAsync(c => c.Id == clienteId, ct);

        if (cliente == null)
            throw new ArgumentException($"Cliente {clienteId} no encontrado");

        var ambientes = ambiente.HasValue
            ? cliente.Ambientes.Where(a => a.Ambiente == ambiente.Value).ToList()
            : cliente.Ambientes;

        // Cargar objetos base para filtrado (si la tabla está vacía, escanear todo)
        List<ObjetoBase>? allBaseObjects = null;
        if (!scanAll)
        {
            var baseList = await _localDb.ObjetosBase.ToListAsync(ct);
            if (baseList.Count > 0)
                allBaseObjects = baseList;
        }

        // Filtro por cliente: global (ClienteId=null) + específicos del cliente
        var objetosBaseCliente = BuildBaseFilterForCliente(allBaseObjects, cliente.Id);

        var scanLog = new ScanLog
        {
            StartedAt = DateTime.UtcNow,
            Status = ScanStatus.Running,
            Trigger = trigger,
            TriggeredBy = triggeredBy,
            TotalClientes = 1,
            TotalAmbientes = ambientes.Count()
        };

        await _centralRepo.CreateScanLogAsync(scanLog, ct);

        int totalObjects = 0, totalChanges = 0, totalErrors = 0;
        var errorMessages = new List<string>();

        foreach (var amb in ambientes)
        {
            var (objects, changes, error) = await ScanClienteAmbienteAsync(
                scanLog.Id, cliente, amb, objetosBaseCliente, ct);

            totalObjects += objects;
            totalChanges += changes;
            if (error != null)
            {
                totalErrors++;
                errorMessages.Add(error);
            }
        }

        scanLog.CompletedAt = DateTime.UtcNow;
        scanLog.TotalObjectsScanned = totalObjects;
        scanLog.TotalChangesDetected = totalChanges;
        scanLog.TotalErrors = totalErrors;
        scanLog.Status = totalErrors == 0 ? ScanStatus.Completed : ScanStatus.CompletedWithErrors;
        if (errorMessages.Count > 0)
            scanLog.ErrorSummary = string.Join("\n", errorMessages);

        await _centralRepo.UpdateScanLogAsync(scanLog, ct);

        // Enviar notificaciones
        if (_notificationService != null)
        {
            var allEntries = await _centralRepo.GetScanLogEntriesAsync(scanLog.Id, ct);
            await _notificationService.ProcessAfterScanAsync(scanLog, allEntries, ct);
        }

        return scanLog;
    }

    /// <summary>
    /// Escanea un cliente/ambiente específico: extrae objetos, guarda snapshots, detecta cambios.
    /// objetosBaseFilter: si no es null, solo se incluyen objetos cuyo FullName esté en este set.
    /// </summary>
    private async Task<(int ObjectCount, int ChangeCount, string? Error)> ScanClienteAmbienteAsync(
        int scanLogId,
        Cliente cliente,
        ClienteAmbiente ambiente,
        HashSet<string>? objetosBaseFilter,
        CancellationToken ct)
    {
        var entry = new ScanLogEntry
        {
            ScanLogId = scanLogId,
            ClienteId = cliente.Id,
            ClienteCodigo = cliente.Codigo,
            Ambiente = ambiente.Ambiente,
            StartedAt = DateTime.UtcNow,
            Success = true
        };

        await _centralRepo.CreateScanLogEntryAsync(entry, ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var connectionString = ambiente.GetConnectionString(_encryptor != null ? _encryptor.Decrypt : null);

            // Timeout por ambiente: máximo 90 segundos para conectar + extraer + guardar
            using var ambienteCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ambienteCts.CancelAfter(TimeSpan.FromSeconds(90));
            var act = ambienteCts.Token;

            // Test de conexión
            var (success, message) = await _extractor.TestConnectionAsync(connectionString, act);
            if (!success)
            {
                throw new Exception($"Conexión fallida: {message}");
            }

            // Extraer todos los objetos
            var allDbObjects = await _extractor.ExtractAllAsync(connectionString, act);

            // Set de objetos custom
            var customObjects = cliente.ObjetosCustom
                .Select(c => c.NombreObjeto)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Filtrar: si hay filtro de objetos base, solo incluir base + custom del cliente
            var dbObjects = objetosBaseFilter != null
                ? allDbObjects.Where(o =>
                    objetosBaseFilter.Contains(o.FullName)
                    || customObjects.Contains(o.FullName)
                    || IsCustomByConvention(o.FullName, cliente.Codigo)).ToList()
                : allDbObjects;

            var now = DateTime.UtcNow;

            // Convertir a snapshots
            var snapshots = new List<ObjectSnapshot>();
            var definitions = new List<string>();

            foreach (var obj in dbObjects)
            {
                var isCustom = customObjects.Contains(obj.FullName)
                    || IsCustomByConvention(obj.FullName, cliente.Codigo);

                snapshots.Add(new ObjectSnapshot
                {
                    ScanLogId = scanLogId,
                    ClienteId = cliente.Id,
                    ClienteNombre = cliente.Nombre,
                    ClienteCodigo = cliente.Codigo,
                    Ambiente = ambiente.Ambiente,
                    ObjectFullName = obj.FullName,
                    SchemaName = obj.SchemaName,
                    ObjectName = obj.ObjectName,
                    ObjectType = obj.ObjectType.ToSqlType(),
                    DefinitionHash = obj.DefinitionHash,
                    ObjectLastModified = obj.LastModified,
                    SnapshotDate = now,
                    IsCustom = isCustom
                });

                definitions.Add(obj.Definition);
            }

            // Obtener snapshots anteriores ANTES de insertar los nuevos
            // (si no, la vista vw_LatestSnapshots devuelve los del scan actual y no detecta cambios)
            var previousSnapshots = await _centralRepo.GetLatestSnapshotsAsync(
                cliente.Id, ambiente.Ambiente.ToString(), act);

            // Insertar masivamente
            await _centralRepo.BulkInsertSnapshotsAsync(scanLogId, snapshots, definitions, act);

            // Detectar cambios solo para objetos base (no custom)
            var baseSnapshots = snapshots.Where(s => !s.IsCustom).ToList();
            var previousBaseSnapshots = previousSnapshots.Where(s => !s.IsCustom).ToList();
            var changes = await DetectChangesAsync(scanLogId, cliente, ambiente.Ambiente, baseSnapshots, previousBaseSnapshots, act);

            // Notificar a usuarios del cliente si el ambiente tiene notificaciones habilitadas
            if (changes.Count > 0 && ambiente.NotificarCambios && _notificationService != null)
            {
                await _notificationService.SendChangeNotificationToClientUsersAsync(
                    cliente.Id, cliente.Codigo, ambiente.Ambiente, changes, ct);
            }

            sw.Stop();
            entry.CompletedAt = DateTime.UtcNow;
            entry.ObjectsFound = dbObjects.Count;
            entry.ObjectsNew = changes.Count(c => c.ChangeType == ObjectChangeType.Created);
            entry.ObjectsModified = changes.Count(c => c.ChangeType == ObjectChangeType.Modified);
            entry.ObjectsDeleted = changes.Count(c => c.ChangeType == ObjectChangeType.Deleted);
            entry.DurationSeconds = sw.Elapsed.TotalSeconds;
            entry.Success = true;

            await _centralRepo.UpdateScanLogEntryAsync(entry, ct);

            _logger.LogDebug(
                "Scan {Cliente}/{Ambiente}: {Found} objetos, {New} nuevos, {Modified} modificados, {Deleted} eliminados ({Duration:F1}s)",
                cliente.Codigo, ambiente.Ambiente, dbObjects.Count,
                entry.ObjectsNew, entry.ObjectsModified, entry.ObjectsDeleted, entry.DurationSeconds);

            return (dbObjects.Count, changes.Count, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            var errorMsg = $"{cliente.Codigo}/{ambiente.Ambiente}: Timeout (>90s)";
            _logger.LogWarning("Timeout escaneando {Cliente}/{Ambiente} (>90s)", cliente.Codigo, ambiente.Ambiente);

            entry.CompletedAt = DateTime.UtcNow;
            entry.Success = false;
            entry.ErrorMessage = "Timeout: el servidor no respondió en 90 segundos";
            entry.DurationSeconds = sw.Elapsed.TotalSeconds;

            try { await _centralRepo.UpdateScanLogEntryAsync(entry, CancellationToken.None); }
            catch { /* best effort */ }

            return (0, 0, errorMsg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var errorMsg = $"{cliente.Codigo}/{ambiente.Ambiente}: {ex.Message}";
            _logger.LogWarning(ex, "Error escaneando {Cliente}/{Ambiente}", cliente.Codigo, ambiente.Ambiente);

            entry.CompletedAt = DateTime.UtcNow;
            entry.Success = false;
            entry.ErrorMessage = ex.Message;
            entry.DurationSeconds = sw.Elapsed.TotalSeconds;

            try { await _centralRepo.UpdateScanLogEntryAsync(entry, ct); }
            catch { /* best effort */ }

            return (0, 0, errorMsg);
        }
    }

    /// <summary>
    /// Compara snapshots actuales contra los anteriores para detectar cambios.
    /// Los previousSnapshots deben obtenerse ANTES de insertar los del scan actual.
    /// </summary>
    private async Task<List<DetectedChange>> DetectChangesAsync(
        int scanLogId,
        Cliente cliente,
        Ambiente ambiente,
        List<ObjectSnapshot> currentSnapshots,
        List<ObjectSnapshot> previousSnapshots,
        CancellationToken ct)
    {
        var previousByName = previousSnapshots
            .ToDictionary(s => s.ObjectFullName, StringComparer.OrdinalIgnoreCase);

        var currentByName = currentSnapshots
            .ToDictionary(s => s.ObjectFullName, StringComparer.OrdinalIgnoreCase);

        var changes = new List<DetectedChange>();
        var now = DateTime.UtcNow;

        // Primer scan: no hay snapshots anteriores, solo se creó la baseline
        if (previousByName.Count == 0)
            return changes;

        // Objetos nuevos o modificados
        foreach (var (fullName, current) in currentByName)
        {
            if (previousByName.TryGetValue(fullName, out var previous))
            {
                // Existía antes: verificar si cambió
                if (current.DefinitionHash != previous.DefinitionHash)
                {
                    changes.Add(new DetectedChange
                    {
                        ScanLogId = scanLogId,
                        ClienteId = cliente.Id,
                        ClienteCodigo = cliente.Codigo,
                        Ambiente = ambiente,
                        ObjectFullName = fullName,
                        ObjectType = current.ObjectType,
                        ChangeType = ObjectChangeType.Modified,
                        PreviousHash = previous.DefinitionHash,
                        CurrentHash = current.DefinitionHash,
                        DetectedAt = now
                    });
                }
            }
            else
            {
                // Objeto nuevo
                changes.Add(new DetectedChange
                {
                    ScanLogId = scanLogId,
                    ClienteId = cliente.Id,
                    ClienteCodigo = cliente.Codigo,
                    Ambiente = ambiente,
                    ObjectFullName = fullName,
                    ObjectType = current.ObjectType,
                    ChangeType = ObjectChangeType.Created,
                    CurrentHash = current.DefinitionHash,
                    DetectedAt = now
                });
            }
        }

        // Objetos eliminados
        foreach (var (fullName, previous) in previousByName)
        {
            if (!currentByName.ContainsKey(fullName))
            {
                changes.Add(new DetectedChange
                {
                    ScanLogId = scanLogId,
                    ClienteId = cliente.Id,
                    ClienteCodigo = cliente.Codigo,
                    Ambiente = ambiente,
                    ObjectFullName = fullName,
                    ObjectType = previous.ObjectType,
                    ChangeType = ObjectChangeType.Deleted,
                    PreviousHash = previous.DefinitionHash,
                    DetectedAt = now
                });
            }
        }

        if (changes.Count > 0)
        {
            await _centralRepo.BulkInsertChangesAsync(changes, ct);
        }

        return changes;
    }

    /// <summary>
    /// Construye el filtro de objetos base para un cliente específico.
    /// Combina objetos globales (ClienteId=null) + específicos del cliente.
    /// Retorna null si no hay filtro (escanear todo).
    /// </summary>
    private static HashSet<string>? BuildBaseFilterForCliente(List<ObjetoBase>? allBaseObjects, int clienteId)
    {
        if (allBaseObjects == null)
            return null;

        return allBaseObjects
            .Where(o => o.ClienteId == null || o.ClienteId == clienteId)
            .Select(o => o.NombreObjeto)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detecta si un objeto es custom por convención de nombre.
    /// Replicado de DbComparer para evitar dependencia.
    /// </summary>
    private static bool IsCustomByConvention(string fullName, string codigoCliente)
    {
        if (string.IsNullOrEmpty(codigoCliente)) return false;
        var name = fullName.Contains('.') ? fullName.Split('.')[1] : fullName;
        return name.Contains(codigoCliente, StringComparison.OrdinalIgnoreCase);
    }
}
