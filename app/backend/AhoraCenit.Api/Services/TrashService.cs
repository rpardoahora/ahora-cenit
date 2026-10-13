using System.Text.RegularExpressions;
using AhoraCenit.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AhoraCenit.Api.Services;

public class TrashOptions
{
    public const string SectionName = "Trash";

    /// <summary>Días que pasa una instancia o un cliente en la papelera antes de borrarse del todo (TRASH_RETENTION_DAYS).</summary>
    public int RetentionDays { get; set; } = 7;
}

/// <summary>
/// Papelera de instancias y clientes. Borrar solo los aparta (la instancia se para); pasados
/// <see cref="TrashOptions.RetentionDays"/> días se eliminan del todo: stack, volúmenes, bases del SQL
/// común, imágenes que nadie más use y, en los clientes, su login SQL.
/// </summary>
public interface ITrashService
{
    int RetentionDays { get; }

    /// <summary>Fecha en la que se borrará del todo algo que entró en la papelera en <paramref name="deletedAt"/>.</summary>
    DateTime PurgeDate(DateTime deletedAt);

    /// <summary>Para la instancia y la manda a la papelera. Devuelve el error de Portainer, si lo hubo.</summary>
    Task<string?> TrashApplicationAsync(Application application, CancellationToken ct = default);

    /// <summary>La saca de la papelera y la vuelve a arrancar.</summary>
    Task<string?> RestoreApplicationAsync(Application application, CancellationToken ct = default);

    /// <summary>Borra la instancia del todo (también si no estaba en la papelera).</summary>
    Task<string?> PurgeApplicationAsync(Application application, CancellationToken ct = default);

    Task TrashUserAsync(User user, CancellationToken ct = default);

    Task RestoreUserAsync(User user, CancellationToken ct = default);

    /// <summary>Borra el cliente del todo: sus instancias (estén o no en la papelera), su login SQL y el propio usuario.</summary>
    Task<string?> PurgeUserAsync(User user, CancellationToken ct = default);

    /// <summary>Borra del todo lo que lleva en la papelera más de <see cref="RetentionDays"/> días.</summary>
    Task<(int Applications, int Users)> PurgeExpiredAsync(CancellationToken ct = default);
}

