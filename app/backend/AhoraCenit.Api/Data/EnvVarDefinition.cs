using System.Text.Json;
using System.Text.Json.Serialization;

namespace AhoraCenit.Api.Data;

/// <summary>
/// Controla qué puede hacer el usuario final con una variable de entorno al desplegar un producto.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EnvVarInputMode
{
    /// <summary>Campo de texto libre, visible y editable.</summary>
    Text,

    /// <summary>Visible y editable, pero enmascarado como contraseña.</summary>
    Secret,

    /// <summary>Visible pero no editable: se muestra siempre el valor por defecto.</summary>
    ReadOnly,

    /// <summary>Ni visible ni editable para el usuario: se aplica siempre el valor por defecto.</summary>
    Hidden
}

/// <summary>
/// Describes one configurable environment variable exposed by a Product's
/// compose template. Stored as part of Product.EnvVarsSchemaJson.
/// </summary>
[JsonConverter(typeof(EnvVarDefinitionJsonConverter))]
public class EnvVarDefinition
{
    public required string Key { get; set; }

    public required string Label { get; set; }

    public string DefaultValue { get; set; } = string.Empty;

    public EnvVarInputMode Mode { get; set; } = EnvVarInputMode.Text;
}

/// <summary>
/// Lee tanto el esquema nuevo (<c>Mode</c>) como el antiguo (<c>IsSecret</c> booleano),
/// para no perder la configuración de los productos guardados antes de introducir los
/// cuatro estados de visibilidad.
/// </summary>
public class EnvVarDefinitionJsonConverter : JsonConverter<EnvVarDefinition>
{
    public override EnvVarDefinition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var key = GetString(root, "Key") ?? string.Empty;
        var label = GetString(root, "Label") ?? string.Empty;
        var defaultValue = GetString(root, "DefaultValue") ?? string.Empty;

        var mode = EnvVarInputMode.Text;
        if (TryGetPropertyIgnoreCase(root, "Mode", out var modeElement))
        {
            if (modeElement.ValueKind == JsonValueKind.String
                && Enum.TryParse<EnvVarInputMode>(modeElement.GetString(), ignoreCase: true, out var parsedMode))
            {
                mode = parsedMode;
            }
            else if (modeElement.ValueKind == JsonValueKind.Number
                && modeElement.TryGetInt32(out var numericMode)
                && Enum.IsDefined(typeof(EnvVarInputMode), numericMode))
            {
                mode = (EnvVarInputMode)numericMode;
            }
        }
        else if (TryGetPropertyIgnoreCase(root, "IsSecret", out var legacyElement)
            && legacyElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            mode = legacyElement.GetBoolean() ? EnvVarInputMode.Secret : EnvVarInputMode.Text;
        }

        return new EnvVarDefinition
        {
            Key = key,
            Label = label,
            DefaultValue = defaultValue,
            Mode = mode
        };
    }

    public override void Write(Utf8JsonWriter writer, EnvVarDefinition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Key", value.Key);
        writer.WriteString("Label", value.Label);
        writer.WriteString("DefaultValue", value.DefaultValue);
        writer.WriteString("Mode", value.Mode.ToString());
        writer.WriteEndObject();
    }

    private static string? GetString(JsonElement root, string propertyName) =>
        TryGetPropertyIgnoreCase(root, propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static bool TryGetPropertyIgnoreCase(JsonElement root, string propertyName, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
