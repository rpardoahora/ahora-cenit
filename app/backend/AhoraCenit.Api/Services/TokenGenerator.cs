using System.Security.Cryptography;

namespace AhoraCenit.Api.Services;

public static class TokenGenerator
{
    /// <summary>
    /// Token aleatorio criptográficamente seguro, URL-safe, para confirmación
    /// de email y reseteo de contraseña.
    /// </summary>
    public static string Create() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