public class TrashService(
    AppDbContext db,
    IPortainerClient portainer,
    ISharedSqlProvisioner sharedSql,
    IOptions<TrashOptions> options,
    ILogger<TrashService> logger) : ITrashService
{
    private static readonly Regex ImageLine = new(@"^\s+image\s*:\s*[""']?(?<image>[^""'\s#]+)", RegexOptions.Multiline);

    public int RetentionDays => Math.Max(0, options.Value.RetentionDays);

    public DateTime PurgeDate(DateTime deletedAt) => deletedAt.AddDays(RetentionDays);

    public async Task<string?> TrashApplicationAsync(Application application, CancellationToken ct = default)
    {
        if (application.PortainerStackId is not null)
        {
            var result = await portainer.StopStackAsync(application.PortainerStackId.Value, application.PortainerEndpointId, ct);
            // Un stack que ya estaba parado no es un error; cualquier otro fallo sí (la instancia seguiría en marcha).
            if (!result.Success && result.ErrorMessage?.Contains("already", StringComparison.OrdinalIgnoreCase) != true)
            {
                return result.ErrorMessage;
            }
        }

        application.DeletedAt = DateTime.UtcNow;
        application.Status = ApplicationStatus.Deleted;
        application.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<string?> RestoreApplicationAsync(Application application, CancellationToken ct = default)
    {
        if (application.PortainerStackId is not null)
        {
            var result = await portainer.StartStackAsync(application.PortainerStackId.Value, application.PortainerEndpointId, ct);
            if (!result.Success && result.ErrorMessage?.Contains("already", StringComparison.OrdinalIgnoreCase) != true)
            {
                return result.ErrorMessage;
            }
        }

        application.DeletedAt = null;
        application.Status = ApplicationStatus.Starting;
        application.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<string?> PurgeApplicationAsync(Application application, CancellationToken ct = default)
    {
        var user = application.User ?? await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == application.UserId, ct);

        if (application.PortainerStackId is not null)
        {
            // Las imágenes se leen del compose desplegado antes de borrar el stack.
            var images = ImagesOf(await portainer.GetStackFileAsync(application.PortainerStackId.Value, ct));
            var result = await portainer.DeleteStackAsync(application.PortainerStackId.Value, application.PortainerEndpointId, application.Subdomain, deleteVolumes: true, ct);
            if (!result.Success)
            {
                return result.ErrorMessage;
            }

            foreach (var image in images)
            {
                // Docker se niega a borrar una imagen que usa otro contenedor: en ese caso se queda.
                await portainer.DeleteImageAsync(application.PortainerEndpointId, image, ct);
            }
        }

        if (!string.IsNullOrEmpty(user?.SqlLogin))
        {
            try
            {
                await sharedSql.DropApplicationDatabasesAsync(user.SqlLogin, SharedSqlComposeRewriter.DatabasePrefix(application.Subdomain), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudieron borrar las bases de {Subdomain} en el SQL Server común", application.Subdomain);
            }
        }

        db.Applications.Remove(application);
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task TrashUserAsync(User user, CancellationToken ct = default)
    {
        user.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RestoreUserAsync(User user, CancellationToken ct = default)
    {
        user.DeletedAt = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> PurgeUserAsync(User user, CancellationToken ct = default)
    {
        var applications = await db.Applications.IgnoreQueryFilters().Where(a => a.UserId == user.Id).ToListAsync(ct);
        foreach (var application in applications)
        {
            application.User = user;
            var error = await PurgeApplicationAsync(application, ct);
            if (error is not null)
            {
                return $"No se pudo borrar la instancia {application.Subdomain}: {error}";
            }
        }

        if (!string.IsNullOrEmpty(user.SqlLogin))
        {
            try
            {
                await sharedSql.DropClientLoginAsync(user.SqlLogin, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo borrar el login SQL {Login} del cliente {ClientSlug}", user.SqlLogin, user.ClientSlug);
            }
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<(int Applications, int Users)> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        var applications = await db.Applications.IgnoreQueryFilters()
            .Include(a => a.User)
            .Where(a => a.DeletedAt != null && a.DeletedAt <= cutoff)
            .ToListAsync(ct);
        var purgedApplications = 0;
        foreach (var application in applications)
        {
            var error = await PurgeApplicationAsync(application, ct);
            if (error is null)
            {
                purgedApplications++;
            }
            else
            {
                logger.LogWarning("Papelera: no se pudo borrar la instancia {Subdomain}: {Error}", application.Subdomain, error);
            }
        }

        var users = await db.Users.IgnoreQueryFilters()
            .Where(u => u.DeletedAt != null && u.DeletedAt <= cutoff)
            .ToListAsync(ct);
        var purgedUsers = 0;
        foreach (var user in users)
        {
            var error = await PurgeUserAsync(user, ct);
            if (error is null)
            {
                purgedUsers++;
            }
            else
            {
                logger.LogWarning("Papelera: no se pudo borrar el cliente {ClientSlug}: {Error}", user.ClientSlug, error);
            }
        }

        return (purgedApplications, purgedUsers);
    }

    private static IReadOnlyList<string> ImagesOf(string? compose) =>
        compose is null
            ? []
            : ImageLine.Matches(compose).Select(m => m.Groups["image"].Value).Where(i => !i.Contains('$')).Distinct().ToList();
}

/// <summary>Tarea diaria (al arrancar y cada 24 h) que vacía de la papelera lo que ha cumplido su plazo.</summary>
public class TrashCleanupService(IServiceScopeFactory scopes, ILogger<TrashCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var trash = scope.ServiceProvider.GetRequiredService<ITrashService>();
                    var (applications, users) = await trash.PurgeExpiredAsync(stoppingToken);
                    logger.LogInformation("Papelera: limpieza diaria hecha ({Applications} instancias y {Users} clientes borrados del todo)", applications, users);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Papelera: la limpieza diaria ha fallado");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // El portal se está parando.
        }
    }
}
