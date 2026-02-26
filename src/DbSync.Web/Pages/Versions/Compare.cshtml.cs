using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;
using DbSync.Core.Services;

namespace DbSync.Web.Pages.Versions;

public class CompareModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly VersionHistoryReader _versionReader;
    private readonly CredentialEncryptor _encryptor;

    public CompareModel(AppDbContext db, VersionHistoryReader versionReader, CredentialEncryptor encryptor)
    {
        _db = db;
        _versionReader = versionReader;
        _encryptor = encryptor;
    }

    public VersionCompareResult? Result { get; set; }
    public string? ErrorMessage { get; set; }

    [BindProperty(SupportsGet = true)] public int? ClienteId { get; set; }
    [BindProperty(SupportsGet = true)] public string Ambiente { get; set; } = "PR";
    [BindProperty(SupportsGet = true)] public int HistoryId1 { get; set; }
    [BindProperty(SupportsGet = true)] public int HistoryId2 { get; set; }
    [BindProperty(SupportsGet = true)] public bool VsActual { get; set; }

    public async Task OnGetAsync()
    {
        if (!ClienteId.HasValue) { ErrorMessage = "Falta clienteId"; return; }
        if (HistoryId1 == 0) { ErrorMessage = "Falta historyId1"; return; }

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

            if (VsActual)
            {
                // Comparar versión histórica vs lo que hay ahora en el ambiente
                Result = await _versionReader.CompareVersionVsCurrentAsync(connStr, HistoryId1, connStr);
            }
            else
            {
                if (HistoryId2 == 0) { ErrorMessage = "Falta historyId2"; return; }
                Result = await _versionReader.CompareVersionsAsync(connStr, HistoryId1, HistoryId2);
            }

            if (Result == null)
                ErrorMessage = "No se encontraron las versiones solicitadas";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error al comparar: {ex.Message}";
        }
    }
}
