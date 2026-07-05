namespace AhoraCenit.Api.Contracts.Applications;

public record CreateApplicationRequest(
    Guid ProductId,
    string? Subdomain,
    Dictionary<string, string>? EnvVars);

public record SetApplicationActiveRequest(bool IsActive);

public record ApplicationResponse(
    Guid Id,
    Guid ProductId,
    string ProductName,
    Guid UserId,
    string OwnerName,
    string OwnerClientSlug,
    string Subdomain,
    string FullDomain,
    Dictionary<string, string> EnvVarValues,
    string Status,
    int? PortainerStackId,
    int PortainerEndpointId,
    DateTime CreatedAt,
    DateTime UpdatedAt);
