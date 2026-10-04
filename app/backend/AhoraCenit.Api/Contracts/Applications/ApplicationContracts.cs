namespace AhoraCenit.Api.Contracts.Applications;

/// <param name="UserId">
/// Cliente propietario de la aplicación. Solo un admin (o el token de API) puede
/// indicarlo, para desplegar en nombre de un cliente; si se omite, la
/// aplicación es de quien hace la petición.
/// </param>
public record CreateApplicationRequest(
    Guid ProductId,
    string? Subdomain,
    Dictionary<string, string>? EnvVars,
    Guid? UserId = null);

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
    string Url,
    Dictionary<string, string> EnvVarValues,
    string Status,
    int? PortainerStackId,
    int PortainerEndpointId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record ApplicationUsageResponse(
    Guid ApplicationId,
    string Subdomain,
    string ProductName,
    Guid UserId,
    string OwnerName,
    string OwnerClientSlug,
    double CpuPercent,
    long MemoryUsageBytes,
    long DiskUsageBytes,
    int ContainerCount);
