using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;

namespace DbSync.Web.Pages.ObjetosBase;

[Authorize(Policy = "DBAOrAdmin")]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db)
    {
        _db = db;
    }

    public List<ObjetoBase> ObjetosGlobales { get; set; } = new();
    public List<ObjetoBase> ObjetosCliente { get; set; } = new();
    public List<Cliente> Clientes { get; set; } = new();

    [BindProperty]
    public string NuevoNombre { get; set; } = string.Empty;

    [BindProperty]
    public string NuevoTipo { get; set; } = "SP";

    [BindProperty]
    public int? NuevoClienteId { get; set; }

    [BindProperty]
    public string? NuevaNotas { get; set; }

    public string? Mensaje { get; set; }
    public bool MensajeExito { get; set; }

    public async Task OnGetAsync(string? mensaje, bool exito = false)
    {
        // Mensaje desde redirect (ej: después de importar)
        if (!string.IsNullOrEmpty(mensaje))
        {
            Mensaje = mensaje;
            MensajeExito = exito;
        }

        await CargarDatosAsync();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (!User.IsInRole("Admin"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(NuevoNombre))
        {
            Mensaje = "El nombre del objeto es requerido";
            await CargarDatosAsync();
            return Page();
        }

        var nombre = NuevoNombre.Trim();
        int? clienteId = NuevoClienteId > 0 ? NuevoClienteId : null;

        var existe = await _db.ObjetosBase
            .AnyAsync(o => o.NombreObjeto == nombre && o.TipoObjeto == NuevoTipo && o.ClienteId == clienteId);

        if (existe)
        {
            var scope = clienteId.HasValue ? "para ese cliente" : "global";
            Mensaje = $"El objeto {nombre} ({NuevoTipo}) ya existe como objeto base {scope}";
            await CargarDatosAsync();
            return Page();
        }

        _db.ObjetosBase.Add(new ObjetoBase
        {
            ClienteId = clienteId,
            NombreObjeto = nombre,
            TipoObjeto = NuevoTipo,
            Notas = string.IsNullOrWhiteSpace(NuevaNotas) ? null : NuevaNotas.Trim()
        });

        await _db.SaveChangesAsync();
        MensajeExito = true;
        Mensaje = $"Objeto {nombre} agregado como objeto base" + (clienteId.HasValue ? " (específico del cliente)" : " (global)");
        await CargarDatosAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (!User.IsInRole("Admin"))
            return Forbid();

        var obj = await _db.ObjetosBase.FindAsync(id);
        if (obj != null)
        {
            _db.ObjetosBase.Remove(obj);
            await _db.SaveChangesAsync();
            MensajeExito = true;
            Mensaje = $"Objeto {obj.NombreObjeto} eliminado";
        }

        await CargarDatosAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateNotesAsync(int id, string? notas)
    {
        if (!User.IsInRole("Admin"))
            return Forbid();

        var obj = await _db.ObjetosBase.FindAsync(id);
        if (obj != null)
        {
            obj.Notas = string.IsNullOrWhiteSpace(notas) ? null : notas.Trim();
            await _db.SaveChangesAsync();
            return new JsonResult(new { ok = true });
        }

        return new JsonResult(new { ok = false, message = "Objeto no encontrado" });
    }

    private async Task CargarDatosAsync()
    {
        var todos = await _db.ObjetosBase
            .Include(o => o.Cliente)
            .OrderBy(o => o.ClienteId)
            .ThenBy(o => o.TipoObjeto)
            .ThenBy(o => o.NombreObjeto)
            .ToListAsync();

        ObjetosGlobales = todos.Where(o => o.ClienteId == null).ToList();
        ObjetosCliente = todos.Where(o => o.ClienteId != null).ToList();

        Clientes = await _db.Clientes
            .Include(c => c.Ambientes)
            .Where(c => c.Activo)
            .OrderBy(c => c.Nombre)
            .ToListAsync();
    }
}
