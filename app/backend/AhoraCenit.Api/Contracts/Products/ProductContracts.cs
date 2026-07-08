using AhoraCenit.Api.Data;

namespace AhoraCenit.Api.Contracts.Products;

public record EnvVarDefinitionDto(string Key, string Label, string DefaultValue, EnvVarInputMode Mode);

public record CreateProductRequest(
    string Name,
    string? Description,
    string? ImageUrl,
    string? WebsiteUrl,
    string ComposeTemplate,
    List<EnvVarDefinitionDto>? EnvVarsSchema,
    bool IsActive = true);

public record UpdateProductRequest(
    string Name,
    string? Description,
    string? ImageUrl,
    string? WebsiteUrl,
    string ComposeTemplate,
    List<EnvVarDefinitionDto>? EnvVarsSchema);

public record SetProductActiveRequest(bool IsActive);

public record SuggestSubdomainResponse(string SuggestedSubdomain);

public record DeployStatsResponse(double? AverageDeploySeconds, int SampleCount);

public record ProductResponse(
    Guid Id,
    string Name,
    string Description,
    string ImageUrl,
    string WebsiteUrl,
    string ComposeTemplate,
    List<EnvVarDefinitionDto> EnvVarsSchema,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Un producto tal y como viaja en el fichero de export/import (sin Id/CreatedAt/UpdatedAt: se regeneran al importar).</summary>
public record ProductExportDto(
    string Name,
    string Description,
    string ImageUrl,
    string WebsiteUrl,
    string ComposeTemplate,
    List<EnvVarDefinitionDto> EnvVarsSchema,
    bool IsActive);

/// <summary>
/// Formato versionado del export/import de productos, para poder evolucionarlo
/// en el futuro sin romper la importación de ficheros generados con una versión anterior.
/// </summary>
public record ProductExportEnvelope(int Version, DateTime ExportedAt, List<ProductExportDto> Products);

public record ImportedProductInfo(string Name, Guid Id);

public record ImportProductError(string Name, string Reason);

/// <summary>Resumen del import "best effort": qué se creó, qué se actualizó y qué falló (con motivo).</summary>
public record ImportProductsResult(
    List<ImportedProductInfo> Created,
    List<ImportedProductInfo> Updated,
    List<ImportProductError> Errors);
