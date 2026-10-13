using AhoraCenit.Api.Data;
using AhoraCenit.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AhoraCenit.Api.Features.Trash;

public record TrashedApplicationResponse(
    Guid Id,
    string Subdomain,
    string ProductName,
    Guid UserId,
    string ClientName,
    string ClientSlug,
    DateTime DeletedAt,
    DateTime PurgeAt);

public record TrashedUserResponse(Guid Id, string Name, string Email, string ClientSlug, DateTime DeletedAt, DateTime PurgeAt);

public record TrashResponse(int RetentionDays, IReadOnlyList<TrashedApplicationResponse> Applications, IReadOnlyList<TrashedUserResponse> Users);

/// <summary>
/// Administración → Papelera. Restaurar y borrar del todo cada elemento van por
/// /api/applications/{id}/restore, /api/users/{id}/restore y DELETE ...?purge=true.
/// </summary>
public static class TrashEndpoints
{
    public static RouteGroupBuilder MapTrashEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization("AdminOnly");

        group.MapGet("/", ListAsync);
        group.MapPost("/purge-expired", PurgeExpiredAsync);

        return group;
    }

    private static async Task<IResult> ListAsync(AppDbContext db, ITrashService trash, CancellationToken ct)
    {
        var applications = await db.Applications.IgnoreQueryFilters().AsNoTracking()
            .Include(a => a.Product)
            .Include(a => a.User)
            .Where(a => a.DeletedAt != null)
            .OrderBy(a => a.DeletedAt)
            .ToListAsync(ct);
        var users = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.DeletedAt != null)
            .OrderBy(u => u.DeletedAt)
            .ToListAsync(ct);

        return Results.Ok(new TrashResponse(
            trash.RetentionDays,
            applications.Select(a => new TrashedApplicationResponse(
                a.Id, a.Subdomain, a.Product?.Name ?? string.Empty, a.UserId, a.User?.Name ?? string.Empty,
                a.User?.ClientSlug ?? string.Empty, a.DeletedAt!.Value, trash.PurgeDate(a.DeletedAt.Value))).ToList(),
            users.Select(u => new TrashedUserResponse(u.Id, u.Name, u.Email, u.ClientSlug, u.DeletedAt!.Value, trash.PurgeDate(u.DeletedAt.Value))).ToList()));
    }

    /// <summary>Ejecuta ya la limpieza diaria (lo que ha cumplido su plazo).</summary>
    private static async Task<IResult> PurgeExpiredAsync(ITrashService trash, CancellationToken ct)
    {
        var (applications, users) = await trash.PurgeExpiredAsync(ct);
        return Results.Ok(new { applications, users });
    }
}
