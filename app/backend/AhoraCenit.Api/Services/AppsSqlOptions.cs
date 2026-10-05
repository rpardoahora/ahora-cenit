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
}
