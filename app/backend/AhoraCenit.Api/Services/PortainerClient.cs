using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AhoraCenit.Api.Data;
using Microsoft.Extensions.Options;

namespace AhoraCenit.Api.Services;

public record PortainerCreateStackResult(bool Success, int? StackId, int EndpointId, string? ErrorMessage);

/// <param name="Busy">Portainer sigue con otra operación sobre el stack (p. ej. el despliegue inicial).</param>
public record PortainerOperationResult(bool Success, string? ErrorMessage, bool Busy = false);

/// <summary>Consumo agregado de recursos de todos los contenedores de un stack.</summary>
/// <summary>La máquina Docker: lo que cuenta su /info y el espacio de su /system/df.</summary>
public record DockerHostInfo(
    int Cpus,
    long MemoryBytes,
    string OperatingSystem,
    string KernelVersion,
    string DockerVersion,
    int Containers,
    int ContainersRunning,
    int Images,
    long ImagesBytes,
    long VolumesBytes,
    long ContainersBytes,
    long BuildCacheBytes);

public record StackReadiness(bool? Ready, StartupProgress? Progress = null);

public record StackResourceUsage(double CpuPercent, long MemoryUsageBytes, long DiskUsageBytes, int ContainerCount);

/// <summary>
/// Abstraction over the Portainer CE HTTP API used to manage Applications'
/// underlying docker compose stacks. All Portainer-specific request/response
/// shapes are encapsulated here so they can be adjusted independently of the
/// rest of the backend if the real API differs from what's assumed below.
/// </summary>
public interface IPortainerClient
{
    /// <summary>
    /// Creates a new standalone compose stack in the configured Portainer
    /// endpoint from a compose YAML string, injecting the given environment
    /// variables (which must already include APP_SUBDOMAIN and BASE_DOMAIN).
    /// </summary>
    Task<PortainerCreateStackResult> CreateStackAsync(
        string stackName,
        string composeContent,
        IReadOnlyDictionary<string, string> envVars,
        CancellationToken ct = default);

    /// <summary>
    /// Consulta el estado real de los contenedores Docker del stack (a través
    /// del proxy de Portainer) y lo mapea a <see cref="ApplicationStatus"/>.
    /// A diferencia del campo Status del propio stack de Portainer (que no se
    /// actualiza si el contenedor se borra/para directamente en Docker), esto
    /// refleja el estado real en el motor Docker.
    /// Devuelve null si no se pudo determinar el estado (fallo de conectividad
    /// transitorio con Portainer): en ese caso el llamador debe mantener el
    /// último estado conocido en vez de asumir que la aplicación ha fallado.
    /// </summary>
    Task<ApplicationStatus?> GetStackStatusAsync(int stackId, int endpointId, string stackName, CancellationToken ct = default);

    /// <summary>
    /// Busca en Portainer un stack por nombre exacto (case-insensitive) y
    /// devuelve su Id si existe. Se usa para recuperar aplicaciones cuya
    /// creación se dio (incorrectamente) por fallida porque nuestra llamada
    /// HTTP hizo timeout, aunque el stack se llegara a crear igualmente.
    /// </summary>
    Task<int?> FindStackIdByNameAsync(string stackName, CancellationToken ct = default);

    Task<PortainerOperationResult> StartStackAsync(int stackId, int endpointId, CancellationToken ct = default);

    Task<PortainerOperationResult> StopStackAsync(int stackId, int endpointId, CancellationToken ct = default);

    /// <summary>
    /// Borra el stack y, además, los volúmenes con nombre creados por su compose (Portainer
    /// solo hace el equivalente a "docker compose down", que no se lleva los volúmenes, y
    /// eso deja huérfanos los datos de la aplicación borrada).
    /// </summary>
    /// <param name="deleteVolumes">false: el stack se borra pero sus volúmenes se conservan.</param>
    Task<PortainerOperationResult> DeleteStackAsync(int stackId, int endpointId, string stackName, bool deleteVolumes = true, CancellationToken ct = default);

