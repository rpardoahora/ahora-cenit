namespace AhoraCenit.Api.Data;

/// <summary>
/// A deployed instance of a Product for a given client (User), backed by a
/// Portainer stack.
/// </summary>
public class Application
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProductId { get; set; }

    public Product? Product { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>
    /// The slug used as APP_SUBDOMAIN. The full public URL is
    /// "{Subdomain}.{BaseDomain}".
    /// </summary>
    public required string Subdomain { get; set; }

    /// <summary>
    /// JSON-serialized dictionary of the final environment variable values
    /// used to deploy this application (product defaults merged with the
    /// user's overrides). Does not include APP_SUBDOMAIN/BASE_DOMAIN, which
    /// are always injected at deploy time.
    /// </summary>
    public string EnvVarValuesJson { get; set; } = "{}";

    public int? PortainerStackId { get; set; }

    public int PortainerEndpointId { get; set; }

    public ApplicationStatus Status { get; set; } = ApplicationStatus.Deploying;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
