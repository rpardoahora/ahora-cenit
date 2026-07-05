using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace AhoraCenit.Api.Services;

public class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string bodyHtml, CancellationToken ct = default)
    {
        var smtp = options.Value;

        using var client = new SmtpClient(smtp.Host, smtp.Port)
        {
            EnableSsl = smtp.EnableSsl,
            Credentials = string.IsNullOrEmpty(smtp.User)
                ? null
                : new NetworkCredential(smtp.User, smtp.Password)
        };

        using var message = new MailMessage(smtp.From, to, subject, bodyHtml)
        {
            IsBodyHtml = true
        };

        await client.SendMailAsync(message, ct);
    }
}