    /// <summary>Compose con el que se desplegó el stack; null si Portainer no responde.</summary>
    Task<string?> GetStackFileAsync(int stackId, CancellationToken ct = default);

    /// <summary>Borra una imagen de Docker si no la usa ningún contenedor (Docker se niega si la usa otro).</summary>
    Task<PortainerOperationResult> DeleteImageAsync(int endpointId, string image, CancellationToken ct = default);

    /// <summary>
    /// Suma el consumo de CPU/RAM/disco de todos los contenedores del stack,
    /// consultando la API de Docker (vía el proxy de Portainer) contenedor a
    /// contenedor. Puede tardar ~1s por contenedor (Docker necesita una
    /// muestra para calcular el % de CPU).
    /// </summary>
    Task<StackResourceUsage> GetStackResourceUsageAsync(int endpointId, string stackName, CancellationToken ct = default);

    /// <summary>Datos de la máquina Docker del endpoint (null si Portainer no responde).</summary>
    Task<DockerHostInfo?> GetDockerHostAsync(int endpointId, CancellationToken ct = default);

    /// <summary>
    /// ¿Responde ya la aplicación? Con los contenedores arriba, mira su HEALTHCHECK (si lo tienen) y consulta su
    /// web por la red interna: en un Flexygo, su deployment-status (frontend, backend y base de datos con versión);
    /// en otra aplicación, que la web conteste sin error 5xx. Ready es null si no se puede saber; si no está lista,
    /// Progress dice por dónde va (en un Flexygo, el progreso de su instalación).
    /// </summary>
    Task<StackReadiness> GetStackReadinessAsync(int endpointId, string stackName, CancellationToken ct = default);

    /// <summary>Imagen con la que corre cada servicio del stack (null si Portainer no responde).</summary>
    Task<IReadOnlyList<StackServiceImage>?> ListStackImagesAsync(int endpointId, string stackName, CancellationToken ct = default);
}

public record StackServiceImage(string Service, string Image, string State);

public class PortainerClient : IPortainerClient
{
    private readonly HttpClient _httpClient;
    private readonly PortainerOptions _options;

    private static readonly TimeSpan StackBusyTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StackBusyRetryDelay = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PortainerClient(HttpClient httpClient, IOptions<PortainerOptions> options)
    {
        _options = options.Value;
        _httpClient = httpClient;

        if (!string.IsNullOrWhiteSpace(_options.Url))
        {
            _httpClient.BaseAddress = new Uri(_options.Url.TrimEnd('/') + "/");
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Remove("X-API-Key");
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", _options.ApiKey);
        }

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<PortainerCreateStackResult> CreateStackAsync(
        string stackName,
        string composeContent,
        IReadOnlyDictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        var endpointId = _options.EndpointId;

        var payload = new PortainerCreateStackRequest
        {
            Name = stackName,
            StackFileContent = composeContent,
            Env = envVars.Select(kv => new PortainerEnvVar { Name = kv.Key, Value = kv.Value }).ToList()
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                $"api/stacks/create/standalone/string?endpointId={endpointId}",
                payload,
                JsonOptions,
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return new PortainerCreateStackResult(false, null, endpointId, $"Portainer respondió {(int)response.StatusCode}: {body}");
            }

            var created = await response.Content.ReadFromJsonAsync<PortainerStackDto>(JsonOptions, ct);
            if (created is null)
            {
                return new PortainerCreateStackResult(false, null, endpointId, "Respuesta de Portainer vacía al crear el stack.");
            }

            return new PortainerCreateStackResult(true, created.Id, endpointId, null);
        }
        catch (Exception ex)
        {
            return new PortainerCreateStackResult(false, null, endpointId, ex.Message);
        }
    }

