using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using DbSync.Core.Data;
using DbSync.Core.Models;

namespace DbSync.Core.Services;

public class UserClientService
{
    private readonly AppDbContext _db;

    public UserClientService(AppDbContext db) => _db = db;

    public IQueryable<Cliente> GetClientesForUser(string userId, bool isAdmin)
    {
        var query = _db.Clientes.Where(c => c.Activo);

        if (!isAdmin)
        {
            var assignedIds = _db.UsuarioClientes
                .Where(uc => uc.UserId == userId)
                .Select(uc => uc.ClienteId);

            query = query.Where(c => assignedIds.Contains(c.Id));
        }

        return query.OrderBy(c => c.Nombre);
    }

    public async Task<List<SelectListItem>> GetClientesSelectListAsync(
        string userId, bool isAdmin, int? selectedId = null)
    {
        var clientes = await GetClientesForUser(userId, isAdmin).ToListAsync();

        return clientes.Select(c => new SelectListItem
        {
            Value = c.Id.ToString(),
            Text = $"{c.Codigo} - {c.Nombre}",
            Selected = c.Id == selectedId
        }).ToList();
    }

    public async Task<bool> UserHasAccessToClienteAsync(string userId, bool isAdmin, int clienteId)
    {
        if (isAdmin) return true;
        return await _db.UsuarioClientes
            .AnyAsync(uc => uc.UserId == userId && uc.ClienteId == clienteId);
    }
}
