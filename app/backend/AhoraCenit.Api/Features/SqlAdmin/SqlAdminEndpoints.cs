using System.Security.Claims;
using AhoraCenit.Api.Data;
using AhoraCenit.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AhoraCenit.Api.Features.SqlAdmin;

public record SqlAdminLaunchRequest(Guid? ApplicationId);

/// <summary>
/// Explorador de bases de datos (CloudBeaver) bajo demanda. El administrador pulsa "Explorar BBDD" (en
/// Herramientas: el SQL de la plataforma; en una instancia: su base de datos), el portal lo prepara y le da un pase
/// de un solo uso; con él, sqladmin.DOMINIO deja una cookie y Traefik consulta al portal en cada petición.
/// </summary>
public static class SqlAdminEndpoints
{
    public static WebApplication MapSqlAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin/sqladmin").RequireAuthorization("AdminOnly");
        admin.MapPost("/launch", LaunchAsync);
        admin.MapGet("/launch/{id:guid}", GetLaunch);

        // forwardAuth de Traefik (sin autenticación del portal: valida la cookie del pase).
        app.MapGet("/api/sqladmin/auth", Authorize).AllowAnonymous();
        // Llega por sqladmin.DOMINIO/__cenit/enter (Traefik lo manda al portal).
        app.MapGet("/__cenit/enter", Enter).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> LaunchAsync(
        SqlAdminLaunchRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        SqlAdminService sqlAdmin,
        IConfiguration configuration,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        SqlAdminTarget target;
        if (request.ApplicationId is null)
        {
            var builder = new SqlConnectionStringBuilder(configuration.GetConnectionString("Default"));
            target = new SqlAdminTarget("platform", "Cenit · SQL Server de la plataforma",
                sqlAdmin.Options.PlatformSqlHost, 1433, builder.UserID, builder.Password, "master");
        }
        else
        {
            var application = await db.Applications.Include(a => a.User).FirstOrDefaultAsync(a => a.Id == request.ApplicationId, ct);
            if (application is null)
            {
                return Results.NotFound();
            }

            var (resolved, error) = await ResolveApplicationTargetAsync(application, sqlAdmin, ct);
            if (resolved is null)
            {
                return Results.Conflict(new { message = error });
            }

            target = resolved;
        }

        var id = sqlAdmin.StartLaunch(target);
        await auditLogger.RecordAsync(principal, "SqlAdmin", "Launch", request.ApplicationId,
            parameters: null, result: new { target.Key, target.Name, target.Host, target.User }, TimeSpan.Zero, success: true);
        return Results.Accepted($"/api/admin/sqladmin/launch/{id}", new { id, state = "starting" });
    }

    /// <summary>
    /// SQL Server propio de la instancia (su contenedor, con la contraseña de sa que tiene) o, en BBDD compartida,
    /// el SQL de la plataforma con el login de su cliente (que solo ve sus bases).
    /// </summary>
    private static async Task<(SqlAdminTarget? Target, string? Error)> ResolveApplicationTargetAsync(
        Application application, SqlAdminService sqlAdmin, CancellationToken ct)
    {
        var containers = await sqlAdmin.ListStackContainersAsync(application.Subdomain, ct);
        var sql = containers.FirstOrDefault(c =>
            c.Image.Contains("mssql", StringComparison.OrdinalIgnoreCase)
            || (c.Env.Any(e => e.StartsWith("ACCEPT_EULA=", StringComparison.Ordinal))
                && c.Env.Any(e => e.StartsWith("MSSQL_SA_PASSWORD=", StringComparison.Ordinal) || e.StartsWith("SA_PASSWORD=", StringComparison.Ordinal))));
        if (sql != default)
        {
            if (!sql.Running)
            {
                return (null, $"El SQL Server de {application.Subdomain} está parado: arranca la aplicación para explorarlo.");
            }

            var password = sql.Env.Select(e => e.Split('=', 2)).Where(p => p.Length == 2 && p[0] is "MSSQL_SA_PASSWORD" or "SA_PASSWORD").Select(p => p[1]).FirstOrDefault();
            if (string.IsNullOrEmpty(password))
            {
                return (null, $"No se encuentra la contraseña de sa del SQL Server de {application.Subdomain}.");
            }

            var alias = $"{application.Subdomain}-sql";
            return (new SqlAdminTarget($"app:{application.Id}", $"{application.Subdomain} · SQL Server propio",
                alias, 1433, "sa", password, "master", sql.Id, alias), null);
        }

        if (!string.IsNullOrEmpty(application.User?.SqlLogin) && !string.IsNullOrEmpty(application.User.SqlPassword))
        {
            return (new SqlAdminTarget($"app:{application.Id}", $"{application.Subdomain} · BBDD compartida ({application.User.Name})",
                sqlAdmin.Options.PlatformSqlHost, 1433, application.User.SqlLogin, application.User.SqlPassword, "master"), null);
        }

        return (null, $"{application.Subdomain} no tiene un SQL Server que explorar.");
    }

    private static IResult GetLaunch(Guid id, SqlAdminService sqlAdmin) =>
        sqlAdmin.GetLaunch(id) is { } launch ? Results.Ok(launch) : Results.NotFound();

    private static IResult Authorize(HttpRequest request, SqlAdminService sqlAdmin)
    {
        if (sqlAdmin.ValidateCookie(request.Cookies[SqlAdminService.CookieName]))
        {
            sqlAdmin.Touch();
            return Results.Ok();
        }

        // Sin pase válido: a Cenit (desde donde se lanza el explorador).
        return Results.Redirect(sqlAdmin.PortalToolsUrl);
    }

    private static IResult Enter(string? ticket, HttpResponse response, SqlAdminService sqlAdmin)
    {
        if (!sqlAdmin.ConsumeTicket(ticket))
        {
            return Results.Redirect(sqlAdmin.PortalToolsUrl);
        }

        response.Cookies.Append(SqlAdminService.CookieName, sqlAdmin.CreateCookie(), new CookieOptions
        {
            HttpOnly = true,
            Secure = sqlAdmin.PublicUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase),
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = sqlAdmin.CookieMaxAge
        });
        sqlAdmin.Touch();
        return Results.Redirect("/");
    }
}
