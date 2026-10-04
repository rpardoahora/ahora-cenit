using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using AhoraCenit.Api.Contracts.Applications;
using AhoraCenit.Api.Data;
using AhoraCenit.Api.Features.Products;
using AhoraCenit.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AhoraCenit.Api.Features.Applications;

public static class ApplicationsEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapApplicationsEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization();

        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/usage", GetUsageSummaryAsync).RequireAuthorization("AdminOnly");
        group.MapGet("/{id:guid}", GetByIdAsync);
        group.MapGet("/{id:guid}/status", RefreshStatusAsync);
        group.MapPost("/{id:guid}/stop", StopAsync);
        group.MapPost("/{id:guid}/start", StartAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        return group;
    }

    private static bool CanAccess(ClaimsPrincipal principal, Guid ownerUserId) =>
        principal.IsAdmin() || principal.GetUserId() == ownerUserId;

    private static async Task<IResult> CreateAsync(
        CreateApplicationRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        IPortainerClient portainerClient,
        IConfiguration configuration,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var userId = principal.GetUserId();

        // Un admin (o el token de API) puede desplegar en nombre de un cliente.
        var onBehalfOfClient = request.UserId is { } ownerId && ownerId != userId;
        if (onBehalfOfClient)
        {
            if (!principal.IsAdmin())
            {
                return Results.Forbid();
            }

            userId = request.UserId!.Value;
        }

        var user = await db.Users.FindAsync([userId], ct);
        if (user is null)
        {
            return onBehalfOfClient
                ? Results.BadRequest(new { message = "El cliente indicado (userId) no existe." })
                : Results.Unauthorized();
        }

        var product = await db.Products.FindAsync([request.ProductId], ct);
        if (product is null || !product.IsActive)
        {
            return Results.BadRequest(new { message = "El producto no existe o no está activo." });
        }

        // Resolve the subdomain slug: user-provided if present, otherwise the
        // default combination of product name + client name.
        var requestedSlugSource = string.IsNullOrWhiteSpace(request.Subdomain)
            ? SlugGenerator.Slugify(product.Name, user.Name)
            : SlugGenerator.Slugify(request.Subdomain);

        if (string.IsNullOrEmpty(requestedSlugSource))
        {
            requestedSlugSource = SlugGenerator.Slugify(product.Name, user.ClientSlug);
        }

        var subdomain = await SlugGenerator.ResolveUniqueAsync(
            requestedSlugSource,
            slug => db.Applications.AnyAsync(a => a.Subdomain == slug, ct));

        // Merge product schema defaults with the user's overrides. Las variables de solo
        // lectura u ocultas ignoran cualquier valor recibido: su valor siempre es el
        // definido por el producto, aunque el cliente intente forzarlo por API.
        var schema = ProductsEndpoints.DeserializeSchema(product.EnvVarsSchemaJson);
        var mergedEnvVars = schema.ToDictionary(d => d.Key, d => d.DefaultValue);

        if (request.EnvVars is not null)
        {
            var lockedKeys = schema
                .Where(d => d.Mode is EnvVarInputMode.ReadOnly or EnvVarInputMode.Hidden)
                .Select(d => d.Key)
                .ToHashSet();

            foreach (var (key, value) in request.EnvVars)
            {
                if (lockedKeys.Contains(key))
                {
                    continue;
                }

                mergedEnvVars[key] = value;
            }
        }

        var application = new Application
        {
            ProductId = product.Id,
            UserId = user.Id,
            Subdomain = subdomain,
            EnvVarValuesJson = JsonSerializer.Serialize(mergedEnvVars, JsonOptions),
            Status = ApplicationStatus.Deploying,
            PortainerEndpointId = configuration.GetValue<int>("Portainer:EndpointId", 1),
            Product = product,
            User = user
        };

        db.Applications.Add(application);
        await db.SaveChangesAsync(ct);

        var baseDomain = configuration["BaseDomain"] ?? "ahoracenit.localhost";

        var portainerEnvVars = new Dictionary<string, string>(mergedEnvVars)
        {
            ["APP_SUBDOMAIN"] = subdomain,
            ["BASE_DOMAIN"] = baseDomain
        };

        // A partir de aquí usamos CancellationToken.None a propósito: el despliegue en
        // Portainer (pull de imagen incluido) puede tardar más que la conexión HTTP del
        // cliente. Si se cancela con el ct de la petición, el stack se crea igualmente en
        // Portainer pero la aplicación queda huérfana en BBDD como "Deploying" para siempre.
        var result = await portainerClient.CreateStackAsync(
            stackName: subdomain,
            composeContent: product.ComposeTemplate,
            envVars: portainerEnvVars,
            ct: CancellationToken.None);

        if (!result.Success)
        {
            // Nuestra llamada HTTP a Portainer puede hacer timeout (p.ej. una imagen grande
            // tardando en descargarse) aunque el "docker compose up" siga corriendo ahí y
            // termine creando el stack igualmente. Antes de dar el despliegue por fallido,
            // comprobamos si el stack ya existe en Portainer con ese nombre.
            var recoveredStackId = await portainerClient.FindStackIdByNameAsync(subdomain, CancellationToken.None);
            if (recoveredStackId is not null)
            {
                result = result with { Success = true, StackId = recoveredStackId, ErrorMessage = null };
            }
        }

        var auditParameters = new
        {
            product.Id,
            product.Name,
            application.Subdomain,
            EnvVars = RedactEnvVars(mergedEnvVars, schema)
        };

        if (result.Success)
        {
            application.PortainerStackId = result.StackId;
            application.PortainerEndpointId = result.EndpointId;
            // Recién creado: el certificado TLS (si aplica) nunca está listo todavía,
            // así que no merece la pena comprobarlo por red aquí; lo hará el primer refresh.
            application.Status = TlsStatus.RequiresCertificateCheck(baseDomain)
                ? ApplicationStatus.Provisioning
                : ApplicationStatus.Running;
            application.UpdatedAt = DateTime.UtcNow;
            sw.Stop();
            db.DeploymentDurationSamples.Add(new DeploymentDurationSample
            {
                ProductId = product.Id,
                DurationSeconds = sw.Elapsed.TotalSeconds
            });
            await db.SaveChangesAsync(CancellationToken.None);

            await auditLogger.RecordAsync(
                principal, "Application", "Create", application.Id,
                auditParameters,
                result: new { application.Id, application.Subdomain, application.PortainerStackId },
                sw.Elapsed, success: true);

            return Results.Created($"/api/applications/{application.Id}", ToResponse(application, configuration));
        }

        application.Status = ApplicationStatus.Error;
        application.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        sw.Stop();

        await auditLogger.RecordAsync(
            principal, "Application", "Create", application.Id,
            auditParameters,
            result: null,
            sw.Elapsed, success: false, errorMessage: result.ErrorMessage);

        return Results.Json(
            new
            {
                message = "No se pudo desplegar la aplicación en Portainer.",
                error = result.ErrorMessage,
                application = ToResponse(application, configuration)
            },
            statusCode: StatusCodes.Status502BadGateway);
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        IConfiguration configuration,
        Guid? userId,
        string? clientSlug,
        CancellationToken ct)
    {
        var query = db.Applications.Include(a => a.Product).Include(a => a.User).AsQueryable();

        if (principal.IsAdmin())
        {
            if (userId.HasValue)
            {
                query = query.Where(a => a.UserId == userId.Value);
            }

            if (!string.IsNullOrWhiteSpace(clientSlug))
            {
                query = query.Where(a => a.User!.ClientSlug == clientSlug);
            }
        }
        else
        {
            var currentUserId = principal.GetUserId();
            query = query.Where(a => a.UserId == currentUserId);
        }

        var baseDomain = GetBaseDomain(configuration);
        var applications = await query.OrderByDescending(a => a.CreatedAt).ToListAsync(ct);
        return Results.Ok(applications.Select(a => ToResponse(a, configuration)));
    }

    private static async Task<IResult> GetUsageSummaryAsync(
        AppDbContext db,
        IPortainerClient portainerClient,
        CancellationToken ct)
    {
        var applications = await db.Applications
            .Include(a => a.Product)
            .Include(a => a.User)
            .Where(a => a.PortainerStackId != null)
            .ToListAsync(ct);

        var results = await Task.WhenAll(applications.Select(async a =>
        {
            var usage = await portainerClient.GetStackResourceUsageAsync(a.PortainerEndpointId, a.Subdomain, ct);
            return new ApplicationUsageResponse(
                a.Id,
                a.Subdomain,
                a.Product?.Name ?? string.Empty,
                a.UserId,
                a.User?.Name ?? string.Empty,
                a.User?.ClientSlug ?? string.Empty,
                usage.CpuPercent,
                usage.MemoryUsageBytes,
                usage.DiskUsageBytes,
                usage.ContainerCount);
        }));

        return Results.Ok(results);
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var application = await db.Applications
            .Include(a => a.Product)
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        if (application is null)
        {
            return Results.NotFound();
        }

        if (!CanAccess(principal, application.UserId))
        {
            return Results.Forbid();
        }

        return Results.Ok(ToResponse(application, configuration));
    }

    private static async Task<IResult> RefreshStatusAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        IPortainerClient portainerClient,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var application = await db.Applications
            .Include(a => a.Product)
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        if (application is null)
        {
            return Results.NotFound();
        }

        if (!CanAccess(principal, application.UserId))
        {
            return Results.Forbid();
        }

        var baseDomain = GetBaseDomain(configuration);

        if (application.PortainerStackId is null)
        {
            // El despliegue original puede haber marcado error por un timeout hablando con
            // Portainer aunque el stack se creara igualmente al otro lado. Antes de devolver
            // el estado guardado sin más, intentamos localizar el stack por nombre.
            var recoveredStackId = await portainerClient.FindStackIdByNameAsync(application.Subdomain, ct);
            if (recoveredStackId is null)
            {
                return Results.Ok(ToResponse(application, configuration));
            }

            application.PortainerStackId = recoveredStackId;
            application.PortainerEndpointId = configuration.GetValue<int>("Portainer:EndpointId", 1);
        }

        var status = await portainerClient.GetStackStatusAsync(
            application.PortainerStackId.Value,
            application.PortainerEndpointId,
            application.Subdomain,
            ct);

        if (status is null)
        {
            // No se pudo determinar el estado real (fallo transitorio de conectividad con
            // Portainer): mantenemos el último estado conocido en lugar de asumir un error.
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(application, configuration));
        }

        if (status == ApplicationStatus.Running)
        {
            status = await ResolveRunningStatusAsync(application.Subdomain, baseDomain, ct);
        }

        application.Status = status.Value;
        application.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(application, configuration));
    }

    /// <summary>
    /// El contenedor está corriendo según Docker; esto afina ese estado comprobando si
    /// Traefik ya sirve el certificado TLS real del subdominio (cuando aplica).
    /// </summary>
    private static async Task<ApplicationStatus> ResolveRunningStatusAsync(string subdomain, string baseDomain, CancellationToken ct)
    {
        if (!TlsStatus.RequiresCertificateCheck(baseDomain))
        {
            return ApplicationStatus.Running;
        }

        var certReady = await TlsStatus.IsCertificateReadyAsync($"{subdomain}.{baseDomain}", ct);
        return certReady ? ApplicationStatus.Running : ApplicationStatus.Provisioning;
    }

    private static async Task<IResult> StopAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        IPortainerClient portainerClient,
        IConfiguration configuration,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var application = await db.Applications
            .Include(a => a.Product)
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        if (application is null)
        {
            return Results.NotFound();
        }

        if (!CanAccess(principal, application.UserId))
        {
            return Results.Forbid();
        }

        if (application.PortainerStackId is null)
        {
            return Results.BadRequest(new { message = "La aplicación no tiene un stack asociado en Portainer." });
        }

        var result = await portainerClient.StopStackAsync(application.PortainerStackId.Value, application.PortainerEndpointId, ct);
        sw.Stop();
        if (!result.Success)
        {
            await auditLogger.RecordAsync(
                principal, "Application", "Stop", application.Id, parameters: null, result: null,
                sw.Elapsed, success: false, errorMessage: result.ErrorMessage);

            return StackActionFailed("No se pudo parar la aplicación.", result);
        }

        application.Status = ApplicationStatus.Stopped;
        application.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await auditLogger.RecordAsync(
            principal, "Application", "Stop", application.Id,
            parameters: null, result: new { application.Subdomain },
            sw.Elapsed, success: true);

        return Results.Ok(ToResponse(application, configuration));
    }

    private static async Task<IResult> StartAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        IPortainerClient portainerClient,
        IConfiguration configuration,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var application = await db.Applications
            .Include(a => a.Product)
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

        if (application is null)
        {
            return Results.NotFound();
        }

        if (!CanAccess(principal, application.UserId))
        {
            return Results.Forbid();
        }

        if (application.PortainerStackId is null)
        {
            return Results.BadRequest(new { message = "La aplicación no tiene un stack asociado en Portainer." });
        }

        var result = await portainerClient.StartStackAsync(application.PortainerStackId.Value, application.PortainerEndpointId, ct);
        sw.Stop();
        if (!result.Success)
        {
            await auditLogger.RecordAsync(
                principal, "Application", "Start", application.Id, parameters: null, result: null,
                sw.Elapsed, success: false, errorMessage: result.ErrorMessage);

            return StackActionFailed("No se pudo iniciar la aplicación.", result);
        }

        application.Status = await ResolveRunningStatusAsync(application.Subdomain, GetBaseDomain(configuration), ct);
        application.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await auditLogger.RecordAsync(
            principal, "Application", "Start", application.Id,
            parameters: null, result: new { application.Subdomain },
            sw.Elapsed, success: true);

        return Results.Ok(ToResponse(application, configuration));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        IPortainerClient portainerClient,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var application = await db.Applications.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (application is null)
        {
            return Results.NotFound();
        }

        if (!CanAccess(principal, application.UserId))
        {
            return Results.Forbid();
        }

        if (application.PortainerStackId is not null)
        {
            var result = await portainerClient.DeleteStackAsync(application.PortainerStackId.Value, application.PortainerEndpointId, application.Subdomain, ct);
            if (!result.Success)
            {
                sw.Stop();
                await auditLogger.RecordAsync(
                    principal, "Application", "Delete", application.Id, parameters: null, result: null,
                    sw.Elapsed, success: false, errorMessage: result.ErrorMessage);

                return Results.Json(new { message = "No se pudo borrar el stack en Portainer.", error = result.ErrorMessage }, statusCode: StatusCodes.Status502BadGateway);
            }
        }

        var deletedSnapshot = new { application.Subdomain, application.ProductId, application.UserId };

        db.Applications.Remove(application);
        await db.SaveChangesAsync(ct);
        sw.Stop();

        await auditLogger.RecordAsync(
            principal, "Application", "Delete", id,
            parameters: null, result: deletedSnapshot,
            sw.Elapsed, success: true);

        return Results.NoContent();
    }

    /// <summary>Sustituye los valores de las env vars marcadas como secretas u ocultas en el schema del producto antes de auditarlas/loguearlas.</summary>
    private static Dictionary<string, string> RedactEnvVars(
        IReadOnlyDictionary<string, string> envVars,
        List<EnvVarDefinition> schema)
    {
        var maskedKeys = schema
            .Where(d => d.Mode is EnvVarInputMode.Secret or EnvVarInputMode.Hidden)
            .Select(d => d.Key)
            .ToHashSet();
        return envVars.ToDictionary(kv => kv.Key, kv => maskedKeys.Contains(kv.Key) ? "***" : kv.Value);
    }

    /// <summary>409 si Portainer sigue desplegando el stack (se puede reintentar); 502 en cualquier otro fallo.</summary>
    private static IResult StackActionFailed(string message, PortainerOperationResult result) =>
        result.Busy
            ? Results.Json(
                new { message = "La aplicación todavía se está desplegando. Inténtalo de nuevo en unos segundos.", error = result.ErrorMessage },
                statusCode: StatusCodes.Status409Conflict)
            : Results.Json(new { message, error = result.ErrorMessage }, statusCode: StatusCodes.Status502BadGateway);

    private static string GetBaseDomain(IConfiguration configuration) =>
        configuration["BaseDomain"] ?? "ahoracenit.localhost";

    /// <summary>
    /// URL pública de la aplicación con el esquema y el puerto del portal
    /// (p. ej. http://app.ahoracenit.localhost:8880 en local, https://app.midominio.com en producción).
    /// </summary>
    private static string BuildPublicUrl(string fullDomain, IConfiguration configuration)
    {
        var scheme = "https";
        var portSuffix = string.Empty;
        if (Uri.TryCreate(configuration[$"{AuthOptions.SectionName}:PublicBaseUrl"], UriKind.Absolute, out var publicUri))
        {
            scheme = publicUri.Scheme;
            portSuffix = publicUri.IsDefaultPort ? string.Empty : $":{publicUri.Port}";
        }

        return $"{scheme}://{fullDomain}{portSuffix}";
    }

    private static ApplicationResponse ToResponse(Application application, IConfiguration configuration)
    {
        var fullDomain = $"{application.Subdomain}.{GetBaseDomain(configuration)}";
        var envVars = string.IsNullOrWhiteSpace(application.EnvVarValuesJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(application.EnvVarValuesJson, JsonOptions) ?? new();

        return new ApplicationResponse(
            application.Id,
            application.ProductId,
            application.Product?.Name ?? string.Empty,
            application.UserId,
            application.User?.Name ?? string.Empty,
            application.User?.ClientSlug ?? string.Empty,
            application.Subdomain,
            fullDomain,
            BuildPublicUrl(fullDomain, configuration),
            envVars,
            application.Status.ToString(),
            application.PortainerStackId,
            application.PortainerEndpointId,
            application.CreatedAt,
            application.UpdatedAt);
    }
}
