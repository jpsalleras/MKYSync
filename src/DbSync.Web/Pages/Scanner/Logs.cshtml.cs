using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using DbSync.Core.Data;
using DbSync.Core.Models;

namespace DbSync.Web.Pages.Scanner;

[Authorize(Policy = "DBAOrAdmin")]
public class LogsModel : PageModel
{
    private readonly CentralRepository _centralRepo;

    public LogsModel(CentralRepository centralRepo)
    {
        _centralRepo = centralRepo;
    }

    public List<ScanLog> ScanLogs { get; set; } = new();

    public async Task OnGetAsync()
    {
        ScanLogs = await _centralRepo.GetRecentScanLogsAsync(50);
    }
}
