using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;
using DbSync.Core.Services;

namespace DbSync.Web.Pages.ObjetosBase;

[Authorize(Roles = "Admin")]
public class ImportModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CredentialEncryptor _encryptor;

    public ImportModel(AppDbContext db, CredentialEncryptor encryptor)
    {
        _db = db;
        _encryptor = encryptor;
    }

    public List<Cliente> Clientes { get; set; } = new();

    // Preview data
    public List<ObjetoPreview> ObjetosPreview { get; set; } = new();
    public Cliente? ClienteSeleccionado { get; set; }
    public string? AmbienteSeleccionado { get; set; }
    public bool MostrarPreview { get; set; }

    public string? Mensaje { get; set; }
    public bool MensajeExito { get; set; }

    public async Task OnGetAsync()
    {
        await CargarClientesAsync();
    }

    /// <summary>
    /// Paso 1: Preview - conecta al SQL Server del cliente, extrae objetos y muestra lista seleccionable.
    /// </summary>
    public async Task<IActionResult> OnPostPreviewAsync(int clienteId, string ambiente)
    {
        await CargarClientesAsync();

        ClienteSeleccionado = await _db.Clientes
            .Include(c => c.Ambientes)
            .Include(c => c.ObjetosCustom)
            .FirstOrDefaultAsync(c => c.Id == clienteId);

        if (ClienteSeleccionado == null)
        {
            Mensaje = "Cliente no encontrado";
            return Page();
        }

        AmbienteSeleccionado = ambiente;
        var amb = Enum.Parse<Ambiente>(ambiente, true);
        var ambConfig = ClienteSeleccionado.Ambientes.FirstOrDefault(a => a.Ambiente == amb);
        if (ambConfig == null)
        {
            Mensaje = $"Ambiente {ambiente} no configurado para {ClienteSeleccionado.Nombre}";
            return Page();
        }

        try
        {
            var extractor = new DbObjectExtractor();
            var connStr = ambConfig.GetConnectionString(_encryptor.Decrypt);
            var dbObjects = await extractor.ExtractAllAsync(connStr);

            // Objetos custom del cliente
            var customObjects = ClienteSeleccionado.ObjetosCustom
                .Select(c => c.NombreObjeto)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Todos los objetos base ya existentes (de cualquier cliente o globales)
            // Se guardan con esquema (ej: dbo.MiSP)
            var existentesBase = (await _db.ObjetosBase.ToListAsync())
                .Select(o => o.NombreObjeto)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var obj in dbObjects.OrderBy(o => o.ObjectType.ToShortCode()).ThenBy(o => o.FullName))
            {
                var isCustom = customObjects.Contains(obj.FullName)
                    || (!string.IsNullOrEmpty(ClienteSeleccionado.Codigo)
                        && obj.ObjectName.Contains(ClienteSeleccionado.Codigo, StringComparison.OrdinalIgnoreCase));

                var yaExisteComoBase = existentesBase.Contains(obj.FullName);

                ObjetosPreview.Add(new ObjetoPreview
                {
                    FullName = obj.FullName,
                    TipoObjeto = obj.ObjectType.ToShortCode(),
                    IsCustom = isCustom,
                    YaExisteComoBase = yaExisteComoBase,
                    Seleccionado = !isCustom && !yaExisteComoBase
                });
            }

            MostrarPreview = true;
        }
        catch (Exception ex)
        {
            Mensaje = $"Error conectando: {ex.Message}";
        }

        return Page();
    }

    /// <summary>
    /// Paso 2: Importar los objetos seleccionados como ObjetoBase con ClienteId del cliente origen.
    /// </summary>
    public async Task<IActionResult> OnPostImportAsync(int clienteId, string ambiente, List<string> selectedObjects)
    {
        if (selectedObjects.Count == 0)
        {
            Mensaje = "No se seleccionaron objetos para importar";
            await CargarClientesAsync();
            return Page();
        }

        var cliente = await _db.Clientes.FindAsync(clienteId);
        if (cliente == null)
        {
            Mensaje = "Cliente no encontrado";
            await CargarClientesAsync();
            return Page();
        }

        // Objetos ya existentes para este cliente (con esquema, ej: dbo.MiSP)
        var existentes = (await _db.ObjetosBase
            .Where(o => o.ClienteId == clienteId)
            .ToListAsync())
            .Select(o => o.NombreObjeto)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int importados = 0;
        foreach (var objName in selectedObjects)
        {
            // El formato viene como "TIPO|schema.nombre" del form
            var parts = objName.Split('|', 2);
            var tipo = parts.Length > 1 ? parts[0] : "SP";
            var nombre = parts.Length > 1 ? parts[1] : objName;

            if (existentes.Contains(nombre)) continue;

            _db.ObjetosBase.Add(new ObjetoBase
            {
                ClienteId = clienteId,
                NombreObjeto = nombre,
                TipoObjeto = tipo,
                Notas = $"Importado desde {ambiente}"
            });

            existentes.Add(nombre);
            importados++;
        }

        await _db.SaveChangesAsync();

        return RedirectToPage("/ObjetosBase/Index", new
        {
            mensaje = $"Importados {importados} objetos desde {cliente.Codigo}/{ambiente}",
            exito = true
        });
    }

    private async Task CargarClientesAsync()
    {
        Clientes = await _db.Clientes
            .Include(c => c.Ambientes)
            .Where(c => c.Activo)
            .OrderBy(c => c.Nombre)
            .ToListAsync();
    }

    public class ObjetoPreview
    {
        public string FullName { get; set; } = string.Empty;
        public string TipoObjeto { get; set; } = "SP";
        public bool IsCustom { get; set; }
        public bool YaExisteComoBase { get; set; }
        public bool Seleccionado { get; set; }
    }
}
