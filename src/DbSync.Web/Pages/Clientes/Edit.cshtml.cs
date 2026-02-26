using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;
using DbSync.Core.Services;
using System.Security.Claims;

namespace DbSync.Web.Pages.Clientes;

[Authorize(Roles = "Admin")]
public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CredentialEncryptor _encryptor;
    private readonly VersionControlProvisioner? _provisioner;
    private readonly UserClientService _userClientService;

    public EditModel(AppDbContext db, CredentialEncryptor encryptor, UserClientService userClientService, VersionControlProvisioner? provisioner = null)
    {
        _db = db;
        _encryptor = encryptor;
        _userClientService = userClientService;
        _provisioner = provisioner;
    }

    [BindProperty]
    public Cliente Cliente { get; set; } = new() { Activo = true };

    [BindProperty]
    public List<AmbienteConfig> AmbientesConfig { get; set; } = new();

    public bool IsNew => Cliente.Id == 0;
    public string? ProvisionMessage { get; set; }
    public bool ProvisionSuccess { get; set; }
    public Dictionary<string, (bool Table, bool Trigger, string Message)> VcStatus { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id.HasValue)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            var isAdmin = User.IsInRole("Admin");
            if (!await _userClientService.UserHasAccessToClienteAsync(userId, isAdmin, id.Value))
                return Forbid();

            Cliente = await _db.Clientes
                .Include(c => c.Ambientes)
                .FirstOrDefaultAsync(c => c.Id == id.Value) ?? new Cliente { Activo = true };
        }

        // Asegurar que siempre hay config para los 3 ambientes
        AmbientesConfig = new List<AmbienteConfig>();
        foreach (Ambiente amb in Enum.GetValues<Ambiente>())
        {
            var existing = Cliente.Ambientes.FirstOrDefault(a => a.Ambiente == amb);
            AmbientesConfig.Add(new AmbienteConfig
            {
                Id = existing?.Id ?? 0,
                Ambiente = amb,
                Server = existing?.Server ?? "",
                Database = existing?.Database ?? "",
                UserId = existing?.UserId ?? "",
                Password = "",
                NotificarCambios = existing?.NotificarCambios ?? false
            });
        }

        return Page();
    }

    public async Task<IActionResult> OnGetTestConnectionAsync(string server, string database, string? userId, string? password)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
            return new JsonResult(new { ok = false, message = "Servidor y base de datos son requeridos" });

        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                TrustServerCertificate = true,
                ConnectTimeout = 10
            };

            if (string.IsNullOrEmpty(userId))
                builder.IntegratedSecurity = true;
            else
            {
                builder.UserID = userId;
                builder.Password = password ?? "";
            }

            var extractor = new DbObjectExtractor();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var (ok, msg) = await extractor.TestConnectionAsync(builder.ConnectionString, cts.Token);
            return new JsonResult(new { ok, message = ok ? "Conexión exitosa" : msg });
        }
        catch (OperationCanceledException)
        {
            return new JsonResult(new { ok = false, message = "Tiempo de espera agotado. Verifique servidor y puerto." });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, message = ex.Message });
        }
    }

    public async Task<IActionResult> OnGetCheckVcStatusAsync(int clienteId)
    {
        if (_provisioner == null)
            return new JsonResult(new { ok = false, message = "Provisioner no disponible" });

        var cliente = await _db.Clientes
            .Include(c => c.Ambientes)
            .FirstOrDefaultAsync(c => c.Id == clienteId);

        if (cliente == null)
            return new JsonResult(new { ok = false, message = "Cliente no encontrado" });

        var results = new Dictionary<string, object>();
        foreach (var amb in cliente.Ambientes.Where(a => !string.IsNullOrEmpty(a.Server)))
        {
            var ambKey = amb.Ambiente.ToString();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var connStr = amb.GetConnectionString(_encryptor.Decrypt);
                var (tableExists, triggerExists, message) = await _provisioner.CheckStatusAsync(connStr);
                results[ambKey] = new { table = tableExists, trigger = triggerExists, message };
            }
            catch (Exception ex)
            {
                results[ambKey] = new { table = false, trigger = false, message = $"Error: {ex.Message}" };
            }
        }

        return new JsonResult(new { ok = true, status = results });
    }

    public async Task<IActionResult> OnGetQueryVersionAsync(string server, string database, string? userId, string? password)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
            return new JsonResult(new { ok = false, version = "" });

        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                TrustServerCertificate = true,
                ConnectTimeout = 10
            };

            if (string.IsNullOrEmpty(userId))
                builder.IntegratedSecurity = true;
            else
            {
                builder.UserID = userId;
                builder.Password = password ?? "";
            }

            await using var conn = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await conn.OpenAsync(cts.Token);
            await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
                "SELECT TOP 1 siveVersion FROM SistemaVersion ORDER BY siveFecha DESC", conn)
            { CommandTimeout = 10 };
            var result = await cmd.ExecuteScalarAsync(cts.Token);
            if (result != null && result != DBNull.Value)
                return new JsonResult(new { ok = true, version = result.ToString() });

            return new JsonResult(new { ok = true, version = "Sin datos" });
        }
        catch (OperationCanceledException)
        {
            return new JsonResult(new { ok = false, version = "Timeout" });
        }
        catch
        {
            return new JsonResult(new { ok = false, version = "Error" });
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Cliente.Nombre) || string.IsNullOrWhiteSpace(Cliente.Codigo))
        {
            ModelState.AddModelError("", "Nombre y Código son requeridos");
            return Page();
        }

        Cliente existente;
        if (Cliente.Id == 0)
        {
            existente = new Cliente
            {
                Nombre = Cliente.Nombre,
                Codigo = Cliente.Codigo,
                Activo = Cliente.Activo
            };
            _db.Clientes.Add(existente);
            await _db.SaveChangesAsync();
        }
        else
        {
            existente = await _db.Clientes
                .Include(c => c.Ambientes)
                .FirstAsync(c => c.Id == Cliente.Id);
            existente.Nombre = Cliente.Nombre;
            existente.Codigo = Cliente.Codigo;
            existente.Activo = Cliente.Activo;
        }

        // Actualizar ambientes
        foreach (var ambConfig in AmbientesConfig)
        {
            if (string.IsNullOrWhiteSpace(ambConfig.Server)) continue;

            var existing = existente.Ambientes.FirstOrDefault(a => a.Ambiente == ambConfig.Ambiente);
            if (existing != null)
            {
                existing.Server = ambConfig.Server;
                existing.Database = ambConfig.Database;
                existing.UserId = string.IsNullOrWhiteSpace(ambConfig.UserId) ? null : ambConfig.UserId;
                existing.NotificarCambios = ambConfig.NotificarCambios;
                if (!string.IsNullOrEmpty(ambConfig.Password))
                    existing.PasswordEncrypted = _encryptor.Encrypt(ambConfig.Password);
            }
            else
            {
                existente.Ambientes.Add(new ClienteAmbiente
                {
                    ClienteId = existente.Id,
                    Ambiente = ambConfig.Ambiente,
                    Server = ambConfig.Server,
                    Database = ambConfig.Database,
                    UserId = string.IsNullOrWhiteSpace(ambConfig.UserId) ? null : ambConfig.UserId,
                    PasswordEncrypted = string.IsNullOrEmpty(ambConfig.Password) ? null : _encryptor.Encrypt(ambConfig.Password),
                    NotificarCambios = ambConfig.NotificarCambios
                });
            }
        }

        await _db.SaveChangesAsync();
        return RedirectToPage("/Clientes/Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!User.IsInRole("Admin"))
            return Forbid();

        var cliente = await _db.Clientes.FindAsync(Cliente.Id);
        if (cliente != null)
        {
            _db.Clientes.Remove(cliente);
            await _db.SaveChangesAsync();
        }
        return RedirectToPage("/Clientes/Index");
    }

    public async Task<IActionResult> OnPostProvisionAsync(string ambiente)
    {
        if (!User.IsInRole("Admin") && !User.IsInRole("DBA"))
            return Forbid();

        if (_provisioner == null)
        {
            ProvisionMessage = "Provisioner no disponible";
            return await ReloadAndReturn();
        }

        var cliente = await _db.Clientes
            .Include(c => c.Ambientes)
            .FirstOrDefaultAsync(c => c.Id == Cliente.Id);

        if (cliente == null)
        {
            ProvisionMessage = "Cliente no encontrado";
            return await ReloadAndReturn();
        }

        var amb = Enum.Parse<Ambiente>(ambiente);
        var ambConfig = cliente.Ambientes.FirstOrDefault(a => a.Ambiente == amb);

        if (ambConfig == null)
        {
            ProvisionMessage = $"Ambiente {ambiente} no configurado";
            return await ReloadAndReturn();
        }

        var connStr = ambConfig.GetConnectionString(_encryptor.Decrypt);

        var result = await _provisioner.ProvisionAsync(connStr);

        ProvisionSuccess = result.Success;
        ProvisionMessage = result.Success
            ? $"Versionado activado en {ambiente}:\n" + string.Join("\n", result.Steps)
            : $"Error activando versionado en {ambiente}: {result.Error}\nPasos completados:\n" + string.Join("\n", result.Steps);

        return await ReloadAndReturn();
    }

    public async Task<IActionResult> OnPostDeactivateAsync(string ambiente)
    {
        if (!User.IsInRole("Admin") && !User.IsInRole("DBA"))
            return Forbid();

        if (_provisioner == null)
        {
            ProvisionMessage = "Provisioner no disponible";
            return await ReloadAndReturn();
        }

        var cliente = await _db.Clientes
            .Include(c => c.Ambientes)
            .FirstOrDefaultAsync(c => c.Id == Cliente.Id);

        if (cliente == null)
        {
            ProvisionMessage = "Cliente no encontrado";
            return await ReloadAndReturn();
        }

        var amb = Enum.Parse<Ambiente>(ambiente);
        var ambConfig = cliente.Ambientes.FirstOrDefault(a => a.Ambiente == amb);

        if (ambConfig == null)
        {
            ProvisionMessage = $"Ambiente {ambiente} no configurado";
            return await ReloadAndReturn();
        }

        var connStr = ambConfig.GetConnectionString(_encryptor.Decrypt);

        var result = await _provisioner.DeactivateAsync(connStr);

        ProvisionSuccess = result.Success;
        ProvisionMessage = result.Success
            ? $"Versionado desactivado en {ambiente}:\n" + string.Join("\n", result.Steps)
            : $"Error desactivando versionado en {ambiente}: {result.Error}";

        return await ReloadAndReturn();
    }

    private async Task<IActionResult> ReloadAndReturn()
    {
        // Recargar datos del cliente para la vista
        if (Cliente.Id > 0)
        {
            Cliente = await _db.Clientes
                .Include(c => c.Ambientes)
                .FirstAsync(c => c.Id == Cliente.Id);
        }

        AmbientesConfig = new List<AmbienteConfig>();
        foreach (Ambiente amb in Enum.GetValues<Ambiente>())
        {
            var existing = Cliente.Ambientes.FirstOrDefault(a => a.Ambiente == amb);
            AmbientesConfig.Add(new AmbienteConfig
            {
                Id = existing?.Id ?? 0,
                Ambiente = amb,
                Server = existing?.Server ?? "",
                Database = existing?.Database ?? "",
                UserId = existing?.UserId ?? "",
                Password = "",
                NotificarCambios = existing?.NotificarCambios ?? false
            });
        }

        if (_provisioner != null)
            await CheckVersionControlStatusAsync();

        return Page();
    }

    private async Task CheckVersionControlStatusAsync()
    {
        if (_provisioner == null) return;

        foreach (var amb in Cliente.Ambientes)
        {
            try
            {
                var connStr = amb.GetConnectionString(_encryptor.Decrypt);
                var (tableExists, triggerExists, message) =
                    await _provisioner.CheckStatusAsync(connStr);
                VcStatus[amb.Ambiente.ToString()] = (tableExists, triggerExists, message);
            }
            catch (Exception ex)
            {
                VcStatus[amb.Ambiente.ToString()] = (false, false, $"Error: {ex.Message}");
            }
        }
    }

    public class AmbienteConfig
    {
        public int Id { get; set; }
        public Ambiente Ambiente { get; set; }
        public string Server { get; set; } = "";
        public string Database { get; set; } = "";
        public string? UserId { get; set; }
        public string Password { get; set; } = "";
        public bool NotificarCambios { get; set; }
    }
}
