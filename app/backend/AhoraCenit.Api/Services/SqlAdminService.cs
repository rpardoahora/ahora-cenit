using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AhoraCenit.Api.Services;

/// <summary>
/// Explorador de bases de datos (CloudBeaver) bajo demanda: apagado por defecto, lo arranca el portal al pulsar
/// "Explorar BBDD", conectado ya a la base elegida y sin pedir credenciales (acceso anónimo detrás de un pase del
/// portal), y se para solo tras <see cref="IdleMinutes"/> minutos sin actividad.
/// </summary>
public class SqlAdminOptions
{
    public const string SectionName = "SqlAdmin";

    public string Image { get; set; } = "dbeaver/cloudbeaver:26.2.2";

    public string ContainerName { get; set; } = "cenit-sqladmin";

    /// <summary>Red a la que se unen el explorador y los SQL Server propios de las instancias que se exploran.</summary>
    public string Network { get; set; } = "cenit_sqladmin";

    /// <summary>Red del SQL Server de la plataforma (alias cenit-sqlserver).</summary>
    public string PlatformSqlNetwork { get; set; } = "cenit_sql";

    public string PlatformSqlHost { get; set; } = "cenit-sqlserver";

    /// <summary>Red pública de Traefik.</summary>
    public string ProxyNetwork { get; set; } = "proxy";

    /// <summary>Volumen con el espacio de trabajo de CloudBeaver (configuración, scripts guardados...).</summary>
    public string Volume { get; set; } = "cenit_sqladmin_data";

    /// <summary>Dirección interna del portal para el forwardAuth de Traefik.</summary>
    public string PortalInternalUrl { get; set; } = "http://cenit-portal:8080";

    public int IdleMinutes { get; set; } = 5;
}

/// <summary>Base de datos a la que se conecta el explorador.</summary>
/// <param name="Key">Identifica el destino ("platform", "app:{id}"): si cambia, se reconfigura el explorador.</param>
/// <param name="AttachContainerId">Contenedor SQL propio de una instancia que hay que unir a la red del explorador.</param>
public sealed record SqlAdminTarget(
    string Key,
    string Name,
    string Host,
    int Port,
    string User,
    string Password,
    string Database,
    string? AttachContainerId = null,
    string? AttachAlias = null);

public sealed record SqlAdminLaunch(string State, string Message, string? LaunchUrl = null);

