namespace AhoraCenit.Api.Contracts.Applications;

/// <param name="UserId">
/// Cliente propietario de la aplicación. Solo un admin (o el token de API) puede
/// indicarlo, para desplegar en nombre de un cliente; si se omite, la
/// aplicación es de quien hace la petición.
/// </param>
/// <param name="AdminPassword">
/// Contraseña inicial del administrador de la aplicación. Solo se usa en modo motor SQL común, donde la
/// aplicación se instala sola al arrancar (Flexygo: Setup__AdminPassword); en ese caso es obligatoria.
/// </param>
/// <param name="SqlMode">
/// Solo administradores: "Dedicated" o "Shared" para esta instancia. Si se omite (y siempre para un
/// cliente) se usa el modo de los ajustes del portal.
/// </param>
public record CreateApplicationRequest(
    Guid ProductId,
    string? Subdomain,
    Dictionary<string, string>? EnvVars,
    Guid? UserId = null,
    string? AdminPassword = null,
    string? SqlMode = null);

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
    DateTime UpdatedAt,
    // Mientras está en Starting (o en Error por un servicio que no arranca): por dónde va. Solo lo trae el refresco de estado.
    AhoraCenit.Api.Services.StartupProgress? Startup = null);

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
