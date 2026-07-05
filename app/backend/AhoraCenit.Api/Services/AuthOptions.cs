namespace AhoraCenit.Api.Services;

public class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Si es true, los usuarios deben confirmar su email (enlace enviado por
    /// correo) antes de poder iniciar sesión. Desactivado por defecto (dev);
    /// se activa por configuración en producción.
    /// </summary>
    public bool RequireEmailConfirmation { get; set; }

    /// <summary>
    /// Minutos de validez de los tokens de confirmación de email / reseteo de contraseña.
    /// </summary>
    public int EmailConfirmationTokenExpiresMinutes { get; set; } = 1440;

    public int PasswordResetTokenExpiresMinutes { get; set; } = 60;

    /// <summary>
    /// URL pública base (esquema + dominio) usada para componer los enlaces
    /// de los emails, p.ej. "http://ahoracenit.localhost" o "https://ahoracenit.com".
    /// </summary>
    public string PublicBaseUrl { get; set; } = "http://ahoracenit.localhost";
}
