using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DbSync.Web.Pages;

public abstract class DbSyncPageModel : PageModel
{
    protected string CurrentUserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    protected bool IsAdmin => User.IsInRole("Admin");

    protected bool IsDBA => User.IsInRole("DBA");

    protected string CurrentUserName =>
        User.Identity?.Name ?? "unknown";
}