public class SqlAdminService(
    IHttpClientFactory httpFactory,
    IOptions<PortainerOptions> portainerOptions,
    IOptions<SqlAdminOptions> options,
    IOptions<AuthOptions> authOptions,
    IConfiguration configuration,
    ILogger<SqlAdminService> logger)
{
    public const string CookieName = "cenit_sqladmin";
    private const string Workspace = "/opt/cloudbeaver/workspace";
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromHours(8);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SqlAdminLaunch> _launches = new();
    private readonly ConcurrentDictionary<string, DateTime> _tickets = new();
    private string? _configuredTarget;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    public SqlAdminOptions Options => options.Value;

    public DateTime LastActivity => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    /// <summary>Cualquier petición al explorador (pasa por el forwardAuth) cuenta como actividad.</summary>
    public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    /// <summary>URL pública del explorador: {esquema}://sqladmin.{dominio}{:puerto}.</summary>
    public string PublicUrl
    {
        get
        {
            var baseDomain = configuration["BaseDomain"] ?? "ahoracenit.localhost";
            var scheme = "http";
            var port = string.Empty;
            if (Uri.TryCreate(authOptions.Value.PublicBaseUrl, UriKind.Absolute, out var portal))
            {
                scheme = portal.Scheme;
                port = portal.IsDefaultPort ? string.Empty : $":{portal.Port}";
            }

            return $"{scheme}://sqladmin.{baseDomain}{port}";
        }
    }

    /// <summary>Adonde se manda a quien llega sin pase o con el explorador apagado.</summary>
    public string PortalToolsUrl => $"{authOptions.Value.PublicBaseUrl.TrimEnd('/')}/admin/herramientas";

    /// <summary>Empieza a preparar el explorador para <paramref name="target"/>; el progreso se consulta con <see cref="GetLaunch"/>.</summary>
    public Guid StartLaunch(SqlAdminTarget target)
    {
        var id = Guid.NewGuid();
        _launches[id] = new SqlAdminLaunch("starting", "Preparando el explorador de bases de datos...");
        _ = Task.Run(async () =>
        {
            try
            {
                await PrepareAsync(target, message => _launches[id] = new SqlAdminLaunch("starting", message));
                var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
                _tickets[ticket] = DateTime.UtcNow + TicketLifetime;
                _launches[id] = new SqlAdminLaunch("ready", $"Conectado a {target.Name}.", $"{PublicUrl}/__cenit/enter?ticket={ticket}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo preparar el explorador de bases de datos para {Target}", target.Key);
                _launches[id] = new SqlAdminLaunch("error", ex.Message);
            }
        });
        return id;
    }

    public SqlAdminLaunch? GetLaunch(Guid id) => _launches.TryGetValue(id, out var launch) ? launch : null;

    /// <summary>Canjea un pase (de un solo uso, caduca en 2 minutos).</summary>
    public bool ConsumeTicket(string? ticket) =>
        !string.IsNullOrEmpty(ticket) && _tickets.TryRemove(ticket, out var expires) && expires > DateTime.UtcNow;

    /// <summary>Valor de la cookie del explorador: caducidad firmada con el secreto JWT del portal.</summary>
    public string CreateCookie()
    {
        var expires = DateTimeOffset.UtcNow.Add(CookieLifetime).ToUnixTimeSeconds().ToString();
        return $"{expires}.{Sign(expires)}";
    }

    public bool ValidateCookie(string? value)
    {
        var parts = value?.Split('.');
        if (parts is not { Length: 2 } || !long.TryParse(parts[0], out var expires) || expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(parts[0])), Encoding.ASCII.GetBytes(parts[1]));
    }

    public TimeSpan CookieMaxAge => CookieLifetime;

    /// <summary>Para el explorador si lleva <see cref="SqlAdminOptions.IdleMinutes"/> minutos sin actividad.</summary>
    public async Task StopIfIdleAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - LastActivity < TimeSpan.FromMinutes(Math.Max(1, Options.IdleMinutes)) || !await _lock.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            var container = await InspectContainerAsync(ct);
            if (container is { Running: true })
            {
                await DockerAsync(HttpMethod.Post, $"containers/{container.Id}/stop?t=10", null, ct, HttpStatusCode.NotModified);
                logger.LogInformation("Explorador de bases de datos parado tras {Minutes} minutos sin actividad", Options.IdleMinutes);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task PrepareAsync(SqlAdminTarget target, Action<string> progress)
    {
        var ct = CancellationToken.None;
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureNetworkAsync(Options.Network, ct);

            if (!await ExistsAsync($"images/{ImagePath(Options.Image)}/json", ct))
            {
                progress("Descargando el explorador de bases de datos (solo la primera vez, puede tardar unos minutos)...");
                var (name, tag) = SplitImage(Options.Image);
                await DockerAsync(HttpMethod.Post, $"images/create?fromImage={Uri.EscapeDataString(name)}&tag={Uri.EscapeDataString(tag)}", null, ct);
            }

            var container = await InspectContainerAsync(ct);
            if (container is null)
            {
                progress("Creando el explorador de bases de datos...");
                await CreateContainerAsync(ct);
                container = await InspectContainerAsync(ct) ?? throw new InvalidOperationException("Docker no ha creado el contenedor del explorador.");
                _configuredTarget = null;
            }

            await ConnectAsync(Options.PlatformSqlNetwork, container.Id, null, ct);
            await ConnectAsync(Options.Network, container.Id, null, ct);
            if (target.AttachContainerId is not null)
            {
                await ConnectAsync(Options.Network, target.AttachContainerId, target.AttachAlias, ct);
            }

            // Otra base de datos (o primer arranque tras reiniciar el portal): se reescribe su conexión y se
            // reinicia, porque CloudBeaver solo lee las conexiones globales al arrancar.
            if (_configuredTarget != target.Key || !container.Running)
            {
                if (container.Running)
                {
                    progress("Cambiando de base de datos...");
                    await DockerAsync(HttpMethod.Post, $"containers/{container.Id}/stop?t=10", null, ct, HttpStatusCode.NotModified);
                }

                await UploadConnectionAsync(container.Id, target, ct);
                progress($"Arrancando el explorador y conectando con {target.Name}...");
                await DockerAsync(HttpMethod.Post, $"containers/{container.Id}/start", null, ct, HttpStatusCode.NotModified);
                _configuredTarget = target.Key;
            }

            await WaitUntilReadyAsync(ct);
            Touch();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task CreateContainerAsync(CancellationToken ct)
    {
        var host = new Uri(PublicUrl).Host;
        var labels = new Dictionary<string, string>
        {
            ["cenit.role"] = "sqladmin",
            ["traefik.enable"] = "true",
            ["traefik.docker.network"] = Options.ProxyNetwork,
            ["traefik.http.routers.sqladmin.rule"] = $"Host(`{host}`)",
            ["traefik.http.routers.sqladmin.priority"] = "100",
            ["traefik.http.routers.sqladmin.service"] = "sqladmin",
            ["traefik.http.routers.sqladmin.middlewares"] = "sqladmin-auth",
            ["traefik.http.services.sqladmin.loadbalancer.server.port"] = "8978",
            // Cada petición pasa antes por el portal: sin la cookie del pase, fuera (y cuenta como actividad).
            ["traefik.http.middlewares.sqladmin-auth.forwardauth.address"] = $"{Options.PortalInternalUrl.TrimEnd('/')}/api/sqladmin/auth"
        };
        var body = new
        {
            Image = Options.Image,
            Env = new[]
            {
                "CB_SERVER_NAME=Cenit",
                $"CB_SERVER_URL={PublicUrl}",
                "CB_ADMIN_NAME=cbadmin",
                // Nadie entra con él: el acceso es anónimo detrás del pase del portal.
                $"CB_ADMIN_PASSWORD=Cb{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}x",
                "CLOUDBEAVER_APP_ANONYMOUS_ACCESS_ENABLED=true",
                "CLOUDBEAVER_APP_GRANT_CONNECTIONS_ACCESS_TO_ANONYMOUS_TEAM=true",
                "CLOUDBEAVER_APP_SUPPORTS_CUSTOM_CONNECTIONS=false"
            },
            Labels = labels,
            HostConfig = new
            {
                Binds = new[] { $"{Options.Volume}:{Workspace}" },
                NetworkMode = Options.ProxyNetwork,
                RestartPolicy = new { Name = "no" }
            }
        };
        await DockerAsync(HttpMethod.Post, $"containers/create?name={Uri.EscapeDataString(Options.ContainerName)}", JsonContent.Create(body), ct);
    }

    /// <summary>Deja en el espacio de trabajo una única conexión global: la de la base elegida.</summary>
    private async Task UploadConnectionAsync(string containerId, SqlAdminTarget target, CancellationToken ct)
    {
        var dataSources = new
        {
            folders = new { },
            connections = new Dictionary<string, object>
            {
                ["cenit-target"] = new Dictionary<string, object>
                {
                    ["provider"] = "sqlserver",
                    ["driver"] = "microsoft",
                    ["name"] = target.Name,
                    ["save-password"] = true,
                    ["read-only"] = false,
                    ["configuration"] = new Dictionary<string, object>
                    {
                        ["host"] = target.Host,
                        ["port"] = target.Port.ToString(),
                        ["database"] = target.Database,
                        ["url"] = $"jdbc:sqlserver://{target.Host}:{target.Port};databaseName={target.Database};encrypt=false;trustServerCertificate=true",
                        ["configurationType"] = "MANUAL",
                        ["type"] = "dev",
                        ["auth-model"] = "native",
                        ["user"] = target.User,
                        ["password"] = target.Password,
                        ["provider-properties"] = new Dictionary<string, string> { ["@dbeaver-show-all-schemas@"] = "true" }
                    }
                }
            }
        };

        using var tar = new MemoryStream();
        await using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, ".metadata/"), ct);
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, "GlobalConfiguration/"), ct);
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, "GlobalConfiguration/.dbeaver/"), ct);
            var file = new PaxTarEntry(TarEntryType.RegularFile, "GlobalConfiguration/.dbeaver/data-sources.json")
            {
                DataStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(dataSources))
            };
            await writer.WriteEntryAsync(file, ct);
        }

        var content = new ByteArrayContent(tar.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");
        await DockerAsync(HttpMethod.Put, $"containers/{containerId}/archive?path={Uri.EscapeDataString(Workspace)}", content, ct);
    }

    private async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync($"http://{Options.ContainerName}:8978/", ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Todavía arrancando.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new TimeoutException("El explorador de bases de datos no ha respondido en 3 minutos.");
    }

    // ---------------------------------------------------------------- Docker (a través de Portainer)

    private sealed record ContainerInfo(string Id, bool Running);

    private async Task<ContainerInfo?> InspectContainerAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"containers/{Uri.EscapeDataString(Options.ContainerName)}/json", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return new ContainerInfo(
            json.RootElement.GetProperty("Id").GetString()!,
            json.RootElement.GetProperty("State").GetProperty("Running").GetBoolean());
    }

    private async Task EnsureNetworkAsync(string name, CancellationToken ct)
    {
        if (!await ExistsAsync($"networks/{Uri.EscapeDataString(name)}", ct))
        {
            await DockerAsync(HttpMethod.Post, "networks/create", JsonContent.Create(new { Name = name, Driver = "bridge", Attachable = true }), ct);
        }
    }

    /// <summary>Une un contenedor a una red; si ya estaba unido, no hace nada.</summary>
    private async Task ConnectAsync(string network, string containerId, string? alias, CancellationToken ct)
    {
        var body = alias is null
            ? (object)new { Container = containerId }
            : new { Container = containerId, EndpointConfig = new { Aliases = new[] { alias } } };
        using var response = await SendAsync(HttpMethod.Post, $"networks/{Uri.EscapeDataString(network)}/connect", JsonContent.Create(body), ct);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        if (text.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException($"Docker no pudo unir el contenedor a la red {network}: {text}");
    }

    /// <summary>Contenedores de un stack de compose con su imagen y sus variables de entorno.</summary>
    public async Task<IReadOnlyList<(string Id, string Name, string Image, IReadOnlyList<string> Env, bool Running)>> ListStackContainersAsync(string stack, CancellationToken ct)
    {
        var filters = JsonSerializer.Serialize(new Dictionary<string, string[]> { ["label"] = [$"com.docker.compose.project={stack}"] });
        using var response = await SendAsync(HttpMethod.Get, $"containers/json?all=true&filters={Uri.EscapeDataString(filters)}", null, ct);
        await EnsureSuccessAsync(response, ct);
        using var list = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var result = new List<(string, string, string, IReadOnlyList<string>, bool)>();
        foreach (var item in list.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("Id").GetString()!;
            using var inspect = await SendAsync(HttpMethod.Get, $"containers/{id}/json", null, ct);
            await EnsureSuccessAsync(inspect, ct);
            using var detail = JsonDocument.Parse(await inspect.Content.ReadAsStringAsync(ct));
            var env = detail.RootElement.GetProperty("Config").GetProperty("Env").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
            result.Add((id,
                detail.RootElement.GetProperty("Name").GetString()!.TrimStart('/'),
                detail.RootElement.GetProperty("Config").GetProperty("Image").GetString() ?? string.Empty,
                env,
                detail.RootElement.GetProperty("State").GetProperty("Running").GetBoolean()));
        }

        return result;
    }

    private async Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, ct);
        return true;
    }

    private async Task DockerAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct, params HttpStatusCode[] alsoOk)
    {
        using var response = await SendAsync(method, path, content, ct);
        if (alsoOk.Contains(response.StatusCode))
        {
            return;
        }

        await EnsureSuccessAsync(response, ct);
        // Las descargas de imágenes responden en streaming: hay que leer hasta el final para que terminen.
        await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var portainer = portainerOptions.Value;
        var http = httpFactory.CreateClient("sqladmin-docker");
        using var request = new HttpRequestMessage(method, $"{portainer.Url.TrimEnd('/')}/api/endpoints/{portainer.EndpointId}/docker/{path}") { Content = content };
        request.Headers.Add("X-API-Key", portainer.ApiKey);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Docker respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        }
    }

    /// <summary>
    /// Nombre de imagen para la ruta de la API: el proxy de Portainer rechaza (403) una "/" codificada, así que
    /// "/" y ":" van tal cual (Docker los acepta en la ruta).
    /// </summary>
    internal static string ImagePath(string image) =>
        Uri.EscapeDataString(image).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase).Replace("%3A", ":", StringComparison.OrdinalIgnoreCase);

    private static (string Name, string Tag) SplitImage(string image)
    {
        var colon = image.LastIndexOf(':');
        return colon > image.LastIndexOf('/') ? (image[..colon], image[(colon + 1)..]) : (image, "latest");
    }

    private string Sign(string value)
    {
        var key = Encoding.UTF8.GetBytes(configuration["Jwt:Secret"] ?? configuration["Jwt__Secret"] ?? "cenit-sqladmin");
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"sqladmin:{value}")));
    }
}

/// <summary>Para el explorador de bases de datos cuando lleva un rato sin usarse (se mira cada 30 s).</summary>
public class SqlAdminIdleService(SqlAdminService sqlAdmin, ILogger<SqlAdminIdleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Tras reiniciar el portal no se sabe cuándo se usó por última vez: se cuenta desde ahora.
        sqlAdmin.Touch();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await sqlAdmin.StopIfIdleAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "No se pudo comprobar la inactividad del explorador de bases de datos");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // El portal se está parando.
        }
    }
}
