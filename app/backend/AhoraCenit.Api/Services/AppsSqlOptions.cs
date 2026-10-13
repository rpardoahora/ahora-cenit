namespace AhoraCenit.Api.Services;

public enum AppsSqlMode
{
    /// <summary>Cada aplicación usa el SQL Server que traiga su compose (imagen propia).</summary>
    Dedicated,

    /// <summary>
    /// Todas las aplicaciones usan el SQL Server de la plataforma: al desplegar se
    /// desactivan los servicios SQL del compose y se reescriben las cadenas de conexión.
    /// </summary>
    Shared
}

/// <summary>
/// Cómo obtienen SQL Server las aplicaciones desplegadas. Lo decide el instalador
/// (APPS_SQL_MODE en infra/.env) y solo afecta a los despliegues nuevos.
/// </summary>
public class AppsSqlOptions
{
    public const string SectionName = "AppsSql";

    public AppsSqlMode Mode { get; set; } = AppsSqlMode.Dedicated;

    /// <summary>Nombre con el que las aplicaciones ven el SQL Server común (alias en la red <see cref="Network"/>).</summary>
    public string Host { get; set; } = "cenit-sqlserver";

    /// <summary>Red Docker externa que comparten el SQL Server común y los servicios que lo usan.</summary>
    public string Network { get; set; } = "cenit_sql";

    /// <summary>
    /// Repositorio NuGet (BaGetter) visto desde las aplicaciones (alias en la red <see cref="Network"/>).
    /// Las URLs del feed que apuntan al de la plataforma (localhost:5100, nuget.DOMINIO) se traducen a esta,
    /// porque desde dentro de un contenedor "localhost" es el propio contenedor.
    /// </summary>
    public string NugetInternalBaseUrl { get; set; } = "http://cenit-nuget:8080/";

    /// <summary>El feed (BaGetter tiene uno solo).</summary>
    public string NugetDefaultFeedPath { get; set; } = "v3/index.json";

    /// <summary>Direcciones públicas del repositorio NuGet de la plataforma, separadas por coma (NUGET_PUBLIC_URL, http://localhost:5100/).</summary>
    public string NugetPublicUrls { get; set; } = string.Empty;

    /// <summary>Usuario de solo lectura del repositorio NuGet que reciben las aplicaciones.</summary>
    public string NugetUser { get; set; } = string.Empty;

    public string NugetPassword { get; set; } = string.Empty;

    /// <summary>
    /// Traduce una URL del feed al repositorio interno si apunta al de la plataforma (cualquier ruta: hay un
    /// solo feed); null si es un feed externo.
    /// </summary>
    public string? ToInternalNugetUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var target))
        {
            return null;
        }

        foreach (var candidate in NugetPublicUrls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var platform)
                && string.Equals(platform.Host, target.Host, StringComparison.OrdinalIgnoreCase)
                && platform.Port == target.Port)
            {
                return NugetInternalBaseUrl.TrimEnd('/') + "/" + NugetDefaultFeedPath.TrimStart('/');
            }
        }

        return null;
    }
}
