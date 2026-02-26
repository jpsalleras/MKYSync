using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using DbSync.Core.Data;
using DbSync.Core.Models;

namespace DbSync.Web.Pages.Scanner;

[Authorize(Policy = "DBAOrAdmin")]
public class LogDetailModel : PageModel
{
    private readonly CentralRepository _centralRepo;

    public LogDetailModel(CentralRepository centralRepo)
    {
        _centralRepo = centralRepo;
    }

    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    public ScanLog? ScanLog { get; set; }
    public List<ScanLogEntry> Entries { get; set; } = new();
    public List<DetectedChange> Changes { get; set; } = new();
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        if (Id <= 0)
        {
            ErrorMessage = "ID de scan no valido.";
            return;
        }

        ScanLog = await _centralRepo.GetScanLogAsync(Id);
        if (ScanLog == null)
        {
            ErrorMessage = $"Scan #{Id} no encontrado.";
            return;
        }

        Entries = await _centralRepo.GetScanLogEntriesAsync(Id);
        Changes = await _centralRepo.GetRecentChangesAsync(top: 100);
        // Filtrar cambios de este scan
        Changes = Changes.Where(c => c.ScanLogId == Id).ToList();
    }
}
