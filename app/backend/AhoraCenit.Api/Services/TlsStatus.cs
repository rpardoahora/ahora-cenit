using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace AhoraCenit.Api.Services;

/// <summary>
/// Comprueba si Traefik ya está sirviendo el certificado TLS real de un
/// subdominio, o si de momento sigue devolviendo su certificado interno por
/// defecto ("TRAEFIK DEFAULT CERT") mientras el resolver ACME lo obtiene.
/// </summary>
public static class TlsStatus
{
    private const string TraefikDefaultCertSubject = "TRAEFIK DEFAULT CERT";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Los dominios de desarrollo (*.localhost) se sirven por HTTP sin TLS,
    /// así que no tiene sentido comprobar el certificado.
    /// </summary>
    public static bool RequiresCertificateCheck(string baseDomain) =>
        !baseDomain.Contains("localhost", StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> IsCertificateReadyAsync(string host, CancellationToken ct)
    {
        try
        {
            using var tcpClient = new TcpClient();
            var connectTask = tcpClient.ConnectAsync(host, 443, ct).AsTask();
            var completed = await Task.WhenAny(connectTask, Task.Delay(ConnectTimeout, ct));
            if (completed != connectTask || !tcpClient.Connected)
            {
                return false;
            }

            using var sslStream = new SslStream(
                tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true);

            await sslStream.AuthenticateAsClientAsync(host);

            using var certificate = sslStream.RemoteCertificate is null
                ? null
                : new X509Certificate2(sslStream.RemoteCertificate);

            if (certificate is null)
            {
                return false;
            }

            return !certificate.Subject.Contains(TraefikDefaultCertSubject, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
