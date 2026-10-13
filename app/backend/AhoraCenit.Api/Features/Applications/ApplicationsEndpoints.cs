using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using AhoraCenit.Api.Contracts.Applications;
using AhoraCenit.Api.Data;
using AhoraCenit.Api.Features.Products;
using AhoraCenit.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
        group.MapPost("/{id:guid}/restore", RestoreAsync);

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
        ISharedSqlProvisioner sharedSql,
        IOptions<AppsSqlOptions> appsSqlOptions,
        IPortalSettings portalSettings,
        IOptions<SmtpOptions> smtpOptions,
        ITrashService trashService,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Modo SQL de esta instancia: el de los ajustes; un administrador puede elegir otro.
        var sqlMode = await portalSettings.GetAppsSqlModeAsync(ct);
        if (!string.IsNullOrWhiteSpace(request.SqlMode))
        {
            if (!principal.IsAdmin())
            {
                return Results.Json(new { message = "Solo un administrador puede elegir el modo SQL al desplegar." }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (!Enum.TryParse<AppsSqlMode>(request.SqlMode, ignoreCase: true, out var requested) || !Enum.IsDefined(requested))
            {
                return Results.BadRequest(new { message = "sqlMode debe ser \"Dedicated\" o \"Shared\"." });
            }

            sqlMode = requested;
        }
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

        // Un nombre pedido expresamente que tiene una instancia en la papelera sigue ocupado hasta que se
        // borre del todo: se avisa en lugar de inventarse otro.
        if (!string.IsNullOrWhiteSpace(request.Subdomain))
        {
            var trashed = await db.Applications.IgnoreQueryFilters()
                .Where(a => a.Subdomain == requestedSlugSource && a.DeletedAt != null)
                .Select(a => a.DeletedAt)
                .FirstOrDefaultAsync(ct);
            if (trashed is not null)
            {
                return Results.Conflict(new
                {
                    message = $"El nombre \"{requestedSlugSource}\" está en uso: hay una instancia con ese nombre en la papelera " +
                              $"(se borrará del todo el {trashService.PurgeDate(trashed.Value):dd/MM/yyyy}). Elige otro nombre."
                });
            }
        }

        var subdomain = await SlugGenerator.ResolveUniqueAsync(
            requestedSlugSource,
            slug => db.Applications.IgnoreQueryFilters().AnyAsync(a => a.Subdomain == slug, ct));

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

        // BBDD compartida: las variables del SQL del compose (p. ej. la contraseña de sa) no se usan; no se
        // guardan con la instancia ni llegan a Portainer, así que tampoco se ven después.
        if (sqlMode == AppsSqlMode.Shared)
        {
            foreach (var key in SharedSqlComposeRewriter.SqlServiceVariables(product.ComposeTemplate))
            {
                mergedEnvVars.Remove(key);
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

        // Motor SQL común: se desactivan los SQL Server del compose y sus cadenas de
        // conexión pasan a usar el de la plataforma con el login del cliente.
        var composeContent = product.ComposeTemplate;
        SharedSqlRewriteResult? sqlRewrite = null;
        string? sqlError = null;
        if (sqlMode == AppsSqlMode.Shared)
        {
            var sqlOptions = appsSqlOptions.Value;
            var usesPlatformFeed = false;
            // Un producto en versión prerelease (imágenes x.y.z-beta) tiene también su dacpac en prerelease:
            // sin PreReleaseVersions, Flexygo no lo encuentra en el feed.
            var isPrerelease = PrereleaseImageTag.IsMatch(composeContent);
            var environment = new Dictionary<string, Func<string?, string?>>
            {
                // Instalación desatendida de Flexygo: crea o actualiza sus bases desplegando el dacpac
                // del paquete DatabaseNuget con las cadenas de conexión de arriba.
                ["Setup__Unattended"] = _ => "true",
                ["Setup__ProjectName"] = _ => subdomain,
                ["Setup__AdminPassword"] = _ => "${" + AppAdminPasswordVariable + "}",
                // El orden importa: la URL decide si van las credenciales del repositorio NuGet de la plataforma.
                ["DatabaseNuget__NugetUrl"] = current =>
                {
                    var internalUrl = string.IsNullOrWhiteSpace(current)
                        ? sqlOptions.NugetInternalBaseUrl.TrimEnd('/') + "/" + sqlOptions.NugetDefaultFeedPath.TrimStart('/')
                        : sqlOptions.ToInternalNugetUrl(current);
                    usesPlatformFeed = internalUrl is not null;
                    return internalUrl;
                },
                ["DatabaseNuget__PreReleaseVersions"] = current => string.IsNullOrWhiteSpace(current) && isPrerelease ? "true" : null,
                ["DatabaseNuget__NugetUser"] = _ => usesPlatformFeed ? "${" + NugetUserVariable + "}" : null,
                ["DatabaseNuget__NugetPassword"] = _ => usesPlatformFeed ? "${" + NugetPasswordVariable + "}" : null
            };

            sqlRewrite = SharedSqlComposeRewriter.Rewrite(
                composeContent,
                SharedSqlComposeRewriter.DatabasePrefix(subdomain),
                userValue: "${" + SharedSqlUserVariable + "}",
                passwordValue: "${" + SharedSqlPasswordVariable + "}",
                sqlOptions.Host,
                sqlOptions.Network,
                environment);

            if (sqlRewrite.Changed && string.IsNullOrWhiteSpace(request.AdminPassword))
            {
                sqlError = "Indica la contraseña inicial del administrador de la aplicación (adminPassword): en modo motor SQL común la aplicación se instala sola al arrancar.";
            }
            else if (sqlRewrite.Changed)
            {
                try
                {
                    await sharedSql.EnsureClientLoginAsync(user, CancellationToken.None);
                    await db.SaveChangesAsync(CancellationToken.None);
                    portainerEnvVars[SharedSqlUserVariable] = user.SqlLogin!;
                    portainerEnvVars[SharedSqlPasswordVariable] = user.SqlPassword!;
                    portainerEnvVars[AppAdminPasswordVariable] = request.AdminPassword!;
                    portainerEnvVars[NugetUserVariable] = sqlOptions.NugetUser;
                    portainerEnvVars[NugetPasswordVariable] = sqlOptions.NugetPassword;
                    composeContent = sqlRewrite.Compose;
                }
                catch (Exception ex)
                {
                    sqlError = $"No se pudo preparar el login SQL del cliente en el SQL Server común: {ex.Message}";
                }

                if (sqlRewrite.Warnings.Count > 0)
                {
                    loggerFactory.CreateLogger("AhoraCenit.Applications").LogWarning(
                        "Compose de {Product} para {Subdomain}: referencias al SQL no adaptadas al motor común: {Warnings}",
                        product.Name, subdomain, string.Join(" | ", sqlRewrite.Warnings));
                }
            }
        }

        // Flexygo hereda el SMTP de Cenit (si lo tiene): MailSettings__* en su backend. Se respeta el producto
        // que ya define su propio servidor de correo.
        var smtp = smtpOptions.Value;
        if (!string.IsNullOrWhiteSpace(smtp.Host))
        {
            var mail = new Dictionary<string, Func<string?, string?>>
            {
                ["MailSettings__Host"] = _ => smtp.Host.Trim(),
                ["MailSettings__Port"] = _ => smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["MailSettings__Ssl"] = _ => smtp.EnableSsl ? "true" : "false",
                ["MailSettings__UserName"] = _ => smtp.User,
                ["MailSettings__Password"] = _ => "${" + SmtpPasswordVariable + "}",
                ["MailSettings__From"] = _ => string.IsNullOrWhiteSpace(smtp.From) ? smtp.User : smtp.From
            };
            var (withMail, mailServices) = SharedSqlComposeRewriter.SetServiceEnvironment(composeContent, IsFlexygoBackendWithoutMail, mail);
            if (mailServices.Count > 0)
            {
                composeContent = withMail;
                portainerEnvVars[SmtpPasswordVariable] = smtp.Password;
            }
        }

        // Siempre (haya SMTP o no): el correo queda marcado como configurado para que Flexygo no lo pida al usuario.
        composeContent = SharedSqlComposeRewriter.SetServiceEnvironment(
            composeContent,
            IsFlexygoBackend,
            new Dictionary<string, Func<string?, string?>> { ["MailSettings__Configured"] = current => current == "true" ? null : "true" }).Compose;

        // A partir de aquí usamos CancellationToken.None a propósito: el despliegue en
        // Portainer (pull de imagen incluido) puede tardar más que la conexión HTTP del
        // cliente. Si se cancela con el ct de la petición, el stack se crea igualmente en
        // Portainer pero la aplicación queda huérfana en BBDD como "Deploying" para siempre.
        var result = sqlError is not null
            ? new PortainerCreateStackResult(false, null, application.PortainerEndpointId, sqlError)
            : await portainerClient.CreateStackAsync(
                stackName: subdomain,
                composeContent: composeContent,
                envVars: portainerEnvVars,
                ct: CancellationToken.None);

        if (!result.Success && sqlError is null)
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
            EnvVars = RedactEnvVars(mergedEnvVars, schema),
            SqlMode = sqlMode.ToString(),
            SharedSql = sqlRewrite is { Changed: true }
                ? new { sqlRewrite.DisabledServices, sqlRewrite.RewrittenServices, sqlRewrite.Databases, sqlRewrite.Warnings }
                : null
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

    /// <summary>
    /// Borrar manda la instancia a la papelera: se para y se borra del todo pasados los días de retención
    /// (TrashCleanupService). <paramref name="purge"/> (solo administradores) la borra del todo ya.
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        Guid id,
        bool? purge,
        ClaimsPrincipal principal,
        AppDbContext db,
        ITrashService trash,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Una que ya está en la papelera solo se puede borrar del todo (purge).
        var application = await db.Applications.IgnoreQueryFilters().Include(a => a.User).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (application is null || (application.DeletedAt is not null && purge != true))
        {
            return Results.NotFound();
        }

        if (!CanAccess(principal, application.UserId))
        {
            return Results.Forbid();
        }

        if (purge == true && !principal.IsAdmin())
        {
            return Results.Json(new { message = "Solo un administrador puede borrar una aplicación del todo." }, statusCode: StatusCodes.Status403Forbidden);
        }

        var snapshot = new { application.Subdomain, application.ProductId, application.UserId };
        var error = purge == true
            ? await trash.PurgeApplicationAsync(application, CancellationToken.None)
            : await trash.TrashApplicationAsync(application, CancellationToken.None);
        sw.Stop();

        var action = purge == true ? "Purge" : "Delete";
        if (error is not null)
        {
            await auditLogger.RecordAsync(principal, "Application", action, id, parameters: null, result: null,
                sw.Elapsed, success: false, errorMessage: error);
            return Results.Json(new { message = "No se pudo eliminar la aplicación en Portainer.", error }, statusCode: StatusCodes.Status502BadGateway);
        }

        await auditLogger.RecordAsync(principal, "Application", action, id,
            parameters: null,
            result: purge == true ? snapshot : new { snapshot.Subdomain, snapshot.ProductId, snapshot.UserId, PurgeDate = trash.PurgeDate(application.DeletedAt!.Value) },
            sw.Elapsed, success: true);
        return Results.NoContent();
    }

    /// <summary>Saca una instancia de la papelera y la vuelve a arrancar (solo administradores).</summary>
    private static async Task<IResult> RestoreAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        ITrashService trash,
        IAuditLogger auditLogger,
        IConfiguration configuration,
        CancellationToken ct)
    {
        if (!principal.IsAdmin())
        {
            return Results.Forbid();
        }

        var sw = Stopwatch.StartNew();
        var application = await db.Applications.IgnoreQueryFilters().Include(a => a.User).Include(a => a.Product)
            .FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt != null, ct);
        if (application is null)
        {
            return Results.NotFound();
        }

        if (application.User?.DeletedAt is not null)
        {
            return Results.Conflict(new { message = $"Su cliente ({application.User.Name}) también está en la papelera: restáuralo primero." });
        }

        var error = await trash.RestoreApplicationAsync(application, CancellationToken.None);
        sw.Stop();
        await auditLogger.RecordAsync(principal, "Application", "Restore", id, parameters: null,
            result: new { application.Subdomain }, sw.Elapsed, success: error is null, errorMessage: error);
        return error is null
            ? Results.Ok(ToResponse(application, configuration))
            : Results.Json(new { message = "No se pudo arrancar la aplicación en Portainer.", error }, statusCode: StatusCodes.Status502BadGateway);
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

    /// <summary>Variables que el portal pasa a Portainer con el login SQL del cliente (modo motor común).</summary>
    private const string SharedSqlUserVariable = "CENIT_SQL_USER";
    private const string SharedSqlPasswordVariable = "CENIT_SQL_PASSWORD";
    private const string AppAdminPasswordVariable = "CENIT_APP_ADMIN_PASSWORD";
    private const string SmtpPasswordVariable = "CENIT_SMTP_PASSWORD";

    /// <summary>Backend de Flexygo: el servicio con la base de configuración.</summary>
    private static bool IsFlexygoBackend(IReadOnlyList<string> serviceLines) =>
        serviceLines.Any(l => l.Contains("ConnectionStrings__ConfConnectionString", StringComparison.Ordinal));

    /// <summary>Backend de Flexygo que no define ya su propio servidor de correo.</summary>
    private static bool IsFlexygoBackendWithoutMail(IReadOnlyList<string> serviceLines) =>
        IsFlexygoBackend(serviceLines)
        && !serviceLines.Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"MailSettings__Host\s*[:=]\s*[""']?[^\s""'#]"));

    /// <summary>Alguna imagen (sin comentar) con un tag de versión prerelease, p. ej. "registry/app:1.0.5.1-beta".</summary>
    private static readonly System.Text.RegularExpressions.Regex PrereleaseImageTag = new(
        @"^[ \t]+image[ \t]*:[ \t]*[""']?[^""'\s#]+:v?\d+(\.\d+)*-[0-9A-Za-z.-]+[""']?[ \t]*$",
        System.Text.RegularExpressions.RegexOptions.Multiline);
    private const string NugetUserVariable = "CENIT_NUGET_USER";
    private const string NugetPasswordVariable = "CENIT_NUGET_PASSWORD";

    private static string GetBaseDomain(IConfiguration configuration) =>
        configuration["BaseDomain"] ?? "ahoracenit.localhost";

    /// <summary>
    /// URL pública de la aplicación con el esquema y el puerto del portal
    /// (p. ej. http://app.ahoracenit.localhost:8880 en local, https://app.midominio.com en producción).
    /// </summary>
    internal static string BuildPublicUrl(string fullDomain, IConfiguration configuration)
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
