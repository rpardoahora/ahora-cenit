using AhoraCenit.Api.Services;

namespace AhoraCenit.Api.Features.DockerHost;

/// <summary>Uso de la máquina leído de su kernel: CPU y RAM de /proc, disco del sistema de archivos de Docker.</summary>
public record HostUsageResponse(
    double? CpuPercent,
    double[]? LoadAverage,
    long? MemoryTotalBytes,
    long? MemoryUsedBytes,
    long? DiskTotalBytes,
    long? DiskUsedBytes,
    long? UptimeSeconds);

public record HostResponse(HostUsageResponse Usage, DockerHostInfo? Docker);

/// <summary>
/// Administración → Consumo: la máquina Docker vista desde fuera de los contenedores. El portal corre en
/// un contenedor de esa misma máquina y /proc/stat, /proc/meminfo, /proc/loadavg y /proc/uptime no están
/// aislados por contenedor, así que son los del host (en Docker Desktop, los de su máquina virtual). El
/// disco es el que respalda el sistema de archivos del contenedor: el directorio de datos de Docker.
/// </summary>
public static class DockerHostEndpoints
{
    private static readonly TimeSpan CpuSampleInterval = TimeSpan.FromSeconds(1);

    public static RouteGroupBuilder MapDockerHostEndpoints(this RouteGroupBuilder group)
    {
        group.RequireAuthorization("AdminOnly");

        group.MapGet("/", GetAsync);

        return group;
    }

    private static async Task<IResult> GetAsync(IPortainerClient portainer, IConfiguration configuration, CancellationToken ct)
    {
        var dockerTask = portainer.GetDockerHostAsync(configuration.GetValue("Portainer:EndpointId", 1), ct);
        var cpuTask = SampleCpuPercentAsync(ct);

        var (memoryTotal, memoryUsed) = ReadMemory();
        var (diskTotal, diskUsed) = ReadDisk();
        var usage = new HostUsageResponse(
            await cpuTask,
            ReadLoadAverage(),
            memoryTotal,
            memoryUsed,
            diskTotal,
            diskUsed,
            ReadUptime());

        return Results.Ok(new HostResponse(usage, await dockerTask));
    }

    /// <summary>% de CPU de toda la máquina entre dos lecturas de la línea "cpu" de /proc/stat.</summary>
    private static async Task<double?> SampleCpuPercentAsync(CancellationToken ct)
    {
        var first = ReadCpuTimes();
        if (first is null)
        {
            return null;
        }

        await Task.Delay(CpuSampleInterval, ct);
        var second = ReadCpuTimes();
        if (second is null)
        {
            return null;
        }

        var total = second.Value.Total - first.Value.Total;
        var idle = second.Value.Idle - first.Value.Idle;
        return total <= 0 ? null : Math.Round(100.0 * (total - idle) / total, 1);
    }

    private static (long Total, long Idle)? ReadCpuTimes()
    {
        var line = ReadLines("/proc/stat")?.FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        // user nice system idle iowait irq softirq steal ...
        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
        if (values.Length < 4)
        {
            return null;
        }

        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        // guest y guest_nice ya van incluidos en user y nice.
        return (values.Take(Math.Min(values.Length, 8)).Sum(), idle);
    }

    private static (long? Total, long? Used) ReadMemory()
    {
        var lines = ReadLines("/proc/meminfo");
        if (lines is null)
        {
            return (null, null);
        }

        long? Kb(string key) => lines
            .Where(l => l.StartsWith(key + ":", StringComparison.Ordinal))
            .Select(l => (long?)long.Parse(l[(key.Length + 1)..].Trim().Split(' ')[0]) * 1024)
            .FirstOrDefault();

        var total = Kb("MemTotal");
        var available = Kb("MemAvailable");
        return (total, total is null || available is null ? null : total - available);
    }

    private static (long? Total, long? Used) ReadDisk()
    {
        try
        {
            var drive = new DriveInfo("/");
            return (drive.TotalSize, drive.TotalSize - drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    private static double[]? ReadLoadAverage()
    {
        var parts = ReadLines("/proc/loadavg")?.FirstOrDefault()?.Split(' ');
        return parts is { Length: >= 3 }
            ? parts.Take(3).Select(p => double.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray()
            : null;
    }

    private static long? ReadUptime()
    {
        var first = ReadLines("/proc/uptime")?.FirstOrDefault()?.Split(' ')[0];
        return first is not null && double.TryParse(first, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? (long)seconds
            : null;
    }

    /// <summary>null fuera de Linux (p. ej. el portal arrancado con dotnet run en Windows).</summary>
    private static string[]? ReadLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
