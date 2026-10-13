namespace AhoraCenit.Api.Contracts.Products;

/// <summary>Panel de un producto: versiones publicadas en sus feeds y clientes que lo tienen instalado.</summary>
public record ProductOverviewResponse(
    Guid ProductId,
    string ProductName,
    IReadOnlyList<ProductImageInfo> Images,
    ProductNugetInfo? Nuget,
    IReadOnlyList<ProductInstallation> Installations);

/// <param name="Service">Servicio del compose.</param>
/// <param name="Repository">Imagen sin tag (p. ej. "localhost:5000/flxcatalog-backend").</param>
/// <param name="ConfiguredTag">Tag que usa el compose del producto (lo que se instala ahora).</param>
/// <param name="LatestTag">Versión más alta publicada en el registry.</param>
/// <param name="RepositoryUrl">Enlace al repositorio de la imagen.</param>
public record ProductImageInfo(
    string Service,
    string Repository,
    string? ConfiguredTag,
    string? LatestTag,
    bool UpdateAvailable,
    IReadOnlyList<string> RecentTags,
    string? RepositoryUrl,
    string? Error);

/// <param name="PackageIdInferred">true si el compose no dice el paquete y se ha supuesto "&lt;producto&gt;.Conf.Database".</param>
/// <param name="FeedUrl">Feed configurado (DatabaseNuget__NugetUrl); null = el repositorio NuGet de la plataforma.</param>
public record ProductNugetInfo(
    string PackageId,
    bool PackageIdInferred,
    string? FeedUrl,
    string? LatestVersion,
    IReadOnlyList<string> RecentVersions,
    string? RepositoryUrl,
    string? Error);

/// <param name="Images">Imagen con la que corre cada servicio (según Portainer); vacío si no se pudo consultar.</param>
/// <param name="Outdated">Algún servicio corre con una versión anterior a la última publicada.</param>
public record ProductInstallation(
    Guid ApplicationId,
    string Subdomain,
    string Url,
    Guid UserId,
    string ClientName,
    string ClientSlug,
    string Status,
    DateTime CreatedAt,
    IReadOnlyList<InstalledImage> Images,
    bool Outdated);

public record InstalledImage(string Service, string Image, string? Tag, string State);
