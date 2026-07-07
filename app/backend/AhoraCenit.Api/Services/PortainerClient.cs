using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AhoraCenit.Api.Data;
using Microsoft.Extensions.Options;

namespace AhoraCenit.Api.Services;

public record PortainerCreateStackResult(bool Success, int? StackId, int EndpointId, string? ErrorMessage);

public record PortainerOperationResult(bool Success, string? ErrorMessage);

/// <summary>Consumo agregado de recursos de todos los contenedores de un stack.</summary>
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
    /// </summary>
    Task<ApplicationStatus> GetStackStatusAsync(int stackId, int endpointId, string stackName, CancellationToken ct = default);

    Task<PortainerOperationResult> StartStackAsync(int stackId, int endpointId, CancellationToken ct = default);

    Task<PortainerOperationResult> StopStackAsync(int stackId, int endpointId, CancellationToken ct = default);

    Task<PortainerOperationResult> DeleteStackAsync(int stackId, int endpointId, CancellationToken ct = default);

    /// <summary>
    /// Suma el consumo de CPU/RAM/disco de todos los contenedores del stack,
    /// consultando la API de Docker (vía el proxy de Portainer) contenedor a
    /// contenedor. Puede tardar ~1s por contenedor (Docker necesita una
    /// muestra para calcular el % de CPU).
    /// </summary>
    Task<StackResourceUsage> GetStackResourceUsageAsync(int endpointId, string stackName, CancellationToken ct = default);
}

public class PortainerClient : IPortainerClient
{
    private readonly HttpClient _httpClient;
    private readonly PortainerOptions _options;
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

    public async Task<ApplicationStatus> GetStackStatusAsync(int stackId, int endpointId, string stackName, CancellationToken ct = default)
    {
        // Fuente de verdad: los contenedores Docker reales del stack (identificados por la
        // label que docker compose asigna automáticamente), no el campo Status del stack de
        // Portainer, que no se entera si alguien para/borra el contenedor fuera de la app.
        var containersStatus = await TryGetStatusFromContainersAsync(endpointId, stackName, ct);
        if (containersStatus is not null)
        {
            return containersStatus.Value;
        }

        // No se pudo consultar el motor Docker (p.ej. permisos): fallback al estado del stack.
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
                return ApplicationStatus.Error;
            }

            var stack = await response.Content.ReadFromJsonAsync<PortainerStackDto>(JsonOptions, ct);
            if (stack is null)
            {
                return ApplicationStatus.Error;
            }

            // Portainer stack Status: 1 = active, 2 = inactive.
            return stack.Status switch
            {
                1 => ApplicationStatus.Running,
                2 => ApplicationStatus.Stopped,
                _ => ApplicationStatus.Error
            };
        }
        catch
        {
            return ApplicationStatus.Error;
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

    public async Task<PortainerOperationResult> StartStackAsync(int stackId, int endpointId, CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.PostAsync($"api/stacks/{stackId}/start?endpointId={endpointId}", content: null, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return new PortainerOperationResult(false, $"Portainer respondió {(int)response.StatusCode}: {body}");
            }

            return new PortainerOperationResult(true, null);
        }
        catch (Exception ex)
        {
            return new PortainerOperationResult(false, ex.Message);
        }
    }

    public async Task<PortainerOperationResult> StopStackAsync(int stackId, int endpointId, CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.PostAsync($"api/stacks/{stackId}/stop?endpointId={endpointId}", content: null, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return new PortainerOperationResult(false, $"Portainer respondió {(int)response.StatusCode}: {body}");
            }

            return new PortainerOperationResult(true, null);
        }
        catch (Exception ex)
        {
            return new PortainerOperationResult(false, ex.Message);
        }
    }

    public async Task<PortainerOperationResult> DeleteStackAsync(int stackId, int endpointId, CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.DeleteAsync($"api/stacks/{stackId}?endpointId={endpointId}", ct);
            if (!response.IsSuccessStatusCode && (int)response.StatusCode != 404)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return new PortainerOperationResult(false, $"Portainer respondió {(int)response.StatusCode}: {body}");
            }

            return new PortainerOperationResult(true, null);
        }
        catch (Exception ex)
        {
            return new PortainerOperationResult(false, ex.Message);
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

        [JsonPropertyName("Status")]
        public int Status { get; set; }
    }

    private class DockerContainerSummaryDto
    {
        [JsonPropertyName("Id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("State")]
        public string State { get; set; } = string.Empty;
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
