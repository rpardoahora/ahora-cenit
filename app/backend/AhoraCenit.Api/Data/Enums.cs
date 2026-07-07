namespace AhoraCenit.Api.Data;

public enum UserRole
{
    Admin = 0,
    Cliente = 1
}

public enum ApplicationStatus
{
    Deploying = 0,
    Running = 1,
    Stopped = 2,
    Error = 3,
    Deleted = 4,

    /// <summary>
    /// El contenedor ya está arriba, pero Traefik todavía no ha emitido/servido
    /// el certificado TLS del subdominio (solo aplica cuando BaseDomain no es
    /// un dominio local de desarrollo).
    /// </summary>
    Provisioning = 5
}
