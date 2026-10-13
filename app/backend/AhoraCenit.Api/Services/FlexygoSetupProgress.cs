using System.Text.Json;
using System.Text.RegularExpressions;

namespace AhoraCenit.Api.Services;

/// <summary>
/// Por dónde va el arranque de una aplicación que aún no responde, para enseñárselo al usuario. Step y StepCount
/// (si hay pasos contables) alimentan una barra "paso 3 de 6"; IsError marca algo que no se arreglará solo.
/// </summary>
public record StartupProgress(string Title, string? Detail = null, int? Step = null, int? StepCount = null, bool IsError = false);

/// <summary>
/// Traduce el tracker de Flexygo (GET /api/Setup/progress: Status, StatusTitle, StatusText y Error) a los pasos de la
/// instalación de una base de datos. El tracker se reinicia con cada base (la de configuración, *_IC, y después las
/// de datos) y StatusText es el último mensaje del despliegue del dacpac, que sigue siempre el mismo orden.
/// </summary>
public static partial class FlexygoSetupProgress
{
    public const int StepCount = 6;

    private static readonly (int Step, string Title)[] Steps =
    [
        (1, "Descargando el paquete de la base de datos"),
        (2, "Cargando el paquete"),
        (3, "Preparando el despliegue"),
        (4, "Creando las tablas y objetos"),
        (5, "Cargando los datos iniciales"),
        (6, "Base de datos lista")
    ];

    private static readonly Dictionary<string, string> ObjectTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Table"] = "Tabla",
        ["View"] = "Vista",
        ["Procedure"] = "Procedimiento",
        ["Function"] = "Función",
        ["Trigger"] = "Trigger",
        ["Index"] = "Índice",
        ["Foreign Key"] = "Clave ajena",
        ["Primary Key"] = "Clave primaria",
        ["Default Constraint"] = "Valor por defecto",
        ["Check Constraint"] = "Restricción",
        ["Unique Constraint"] = "Restricción única",
        ["User-Defined Data Type"] = "Tipo de datos",
        ["User-Defined Table Type"] = "Tipo de tabla",
        ["Schema"] = "Esquema",
        ["Sequence"] = "Secuencia"
    };

    // "Deploying package to database -> Creating Table [dbo].[Objects]..."
    [GeneratedRegex(@"->\s*(Creating|Altering|Dropping|Updating|Renaming|Rebuilding)\s+(?<type>[A-Za-z -]+?)\s+(?<name>\[.+?)\.{0,3}$")]
    private static partial Regex ObjectMessage();

    // "Deploying package to database -> toolbars_buttons.sql..."
    [GeneratedRegex(@"->\s*(?<file>[\w.-]+)\.sql\.{0,3}$", RegexOptions.IgnoreCase)]
    private static partial Regex DataFileMessage();

    // "Creating flxcat_flxCatalog_IC Database" / "Updating ... Database"
    [GeneratedRegex(@"^(Creating|Updating)\s+(?<db>\S+)\s+Database$")]
    private static partial Regex DatabaseTitle();

    /// <summary>Descripción del tracker mientras la instalación desatendida no ha terminado (Setup/ready no da 200).</summary>
    public static StartupProgress Describe(JsonElement tracker)
    {
        var status = tracker.TryGetProperty("Status", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
        var title = Text(tracker, "StatusTitle");
        var text = Text(tracker, "StatusText") ?? string.Empty;

        var database = title is not null && DatabaseTitle().Match(title) is { Success: true } m ? m.Groups["db"].Value : null;
        var isConf = database?.EndsWith("_IC", StringComparison.OrdinalIgnoreCase) == true;
        var databaseLabel = database is null
            ? "la base de datos"
            : isConf ? $"la base de datos de configuración ({database})" : $"la base de datos {database}";

        if (status == 3)
        {
            var error = tracker.TryGetProperty("Error", out var e) && e.ValueKind == JsonValueKind.Object
                ? Text(e, "Message") ?? Text(e, "Title")
                : null;
            return new StartupProgress($"Ha fallado la instalación de {databaseLabel}", error ?? text, IsError: true);
        }

        if (status == 2)
        {
            // Entre una base y la siguiente Flexygo configura el proyecto y el administrador; tras la última, termina.
            return isConf
                ? new StartupProgress("Configurando el proyecto y preparando las bases de datos de datos")
                : new StartupProgress("Terminando la instalación");
        }

        if (status != 1)
        {
            return new StartupProgress("Preparando la instalación de la aplicación");
        }

        var (step, detail) = StepOf(text);
        return new StartupProgress($"Instalando {databaseLabel}: {Steps[step - 1].Title.ToLowerInvariant()}", detail, step, StepCount);
    }

    private static (int Step, string? Detail) StepOf(string text)
    {
        if (ObjectMessage().Match(text) is { Success: true } obj)
        {
            var type = obj.Groups["type"].Value.Trim();
            return (4, $"{ObjectTypes.GetValueOrDefault(type, type)} {obj.Groups["name"].Value.TrimEnd('.')}");
        }

        if (DataFileMessage().Match(text) is { Success: true } file)
        {
            return (5, file.Groups["file"].Value);
        }

        if (text.Contains("Inserting data", StringComparison.OrdinalIgnoreCase))
        {
            return (5, null);
        }

        if (text.StartsWith("Deployment completed", StringComparison.OrdinalIgnoreCase))
        {
            return (6, null);
        }

        if (text.StartsWith("Deploy at", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Downloading package", StringComparison.OrdinalIgnoreCase))
        {
            return (1, null);
        }

        if (text.StartsWith("Package downloaded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dacpac", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Database deployment options", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Starting deployment", StringComparison.OrdinalIgnoreCase))
        {
            return (2, null);
        }

        // Plan del despliegue, creación de la base y migraciones previas: lo que queda antes de crear objetos.
        var arrow = text.IndexOf("->", StringComparison.Ordinal);
        return (3, arrow >= 0 ? text[(arrow + 2)..].Trim().TrimEnd('.') : null);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
}
