namespace AhoraCenit.Api.Data;

/// <summary>
/// Ajuste del portal editable desde el panel de administración (clave/valor).
/// Si una clave no existe todavía, manda el valor inicial de la configuración
/// (variables de entorno); en cuanto un admin la cambia, manda la base de datos.
/// </summary>
public class AppSetting
{
    public required string Key { get; set; }

    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
