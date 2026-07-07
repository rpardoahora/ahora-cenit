using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Json;

namespace AhoraCenit.Api.Services;

/// <summary>
/// Registra un evento de auditoría (alta/modificación/baja) sobre Usuarios,
/// Productos o Aplicaciones: quién, qué, cuándo, cuánto tardó y con qué
/// parámetros/resultado. Siempre queda en el log estructurado de la app; si
/// además hay un listener de OpenTelemetry para <see cref="ActivitySourceName"/>
/// / <see cref="MeterName"/> (ver Program.cs, activado con
/// OTEL_EXPORTER_OTLP_ENDPOINT), también se exporta como traza y métrica con
/// esa misma información.
/// </summary>
public interface IAuditLogger
{
    Task RecordAsync(
        ClaimsPrincipal principal,
        string entityType,
        string action,
        Guid? entityId,
        object? parameters,
        object? result,
        TimeSpan duration,
        bool success,
        string? errorMessage = null);
}

public class AuditLogger(ILogger<AuditLogger> logger) : IAuditLogger
{
    public const string ActivitySourceName = "AhoraCenit.Audit";
    public const string MeterName = "AhoraCenit.Audit";

    private static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly Meter AppMeter = new(MeterName);
    private static readonly Counter<long> ActionsCounter = AppMeter.CreateCounter<long>(
        "ahora_cenit.audit.actions", unit: "{action}", description: "Número de acciones de auditoría (alta/baja/cambio) por entidad, acción y resultado.");
    private static readonly Histogram<double> ActionDuration = AppMeter.CreateHistogram<double>(
        "ahora_cenit.audit.action.duration", unit: "s", description: "Duración de cada acción auditada (incluye llamadas a Portainer cuando aplica).");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task RecordAsync(
        ClaimsPrincipal principal,
        string entityType,
        string action,
        Guid? entityId,
        object? parameters,
        object? result,
        TimeSpan duration,
        bool success,
        string? errorMessage = null)
    {
        var actorId = principal.Identity?.IsAuthenticated == true ? principal.GetUserId().ToString() : "anonymous";
        var actorEmail = principal.FindFirstValue(ClaimTypes.Email) ?? "unknown";
        var parametersJson = Serialize(parameters);
        var resultJson = Serialize(result);
        var startTime = DateTimeOffset.UtcNow - duration;

        using var activity = Source.StartActivity($"{entityType}.{action}", ActivityKind.Internal, default(ActivityContext), startTime: startTime);
        if (activity is not null)
        {
            activity.SetTag("audit.actor.id", actorId);
            activity.SetTag("audit.actor.email", actorEmail);
            activity.SetTag("audit.entity.type", entityType);
            activity.SetTag("audit.entity.id", entityId?.ToString());
            activity.SetTag("audit.action", action);
            activity.SetTag("audit.success", success);
            activity.SetTag("audit.parameters", parametersJson);
            activity.SetTag("audit.result", resultJson);
            if (!success)
            {
                activity.SetStatus(ActivityStatusCode.Error, errorMessage);
            }

            activity.SetEndTime(startTime.UtcDateTime + duration);
        }

        var metricTags = new TagList
        {
            { "entity_type", entityType },
            { "action", action },
            { "success", success }
        };
        ActionsCounter.Add(1, metricTags);
        ActionDuration.Record(duration.TotalSeconds, metricTags);

        logger.LogInformation(
            "Audit {EntityType}.{Action} entityId={EntityId} actor={ActorEmail} ({ActorId}) durationMs={DurationMs} success={Success} error={Error} parameters={Parameters} result={Result}",
            entityType, action, entityId, actorEmail, actorId, duration.TotalMilliseconds, success, errorMessage, parametersJson, resultJson);

        return Task.CompletedTask;
    }

    private static string? Serialize(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
}
