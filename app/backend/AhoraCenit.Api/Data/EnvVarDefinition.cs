namespace AhoraCenit.Api.Data;

/// <summary>
/// Describes one configurable environment variable exposed by a Product's
/// compose template. Stored as part of Product.EnvVarsSchemaJson.
/// </summary>
public class EnvVarDefinition
{
    public required string Key { get; set; }

    public required string Label { get; set; }

    public string DefaultValue { get; set; } = string.Empty;

    public bool IsSecret { get; set; }
}
