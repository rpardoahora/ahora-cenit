namespace AhoraCenit.Api.Data;

/// <summary>
/// Cuánto tardó un despliegue concreto (Application Create exitoso) de un
/// Product. Se usa para estimar cuánto va a tardar el próximo despliegue del
/// mismo producto (media de las últimas muestras).
/// </summary>
public class DeploymentDurationSample
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProductId { get; set; }

    public Product? Product { get; set; }

    public double DurationSeconds { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
