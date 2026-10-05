using System.Diagnostics;
using System.Security.Claims;
using AhoraCenit.Api.Contracts.Users;
using AhoraCenit.Api.Data;
using AhoraCenit.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AhoraCenit.Api.Features.Users;

public static class UsersEndpoints
{
    public static RouteGroupBuilder MapUsersEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization("AdminOnly");

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetByIdAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapPost("/{id:guid}/reset-password", ResetPasswordAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        return group;
    }

    private static async Task<IResult> ListAsync(AppDbContext db, CancellationToken ct)
    {
        var users = await db.Users
            .Include(u => u.Applications)
            .OrderBy(u => u.Name)
            .ToListAsync(ct);

        return Results.Ok(users.Select(ToResponse));
    }

    private static async Task<IResult> GetByIdAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var user = await db.Users.Include(u => u.Applications).FirstOrDefaultAsync(u => u.Id == id, ct);
        return user is null ? Results.NotFound() : Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        IAuditLogger auditLogger,
        ISharedSqlProvisioner sharedSql,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.BadRequest(new { message = "Email, nombre y contraseña son obligatorios." });
        }

        if (request.Password.Length < 8)
        {
            return Results.BadRequest(new { message = "La contraseña debe tener al menos 8 caracteres." });
        }

        if (!TryParseRole(request.Role, out var role))
        {
            return Results.BadRequest(new { message = "Rol no válido." });
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var emailTaken = await db.Users.AnyAsync(u => u.Email == normalizedEmail, ct);
        if (emailTaken)
        {
            return Results.Conflict(new { message = "Ya existe un usuario con ese email." });
        }

        var baseSlug = SlugGenerator.Slugify(request.Name);
        if (string.IsNullOrEmpty(baseSlug))
        {
            baseSlug = "cliente";
        }

        var clientSlug = await SlugGenerator.ResolveUniqueAsync(
            baseSlug,
            slug => db.Users.AnyAsync(u => u.ClientSlug == slug, ct));

        var user = new User
        {
            Email = normalizedEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Name = request.Name.Trim(),
            Role = role,
            ClientSlug = clientSlug,
            EmailConfirmed = true
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await SharedSqlLogins.TryCreateAsync(user, db, sharedSql, loggerFactory, ct);
        sw.Stop();

        await auditLogger.RecordAsync(
            principal, "User", "Create", user.Id,
            parameters: new { user.Email, user.Name, Role = user.Role.ToString() },
            result: new { user.Id, user.ClientSlug },
            sw.Elapsed, success: true);

        return Results.Created($"/api/users/{user.Id}", ToResponse(user));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateUserRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { message = "Email y nombre son obligatorios." });
        }

        if (!TryParseRole(request.Role, out var role))
        {
            return Results.BadRequest(new { message = "Rol no válido." });
        }

        var user = await db.Users.FindAsync([id], ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var emailTaken = await db.Users.AnyAsync(u => u.Id != id && u.Email == normalizedEmail, ct);
        if (emailTaken)
        {
            return Results.Conflict(new { message = "Ya existe un usuario con ese email." });
        }

        if (user.Role == UserRole.Admin && role != UserRole.Admin)
        {
            var otherAdmins = await db.Users.CountAsync(u => u.Id != id && u.Role == UserRole.Admin, ct);
            if (otherAdmins == 0)
            {
                return Results.Conflict(new { message = "No puede quedar el sistema sin ningún administrador." });
            }
        }

        var previousRole = user.Role.ToString();
        user.Email = normalizedEmail;
        user.Name = request.Name.Trim();
        user.Role = role;
        await db.SaveChangesAsync(ct);
        sw.Stop();

        await auditLogger.RecordAsync(
            principal, "User", "Update", user.Id,
            parameters: new { request.Email, request.Name, request.Role },
            result: new { PreviousRole = previousRole, NewRole = user.Role.ToString() },
            sw.Elapsed, success: true);

        return Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> ResetPasswordAsync(
        Guid id,
        ResetUserPasswordRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
        {
            return Results.BadRequest(new { message = "La nueva contraseña debe tener al menos 8 caracteres." });
        }

        var user = await db.Users.FindAsync([id], ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await db.SaveChangesAsync(ct);
        sw.Stop();

        await auditLogger.RecordAsync(
            principal, "User", "ResetPassword", user.Id,
            parameters: null,
            result: new { user.Email },
            sw.Elapsed, success: true);

        return Results.Ok(new { message = "Contraseña actualizada." });
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        IAuditLogger auditLogger,
        ISharedSqlProvisioner sharedSql,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (principal.GetUserId() == id)
        {
            return Results.BadRequest(new { message = "No puedes eliminar tu propio usuario." });
        }

        var user = await db.Users.FindAsync([id], ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (user.Role == UserRole.Admin)
        {
            var otherAdmins = await db.Users.CountAsync(u => u.Id != id && u.Role == UserRole.Admin, ct);
            if (otherAdmins == 0)
            {
                return Results.Conflict(new { message = "No puede quedar el sistema sin ningún administrador." });
            }
        }

        var hasApplications = await db.Applications.AnyAsync(a => a.UserId == id, ct);
        if (hasApplications)
        {
            return Results.Conflict(new
            {
                message = "No se puede borrar el usuario porque tiene aplicaciones (instancias) desplegadas."
            });
        }

        var deletedSnapshot = new { user.Email, user.Name, Role = user.Role.ToString(), user.ClientSlug };

        if (!string.IsNullOrEmpty(user.SqlLogin))
        {
            try
            {
                await sharedSql.DropClientLoginAsync(user.SqlLogin, ct);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AhoraCenit.Users").LogWarning(ex,
                    "No se pudo borrar el login SQL {Login} del cliente {ClientSlug}", user.SqlLogin, user.ClientSlug);
            }
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        sw.Stop();

        await auditLogger.RecordAsync(
            principal, "User", "Delete", id,
            parameters: null,
            result: deletedSnapshot,
            sw.Elapsed, success: true);

        return Results.NoContent();
    }

    private static bool TryParseRole(string? role, out UserRole parsed) =>
        Enum.TryParse(role, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);

    private static AdminUserResponse ToResponse(User user) =>
        new(
            user.Id,
            user.Email,
            user.Name,
            user.Role.ToString(),
            user.ClientSlug,
            user.EmailConfirmed,
            user.CreatedAt,
            user.Applications.Count);
}
