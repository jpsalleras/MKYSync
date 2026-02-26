using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;

namespace DbSync.Web.Pages.BaseVersions;

[Authorize(Policy = "DBAOrAdmin")]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CentralRepository _centralRepo;

    public IndexModel(AppDbContext db, CentralRepository centralRepo)
    {
        _db = db;
        _centralRepo = centralRepo;
    }

    public List<BaseVersion> Versions { get; set; } = new();
    public List<SelectListItem> ClientesList { get; set; } = new();

    public string? Mensaje { get; set; }
    public bool MensajeExito { get; set; }

    [BindProperty]
    public string NuevoNombre { get; set; } = string.Empty;

    [BindProperty]
    public string? NuevaDescripcion { get; set; }

    [BindProperty]
    public int NuevoClienteId { get; set; }

    [BindProperty]
    public string NuevoAmbiente { get; set; } = "PR";

    public async Task OnGetAsync(string? mensaje, bool exito = false)
    {
        if (!string.IsNullOrEmpty(mensaje))
        {
            Mensaje = mensaje;
            MensajeExito = exito;
        }

        await CargarDatosAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!User.IsInRole("Admin") && !User.IsInRole("DBA"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(NuevoNombre))
        {
            Mensaje = "El nombre de la version es requerido";
            await CargarDatosAsync();
            return Page();
        }

        if (NuevoClienteId <= 0)
        {
            Mensaje = "Debe seleccionar un cliente origen";
            await CargarDatosAsync();
            return Page();
        }

        var versionName = NuevoNombre.Trim();

        // Verificar que no exista una version con ese nombre
        var existing = await _centralRepo.GetAllBaseVersionsAsync();
        if (existing.Any(v => v.VersionName.Equals(versionName, StringComparison.OrdinalIgnoreCase)))
        {
            Mensaje = $"Ya existe una version con el nombre '{versionName}'";
            await CargarDatosAsync();
            return Page();
        }

        var cliente = await _db.Clientes
            .FirstOrDefaultAsync(c => c.Id == NuevoClienteId);

        if (cliente == null)
        {
            Mensaje = "Cliente no encontrado";
            await CargarDatosAsync();
            return Page();
        }

        try
        {
            var version = new BaseVersion
            {
                VersionName = versionName,
                Description = string.IsNullOrWhiteSpace(NuevaDescripcion) ? null : NuevaDescripcion.Trim(),
                SourceClienteId = cliente.Id,
                SourceClienteNombre = cliente.Nombre,
                SourceClienteCodigo = cliente.Codigo,
                SourceAmbiente = NuevoAmbiente,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = User.Identity?.Name
            };

            await _centralRepo.CreateBaseVersionAsync(version);
            var count = await _centralRepo.CreateBaseVersionFromSnapshotsAsync(
                version.Id, cliente.Id, NuevoAmbiente);

            if (count == 0)
            {
                // Limpiar la version vacia
                await _centralRepo.DeleteBaseVersionAsync(version.Id);
                Mensaje = $"No se encontraron snapshots para {cliente.Codigo} / {NuevoAmbiente}. Ejecute un scan primero.";
            }
            else
            {
                MensajeExito = true;
                Mensaje = $"Version '{versionName}' creada con {count} objetos desde {cliente.Codigo} / {NuevoAmbiente}";
            }
        }
        catch (Exception ex)
        {
            Mensaje = $"Error al crear version: {ex.Message}";
        }

        await CargarDatosAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (!User.IsInRole("Admin") && !User.IsInRole("DBA"))
            return Forbid();

        try
        {
            var version = await _centralRepo.GetBaseVersionAsync(id);
            if (version != null)
            {
                await _centralRepo.DeleteBaseVersionAsync(id);
                MensajeExito = true;
                Mensaje = $"Version '{version.VersionName}' eliminada";
            }
        }
        catch (Exception ex)
        {
            Mensaje = $"Error al eliminar version: {ex.Message}";
        }

        await CargarDatosAsync();
        return Page();
    }

    public async Task<IActionResult> OnGetDetailAsync(int id)
    {
        var objects = await _centralRepo.GetBaseVersionObjectsAsync(id);
        return new JsonResult(objects.Select(o => new
        {
            o.ObjectFullName,
            o.ObjectType,
            o.SchemaName,
            o.ObjectName
        }));
    }

    private async Task CargarDatosAsync()
    {
        Versions = await _centralRepo.GetAllBaseVersionsAsync();

        var clientes = await _db.Clientes
            .Where(c => c.Activo)
            .OrderBy(c => c.Nombre)
            .ToListAsync();

        ClientesList = clientes.Select(c => new SelectListItem(
            $"{c.Codigo} - {c.Nombre}", c.Id.ToString())).ToList();
    }
}
