using System.Text.RegularExpressions;
using AhoraCenit.Api.Contracts.Products;
using AhoraCenit.Api.Data;
using AhoraCenit.Api.Features.Applications;
using AhoraCenit.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AhoraCenit.Api.Features.Products;

/// <summary>
/// GET /api/products/{id}/overview: qué clientes tienen instalado el producto (y con qué versión, según
/// Portainer) y cuál es la última versión publicada de sus imágenes Docker y de su paquete NuGet.
/// </summary>
public static class ProductOverviewEndpoint
{
    private static readonly Regex ServiceLine = new(@"^  (?<name>[A-Za-z0-9._-]+):\s*(#.*)?$");
    private static readonly Regex ImageLine = new(@"^\s+image\s*:\s*[""']?(?<image>[^""'\s#]+)");
    private static readonly Regex EnvValue = new(@"^\s*-?\s*[""']?(?<key>DatabaseNuget__(PackageId|NugetUrl|NugetUser|NugetPassword))\s*[:=]\s*[""']?(?<value>[^""'#]*?)[""']?\s*$");
    private static readonly Regex Variable = new(@"^\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}$");

    private const int RecentCount = 5;

    public static async Task<IResult> GetAsync(
        Guid id,
        AppDbContext db,
        IPortainerClient portainer,
        IFeedVersionService feeds,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
        {
            return Results.NotFound();
        }

        var applications = await db.Applications.AsNoTracking()
            .Include(a => a.User)
            .Where(a => a.ProductId == id)
            .OrderBy(a => a.User!.Name).ThenBy(a => a.Subdomain)
            .ToListAsync(ct);

        // Las consultas a los feeds y a Portainer van en paralelo: cada una puede tardar unos segundos.
        var composeImages = ReadImages(product.ComposeTemplate);
        var imageTasks = composeImages
            .Select(async ci =>
            {
                var reference = ImageReference.Parse(ci.Image);
                if (reference is null)
                {
                    return new ProductImageInfo(ci.Service, ci.Image, null, null, false, [], null, "Imagen con variables: no se puede consultar.");
                }

                var versions = await feeds.GetImageTagsAsync(reference, ct);
                var update = reference.Tag is not null && versions.Latest is not null && VersionComparer.IsNewer(versions.Latest, reference.Tag);
                return new ProductImageInfo(ci.Service, reference.Display, reference.Tag, versions.Latest, update,
                    versions.Versions.Take(RecentCount).ToList(), versions.RepositoryUrl, versions.Error);
            })
            .ToList();
        var nugetTask = GetNugetAsync(product, feeds, ct);
        var installedTasks = applications
            .Select(a => a.PortainerStackId is null
                ? Task.FromResult<IReadOnlyList<StackServiceImage>?>(null)
                : portainer.ListStackImagesAsync(a.PortainerEndpointId, a.Subdomain, ct))
            .ToList();

        var images = await Task.WhenAll(imageTasks);
        var nuget = await nugetTask;
        var installed = await Task.WhenAll(installedTasks);

        // Última versión por repositorio, para marcar las instalaciones desfasadas.
        var latestByRepository = images.Where(i => i.LatestTag is not null)
            .GroupBy(i => i.Repository).ToDictionary(g => g.Key, g => g.First().LatestTag!);

        var installations = applications.Select((app, index) =>
        {
            var running = (installed[index] ?? [])
                .Select(s =>
                {
                    var reference = ImageReference.Parse(s.Image);
                    return (Info: new InstalledImage(s.Service, reference?.Display ?? s.Image, reference?.Tag, s.State), Reference: reference);
                })
                .ToList();
            var outdated = running.Any(r => r.Reference?.Tag is { } tag
                && latestByRepository.TryGetValue(r.Reference.Display, out var latest)
                && VersionComparer.IsNewer(latest, tag));
            var fullDomain = $"{app.Subdomain}.{configuration["BaseDomain"] ?? "ahoracenit.localhost"}";
            return new ProductInstallation(
                app.Id, app.Subdomain, ApplicationsEndpoints.BuildPublicUrl(fullDomain, configuration),
                app.UserId, app.User?.Name ?? string.Empty, app.User?.ClientSlug ?? string.Empty,
                app.Status.ToString(), app.CreatedAt, running.Select(r => r.Info).ToList(), outdated);
        }).ToList();

        return Results.Ok(new ProductOverviewResponse(product.Id, product.Name, images, nuget, installations));
    }

    /// <summary>Paquete NuGet del producto: el del compose (DatabaseNuget__*) o, si no lo dice, "&lt;producto&gt;.Conf.Database" en el repositorio de la plataforma.</summary>
    private static async Task<ProductNugetInfo?> GetNugetAsync(Product product, IFeedVersionService feeds, CancellationToken ct)
    {
        var values = new Dictionary<string, string>();
        foreach (var line in ActiveLines(product.ComposeTemplate))
        {
            var match = EnvValue.Match(line);
            if (match.Success)
            {
                values.TryAdd(match.Groups["key"].Value, match.Groups["value"].Value.Trim());
            }
        }

        // Los valores tipo ${NUGET_USER} salen de las variables del producto (su valor por defecto).
        var schema = ProductsEndpoints.DeserializeSchema(product.EnvVarsSchemaJson).ToDictionary(d => d.Key, d => d.DefaultValue);
        string? Resolve(string key)
        {
            if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var variable = Variable.Match(value);
            return variable.Success ? schema.GetValueOrDefault(variable.Groups["name"].Value) : value;
        }

        var packageId = Resolve("DatabaseNuget__PackageId");
        var inferred = packageId is null;
        packageId ??= $"{product.Name.Trim()}.Conf.Database";
        var feedUrl = Resolve("DatabaseNuget__NugetUrl");
        var versions = await feeds.GetNugetVersionsAsync(packageId, feedUrl, Resolve("DatabaseNuget__NugetUser"), Resolve("DatabaseNuget__NugetPassword"), ct);
        if (inferred && versions.Latest is null)
        {
            // Producto sin paquete NuGet (no es Flexygo, o aún no se ha publicado): no se muestra.
            return null;
        }

        return new ProductNugetInfo(packageId, inferred, feedUrl, versions.Latest, versions.Versions.Take(RecentCount).ToList(), versions.RepositoryUrl, versions.Error);
    }

    /// <summary>Servicio e imagen de cada servicio del compose (sin los comentados).</summary>
    private static List<(string Service, string Image)> ReadImages(string compose)
    {
        var result = new List<(string, string)>();
        string? service = null;
        var inServices = false;
        foreach (var line in ActiveLines(compose))
        {
            if (!line.StartsWith(' '))
            {
                inServices = line.TrimEnd().StartsWith("services:", StringComparison.Ordinal);
                service = null;
                continue;
            }

            if (!inServices)
            {
                continue;
            }

            var serviceMatch = ServiceLine.Match(line);
            if (serviceMatch.Success)
            {
                service = serviceMatch.Groups["name"].Value;
                continue;
            }

            var imageMatch = ImageLine.Match(line);
            if (service is not null && imageMatch.Success)
            {
                result.Add((service, imageMatch.Groups["image"].Value));
            }
        }

        return result;
    }

    private static IEnumerable<string> ActiveLines(string compose) =>
        compose.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
}
