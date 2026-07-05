namespace AhoraCenit.Api.Services;

public class SmtpOptions
{
    public const string SectionName = "Smtp";

    /// <summary>
    /// Si está vacío, no hay SMTP configurado y los emails se escriben en el
    /// log de la aplicación en vez de enviarse de verdad (modo dev).
    /// </summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public string User { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string From { get; set; } = "no-reply@ahoracenit.com";

    public bool EnableSsl { get; set; } = true;
}