    public async Task<ApplicationStatus?> GetStackStatusAsync(int stackId, int endpointId, string stackName, CancellationToken ct = default)
    {
        // Fuente de verdad: los contenedores Docker reales del stack (identificados por la
        // label que docker compose asigna automáticamente), no el campo Status del stack de
        // Portainer, que no se entera si alguien para/borra el contenedor fuera de la app.
        var containersStatus = await TryGetStatusFromContainersAsync(endpointId, stackName, ct);
        if (containersStatus is ApplicationStatus.Running or ApplicationStatus.Stopped)
        {
            return containersStatus.Value;
        }

        // Sin contenedores, o sin acceso al motor Docker: decide el estado del stack en
        // Portainer. Ojo: Portainer para un stack con el equivalente a "docker compose down",
        // que elimina los contenedores, así que "sin contenedores" también es un stack parado.
        var stackStatus = await GetStatusFromStackAsync(stackId, ct);
        if (containersStatus == ApplicationStatus.Deleted && stackStatus == ApplicationStatus.Running)
        {
            // Portainer lo da por activo pero sus contenedores ya no existen (borrados fuera de la app).
            return ApplicationStatus.Deleted;
        }

        return stackStatus;
    }

    /// <summary>Estado según el propio stack de Portainer, o null si no se pudo determinar.</summary>
    private async Task<ApplicationStatus?> GetStatusFromStackAsync(int stackId, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"api/stacks/{stackId}", ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // El stack ya no existe en Portainer (borrado manualmente fuera de la app).
                return ApplicationStatus.Deleted;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Fallo puntual de Portainer al consultar el stack: no sabemos el estado
                // real, así que no lo demos por erróneo.
                return null;
            }

            var stack = await response.Content.ReadFromJsonAsync<PortainerStackDto>(JsonOptions, ct);
            if (stack is null)
            {
                return null;
            }

