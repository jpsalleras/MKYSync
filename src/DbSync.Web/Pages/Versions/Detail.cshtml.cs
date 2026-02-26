using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;
using DbSync.Core.Services;

namespace DbSync.Web.Pages.Versions;

public class DetailModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly VersionHistoryReader _versionReader;
    private readonly CredentialEncryptor _encryptor;

    public DetailModel(AppDbContext db, VersionHistoryReader versionReader, CredentialEncryptor encryptor)
    {
        _db = db;
        _versionReader = versionReader;
        _encryptor = encryptor;
    }

    public ObjectVersionEntry? Version { get; set; }
    public string? ErrorMessage { get; set; }

    [BindProperty(SupportsGet = true)] public int? ClienteId { get; set; }
    [BindProperty(SupportsGet = true)] public string Ambiente { get; set; } = "PR";
    [BindProperty(SupportsGet = true)] public int HistoryId { get; set; }

    public async Task OnGetAsync()
    {
        if (!ClienteId.HasValue || HistoryId == 0) { ErrorMessage = "Parámetros faltantes"; return; }

        try
        {
            var cliente = await _db.Clientes
                .Include(c => c.Ambientes)
                .FirstOrDefaultAsync(c => c.Id == ClienteId.Value);

            if (cliente == null) { ErrorMessage = "Cliente no encontrado"; return; }

            var amb = Enum.Parse<Ambiente>(Ambiente, true);
            var ambConfig = cliente.Ambientes.FirstOrDefault(a => a.Ambiente == amb);
            if (ambConfig == null) { ErrorMessage = $"Ambiente {Ambiente} no configurado"; return; }

            var connStr = ambConfig.GetConnectionString(_encryptor.Decrypt);

            Version = await _versionReader.GetVersionAsync(connStr, HistoryId);
            if (Version == null)
                ErrorMessage = $"Versión con ID {HistoryId} no encontrada";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error: {ex.Message}";
        }
    }
}
