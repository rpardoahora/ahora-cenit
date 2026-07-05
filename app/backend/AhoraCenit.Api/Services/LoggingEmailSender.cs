namespace AhoraCenit.Api.Services;

/// <summary>
/// Fallback usado cuando no hay SMTP configurado (<see cref="SmtpOptions.Host"/>
/// vacío): en vez de enviar el email de verdad, lo escribe en el log para
/// poder probar los flujos de confirmación/reseteo en dev sin un servidor SMTP real.
/// </summary>
public class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string bodyHtml, CancellationToken ct = default)
    {
        logger.LogInformation(
            "[EMAIL simulado, no hay SMTP configurado] Para: {To} | Asunto: {Subject}\n{Body}",
            to, subject, bodyHtml);
        return Task.CompletedTask;
    }
}