            // Portainer stack Status: 1 = active, 2 = inactive.
            return stack.Status switch
            {
                1 => ApplicationStatus.Running,
                2 => ApplicationStatus.Stopped,
                _ => null
            };
        }
        catch
        {
            // Timeout o error de red hablando con Portainer: desconocido, no error.
            return null;
        }
    }

    public async Task<int?> FindStackIdByNameAsync(string stackName, CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("api/stacks", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var stacks = await response.Content.ReadFromJsonAsync<List<PortainerStackDto>>(JsonOptions, ct);
            return stacks?.FirstOrDefault(s => string.Equals(s.Name, stackName, StringComparison.OrdinalIgnoreCase))?.Id;
        }
        catch
        {
            return null;
        }
    }

    private async Task<ApplicationStatus?> TryGetStatusFromContainersAsync(int endpointId, string stackName, CancellationToken ct)
    {
        var containers = await ListStackContainersAsync(endpointId, stackName, ct);
        if (containers is null)
        {
            return null;
        }

        if (containers.Count == 0)
        {
            return ApplicationStatus.Deleted;
        }

        return containers.Any(c => c.State == "running") ? ApplicationStatus.Running : ApplicationStatus.Stopped;
    }

    private static readonly HttpClient ProbeClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<StackReadiness> GetStackReadinessAsync(int endpointId, string stackName, CancellationToken ct = default)
    {
        var containers = await ListStackContainersAsync(endpointId, stackName, ct);
        if (containers is null)
        {
            return new StackReadiness(null);
        }

        static string ServiceOf(DockerContainerSummaryDto c) =>
            c.Labels?.GetValueOrDefault("com.docker.compose.service") ?? c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.Id;

        var restarting = containers.FirstOrDefault(c => c.State == "restarting" || c.Status.StartsWith("Restarting", StringComparison.OrdinalIgnoreCase));
        if (restarting is not null)
        {
            return new StackReadiness(false, new StartupProgress(
                $"El servicio «{ServiceOf(restarting)}» se para y se vuelve a arrancar una y otra vez",
                "Algo le impide arrancar; su log (en Portainer) dice por qué.", IsError: true));
        }

        var running = containers.Where(c => c.State == "running").ToList();
        // Un servicio web (publicado por Traefik) que aún no corre: todavía no se puede abrir.
        var webStopped = containers.Any(c => c.State != "running" && c.Labels?.GetValueOrDefault("traefik.enable") == "true");
        if (running.Count == 0 || webStopped)
        {
            return new StackReadiness(false, new StartupProgress($"Arrancando los contenedores ({running.Count} de {containers.Count} en marcha)"));
        }

        var waiting = running.FirstOrDefault(c => c.Status.Contains("health: starting", StringComparison.OrdinalIgnoreCase));
        if (waiting is not null)
        {
            var isSql = waiting.Image.Contains("mssql", StringComparison.OrdinalIgnoreCase) || waiting.Image.Contains("-db:", StringComparison.OrdinalIgnoreCase);
            return new StackReadiness(false, new StartupProgress(isSql ? "Esperando a que arranque SQL Server" : $"Esperando a que «{ServiceOf(waiting)}» esté listo"));
        }

        var unhealthy = running.FirstOrDefault(c => c.Status.Contains("unhealthy", StringComparison.OrdinalIgnoreCase));
        if (unhealthy is not null)
        {
            return new StackReadiness(false, new StartupProgress($"«{ServiceOf(unhealthy)}» no pasa su comprobación de salud", IsError: true));
        }

        // El servicio web es el que publica Traefik; se le consulta directamente por la red proxy.
        var web = running
            .Select(c => (Container: c, Port: c.Labels?.FirstOrDefault(l => l.Key.StartsWith("traefik.http.services.", StringComparison.Ordinal)
                                                                           && l.Key.EndsWith(".loadbalancer.server.port", StringComparison.Ordinal)).Value))
            .FirstOrDefault(x => !string.IsNullOrEmpty(x.Port) && x.Container.Labels?.GetValueOrDefault("traefik.enable") == "true");
        if (web.Container is null || string.IsNullOrEmpty(web.Container.Names?.FirstOrDefault()))
        {
            return new StackReadiness(true);
        }

        var baseUrl = $"http://{web.Container.Names![0].TrimStart('/')}:{web.Port}";
        var notResponding = new StackReadiness(false, new StartupProgress("Esperando a que la aplicación responda"));
        try
        {
            using var status = await ProbeClient.GetAsync($"{baseUrl}/api/frontend/sys/deployment-status", ct);
            if (status.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync(ct));
                // Un Flexygo contesta con su estado o, mientras instala una base de datos, con el aviso de mantenimiento.
                var isFlexygo = json.RootElement.ValueKind == JsonValueKind.Object
                                && (json.RootElement.TryGetProperty("Frontend", out _) || json.RootElement.TryGetProperty("maintenance", out _));
                if (isFlexygo)
                {
                    bool Reachable(string part) =>
                        json.RootElement.TryGetProperty(part, out var p) && p.TryGetProperty("IsReachable", out var r) && r.ValueKind == JsonValueKind.True;
                    var databaseVersion = json.RootElement.TryGetProperty("Database", out var db) && db.TryGetProperty("Version", out var v)
                        ? v.GetString() : null;
                    if (!status.IsSuccessStatusCode || !Reachable("Frontend") || !Reachable("Backend") || !Reachable("Database") || string.IsNullOrEmpty(databaseVersion))
                    {
                        return new StackReadiness(false, await FlexygoProgressAsync(baseUrl, ct));
                    }

                    // La base de datos tiene versión antes de que acabe la instalación desatendida: Setup/ready (que el
                    // frontend reenvía al backend) da 503 hasta entonces. Las versiones que no lo tienen dan 404.
                    using var setup = await ProbeClient.GetAsync($"{baseUrl}/api/Setup/ready", ct);
                    if (setup.StatusCode != System.Net.HttpStatusCode.NotFound && !setup.IsSuccessStatusCode)
                    {
                        return new StackReadiness(false, await FlexygoProgressAsync(baseUrl, ct));
                    }
                }
            }

            // Pedida como API, la web contesta 503 mientras está en mantenimiento (p. ej. aplicando un paquete de BBDD).
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/");
            request.Headers.Accept.ParseAdd("application/json");
            using var home = await ProbeClient.SendAsync(request, ct);
            return (int)home.StatusCode < 500 ? new StackReadiness(true) : notResponding;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return notResponding;
        }
    }

    /// <summary>Progreso de la instalación de un Flexygo (GET /api/Setup/progress, que el frontend reenvía al backend).</summary>
    private static async Task<StartupProgress> FlexygoProgressAsync(string baseUrl, CancellationToken ct)
    {
        var waiting = new StartupProgress("Esperando a que la aplicación responda");
        try
        {
            using var response = await ProbeClient.GetAsync($"{baseUrl}/api/Setup/progress", ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                // Mientras despliega las bases de datos de datos, Flexygo entra en mantenimiento y no deja pasar ni la
                // consulta del progreso.
                return new StartupProgress(
                    "Instalando las bases de datos de datos",
                    "Flexygo está en mantenimiento mientras las despliega y no informa del avance de esta fase.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return body.Contains("Backend unavailable", StringComparison.OrdinalIgnoreCase)
                    ? new StartupProgress("Arrancando el backend de la aplicación")
                    : waiting;
            }

            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("Status", out _)
                ? FlexygoSetupProgress.Describe(json.RootElement)
                : waiting;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return waiting;
        }
    }

    public async Task<IReadOnlyList<StackServiceImage>?> ListStackImagesAsync(int endpointId, string stackName, CancellationToken ct = default)
    {
        var containers = await ListStackContainersAsync(endpointId, stackName, ct);
        return containers?
            .Select(c => new StackServiceImage(
                c.Labels?.GetValueOrDefault("com.docker.compose.service") ?? c.Id[..Math.Min(12, c.Id.Length)],
                c.Image,
                c.State))
            .OrderBy(c => c.Service)
            .ToList();
    }

    /// <summary>Contenedores Docker (de cualquier estado) que pertenecen al stack de compose dado.</summary>
    private async Task<List<DockerContainerSummaryDto>?> ListStackContainersAsync(int endpointId, string stackName, CancellationToken ct)
    {
        try
        {
            var filters = JsonSerializer.Serialize(new Dictionary<string, string[]>
            {
                ["label"] = [$"com.docker.compose.project={stackName}"]
            });

            using var response = await _httpClient.GetAsync(
                $"api/endpoints/{endpointId}/docker/containers/json?all=true&filters={Uri.EscapeDataString(filters)}",
                ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<List<DockerContainerSummaryDto>>(JsonOptions, ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<StackResourceUsage> GetStackResourceUsageAsync(int endpointId, string stackName, CancellationToken ct = default)
    {
        var containers = await ListStackContainersAsync(endpointId, stackName, ct);
        if (containers is null || containers.Count == 0)
        {
            return new StackResourceUsage(0, 0, 0, 0);
        }

        var perContainer = await Task.WhenAll(
            containers.Select(c => GetContainerUsageAsync(endpointId, c.Id, ct)));

        var valid = perContainer.Where(u => u is not null).Select(u => u!.Value).ToList();

        return new StackResourceUsage(
            CpuPercent: valid.Sum(u => u.CpuPercent),
            MemoryUsageBytes: valid.Sum(u => u.MemoryUsageBytes),
            DiskUsageBytes: valid.Sum(u => u.DiskUsageBytes),
            ContainerCount: valid.Count);
    }

    public async Task<DockerHostInfo?> GetDockerHostAsync(int endpointId, CancellationToken ct = default)
    {
        try
        {
            // /system/df se pide por tipo: sin filtro, un fallo al medir las imágenes (p. ej. un snapshot de
            // containerd que ya no existe) hace fallar la consulta entera y se perderían también las demás.
            var infoTask = _httpClient.GetAsync($"api/endpoints/{endpointId}/docker/info", ct);
            var imagesTask = DiskUsageAsync(endpointId, "image", df => Long(df, "LayersSize"), ct);
            var volumesTask = DiskUsageAsync(endpointId, "volume", df => Sum(df, "Volumes", v => v.TryGetProperty("UsageData", out var u) ? Long(u, "Size") : 0), ct);
            var containersTask = DiskUsageAsync(endpointId, "container", df => Sum(df, "Containers", c => Long(c, "SizeRw")), ct);
            var buildCacheTask = DiskUsageAsync(endpointId, "build-cache", df => Sum(df, "BuildCache", b => Long(b, "Size")), ct);
            using var infoResponse = await infoTask;
            if (!infoResponse.IsSuccessStatusCode)
            {
                return null;
            }

            using var info = JsonDocument.Parse(await infoResponse.Content.ReadAsStringAsync(ct));
            var root = info.RootElement;
            int Int(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

            return new DockerHostInfo(
                Int("NCPU"),
                root.TryGetProperty("MemTotal", out var mem) && mem.ValueKind == JsonValueKind.Number ? mem.GetInt64() : 0,
                Text("OperatingSystem"),
                Text("KernelVersion"),
                Text("ServerVersion"),
                Int("Containers"),
                Int("ContainersRunning"),
                Int("Images"),
                await imagesTask,
                await volumesTask,
                await containersTask,
                await buildCacheTask);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }

        static long Long(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Max(0, v.GetInt64()) : 0;

        static long Sum(JsonElement df, string list, Func<JsonElement, long> size) =>
            df.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().Sum(size) : 0;
    }

    /// <summary>Bytes de un tipo de /system/df (0 si Docker no lo puede calcular).</summary>
    private async Task<long> DiskUsageAsync(int endpointId, string type, Func<JsonElement, long> bytes, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"api/endpoints/{endpointId}/docker/system/df?type={type}", ct);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            using var df = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return bytes(df.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return 0;
        }
    }

    // Con stream=false, Docker a veces devuelve precpu_stats == cpu_stats (delta 0) si no
    // hay ya un stream de stats abierto para ese contenedor. Para tener un % de CPU fiable,
    // pedimos dos muestras reales separadas por este intervalo y calculamos el delta nosotros.
    private static readonly TimeSpan CpuSampleInterval = TimeSpan.FromSeconds(1);

    private async Task<(double CpuPercent, long MemoryUsageBytes, long DiskUsageBytes)?> GetContainerUsageAsync(
        int endpointId, string containerId, CancellationToken ct)
    {
        try
        {
            var statsUrl = $"api/endpoints/{endpointId}/docker/containers/{containerId}/stats?stream=false";

            using var firstResponse = await _httpClient.GetAsync(statsUrl, ct);
            if (!firstResponse.IsSuccessStatusCode)
            {
                return null;
            }
            var first = await firstResponse.Content.ReadFromJsonAsync<DockerStatsDto>(JsonOptions, ct);

            await Task.Delay(CpuSampleInterval, ct);

            // La segunda muestra de stats y el inspect (para el disco) se piden en paralelo.
            var secondResponseTask = _httpClient.GetAsync(statsUrl, ct);
            var inspectResponseTask = _httpClient.GetAsync(
                $"api/endpoints/{endpointId}/docker/containers/{containerId}/json?size=true", ct);

            using var secondResponse = await secondResponseTask;
            using var inspectResponse = await inspectResponseTask;

            if (first is null || !secondResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var second = await secondResponse.Content.ReadFromJsonAsync<DockerStatsDto>(JsonOptions, ct);
            if (second is null)
            {
                return null;
            }

            var cpuPercent = CalculateCpuPercent(first, second);
            var memoryBytes = CalculateMemoryUsage(second);

            long diskBytes = 0;
            if (inspectResponse.IsSuccessStatusCode)
            {
                var info = await inspectResponse.Content.ReadFromJsonAsync<DockerContainerInspectDto>(JsonOptions, ct);
                diskBytes = info?.SizeRw ?? 0;
            }

            return (cpuPercent, memoryBytes, diskBytes);
        }
        catch
        {
            return null;
        }
    }

    private static double CalculateCpuPercent(DockerStatsDto first, DockerStatsDto second)
    {
        if (first.CpuStats?.CpuUsage is null || second.CpuStats?.CpuUsage is null)
        {
            return 0;
        }

        var cpuDelta = second.CpuStats.CpuUsage.TotalUsage - first.CpuStats.CpuUsage.TotalUsage;
        var systemDelta = second.CpuStats.SystemCpuUsage - first.CpuStats.SystemCpuUsage;
        if (systemDelta <= 0 || cpuDelta < 0)
        {
            return 0;
        }

        var onlineCpus = second.CpuStats.OnlineCpus
            ?? second.CpuStats.CpuUsage.PerCpuUsage?.Count
            ?? 1;

        return (double)cpuDelta / systemDelta * onlineCpus * 100.0;
    }

    private static long CalculateMemoryUsage(DockerStatsDto stats)
    {
        if (stats.MemoryStats is null)
        {
            return 0;
        }

        // El uso "real" excluye la cache de página, igual que hace `docker stats`.
        // cgroup v1 la llama "cache"; cgroup v2, "inactive_file".
        long cache = 0;
        stats.MemoryStats.Stats?.TryGetValue("cache", out cache);
        if (cache == 0)
        {
            stats.MemoryStats.Stats?.TryGetValue("inactive_file", out cache);
        }

        var usage = stats.MemoryStats.Usage - cache;
        return usage < 0 ? stats.MemoryStats.Usage : usage;
    }

    /// <summary>
    /// Arranca o para un stack. Portainer responde 409 "Stack deployment is in
    /// progress" mientras termina el despliegue (aunque los contenedores ya estén
    /// en marcha), así que en ese caso se reintenta durante un rato.
    /// </summary>
    private async Task<PortainerOperationResult> PostStackActionAsync(int stackId, int endpointId, string action, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + StackBusyTimeout;
        while (true)
        {
            try
            {
                using var response = await _httpClient.PostAsync($"api/stacks/{stackId}/{action}?endpointId={endpointId}", content: null, ct);
                if (response.IsSuccessStatusCode)
                {
                    return new PortainerOperationResult(true, null);
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                var busy = response.StatusCode == HttpStatusCode.Conflict
                    && body.Contains("in progress", StringComparison.OrdinalIgnoreCase);
                if (!busy || DateTime.UtcNow >= deadline)
                {
                    return new PortainerOperationResult(false, $"Portainer respondió {(int)response.StatusCode}: {body}", busy);
                }
            }
            catch (Exception ex)
            {
                return new PortainerOperationResult(false, ex.Message);
            }

            await Task.Delay(StackBusyRetryDelay, ct);
        }
    }

    public Task<PortainerOperationResult> StartStackAsync(int stackId, int endpointId, CancellationToken ct = default) =>
        PostStackActionAsync(stackId, endpointId, "start", ct);

    public Task<PortainerOperationResult> StopStackAsync(int stackId, int endpointId, CancellationToken ct = default) =>
        PostStackActionAsync(stackId, endpointId, "stop", ct);

    public async Task<PortainerOperationResult> DeleteStackAsync(int stackId, int endpointId, string stackName, bool deleteVolumes = true, CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.DeleteAsync($"api/stacks/{stackId}?endpointId={endpointId}", ct);
            if (!response.IsSuccessStatusCode && (int)response.StatusCode != 404)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return new PortainerOperationResult(false, $"Portainer respondió {(int)response.StatusCode}: {body}");
            }
        }
        catch (Exception ex)
        {
            return new PortainerOperationResult(false, ex.Message);
        }

        // El borrado del stack ya se ha confirmado en este punto: el de los volúmenes es un
        // best-effort de limpieza que no debe hacer fallar la operación si Docker no responde.
        if (deleteVolumes)
        {
            await DeleteStackVolumesAsync(endpointId, stackName, ct);
        }

        return new PortainerOperationResult(true, null);
    }

    /// <summary>Borra los volúmenes con nombre (no externos) que docker compose creó para este stack.</summary>
    public async Task<string?> GetStackFileAsync(int stackId, CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"api/stacks/{stackId}/file", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.TryGetProperty("StackFileContent", out var content) ? content.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<PortainerOperationResult> DeleteImageAsync(int endpointId, string image, CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.DeleteAsync(
                $"api/endpoints/{endpointId}/docker/images/{SqlAdminService.ImagePath(image)}?force=false&noprune=false", ct);
            if (response.IsSuccessStatusCode || (int)response.StatusCode == 404)
            {
                return new PortainerOperationResult(true, null);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return new PortainerOperationResult(false, $"Portainer respondió {(int)response.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            return new PortainerOperationResult(false, ex.Message);
        }
    }

    private async Task DeleteStackVolumesAsync(int endpointId, string stackName, CancellationToken ct)
    {
        try
        {
            var filters = JsonSerializer.Serialize(new Dictionary<string, string[]>
            {
                ["label"] = [$"com.docker.compose.project={stackName}"]
            });

            using var listResponse = await _httpClient.GetAsync(
                $"api/endpoints/{endpointId}/docker/volumes?filters={Uri.EscapeDataString(filters)}",
                ct);

            if (!listResponse.IsSuccessStatusCode)
            {
                return;
            }

            var payload = await listResponse.Content.ReadFromJsonAsync<DockerVolumesListDto>(JsonOptions, ct);
            foreach (var volume in payload?.Volumes ?? [])
            {
                try
                {
                    await _httpClient.DeleteAsync(
                        $"api/endpoints/{endpointId}/docker/volumes/{Uri.EscapeDataString(volume.Name)}?force=true",
                        ct);
                }
                catch
                {
                    // Si falla el borrado de un volumen concreto, seguimos con el resto.
                }
            }
        }
        catch
        {
            // No bloqueamos el borrado de la aplicación si no se pudieron limpiar los volúmenes.
        }
    }

    // --- Portainer wire DTOs ---

    private class PortainerCreateStackRequest
    {
        [JsonPropertyName("Name")]
        public required string Name { get; set; }

        [JsonPropertyName("StackFileContent")]
        public required string StackFileContent { get; set; }

        [JsonPropertyName("Env")]
        public List<PortainerEnvVar> Env { get; set; } = [];
    }

    private class PortainerEnvVar
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("value")]
        public required string Value { get; set; }
    }

    private class PortainerStackDto
    {
        [JsonPropertyName("Id")]
        public int Id { get; set; }

        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("Status")]
        public int Status { get; set; }
    }

    private class DockerContainerSummaryDto
    {
        [JsonPropertyName("Id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("State")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("Image")]
        public string Image { get; set; } = string.Empty;

        [JsonPropertyName("Labels")]
        public Dictionary<string, string>? Labels { get; set; }

        /// <summary>"Up 2 minutes (health: starting)".</summary>
        [JsonPropertyName("Status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("Names")]
        public List<string>? Names { get; set; }
    }

    private class DockerVolumesListDto
    {
        [JsonPropertyName("Volumes")]
        public List<DockerVolumeSummaryDto>? Volumes { get; set; }
    }

    private class DockerVolumeSummaryDto
    {
        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;
    }

    private class DockerContainerInspectDto
    {
        [JsonPropertyName("SizeRw")]
        public long? SizeRw { get; set; }
    }

    private class DockerStatsDto
    {
        [JsonPropertyName("cpu_stats")]
        public DockerCpuStatsDto? CpuStats { get; set; }

        [JsonPropertyName("memory_stats")]
        public DockerMemoryStatsDto? MemoryStats { get; set; }
    }

    private class DockerCpuStatsDto
    {
        [JsonPropertyName("cpu_usage")]
        public DockerCpuUsageDto? CpuUsage { get; set; }

        [JsonPropertyName("system_cpu_usage")]
        public long SystemCpuUsage { get; set; }

        [JsonPropertyName("online_cpus")]
        public int? OnlineCpus { get; set; }
    }

    private class DockerCpuUsageDto
    {
        [JsonPropertyName("total_usage")]
        public long TotalUsage { get; set; }

        [JsonPropertyName("percpu_usage")]
        public List<long>? PerCpuUsage { get; set; }
    }

    private class DockerMemoryStatsDto
    {
        [JsonPropertyName("usage")]
        public long Usage { get; set; }

        [JsonPropertyName("stats")]
        public Dictionary<string, long>? Stats { get; set; }
    }
}
