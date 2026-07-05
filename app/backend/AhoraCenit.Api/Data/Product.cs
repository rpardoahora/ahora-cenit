namespace AhoraCenit.Api.Data;

public class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Docker compose YAML template with ${VAR} placeholders. Not validated
    /// by the backend beyond being stored as-is.
    /// </summary>
    public required string ComposeTemplate { get; set; }

    /// <summary>
    /// JSON-serialized list of <see cref="EnvVarDefinition"/> describing the
    /// configurable environment variables for this product.
    /// </summary>
    public string EnvVarsSchemaJson { get; set; } = "[]";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Application> Applications { get; set; } = new List<Application>();
}
