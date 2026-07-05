namespace AhoraCenit.Api.Contracts.Products;

public record EnvVarDefinitionDto(string Key, string Label, string DefaultValue, bool IsSecret);

public record CreateProductRequest(
    string Name,
    string? Description,
    string? ImageUrl,
    string ComposeTemplate,
    List<EnvVarDefinitionDto>? EnvVarsSchema,
    bool IsActive = true);

public record UpdateProductRequest(
    string Name,
    string? Description,
    string? ImageUrl,
    string ComposeTemplate,
    List<EnvVarDefinitionDto>? EnvVarsSchema);

public record SetProductActiveRequest(bool IsActive);

public record SuggestSubdomainResponse(string SuggestedSubdomain);

public record ProductResponse(
    Guid Id,
    string Name,
    string Description,
    string ImageUrl,
    string ComposeTemplate,
    List<EnvVarDefinitionDto> EnvVarsSchema,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);
