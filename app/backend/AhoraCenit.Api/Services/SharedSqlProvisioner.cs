using System.Security.Cryptography;
using AhoraCenit.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace AhoraCenit.Api.Services;

/// <summary>
/// Logins y bases de datos de los clientes en el SQL Server común (modo
/// <see cref="AppsSqlMode.Shared"/>). Cada cliente tiene un login propio que
/// comparten todas sus instancias; las bases que crea son suyas (db_owner) y no
/// ve ni puede tocar las de otros clientes ni la de la plataforma.
/// </summary>
public interface ISharedSqlProvisioner
{
    bool Enabled { get; }

    /// <summary>
    /// Asigna al cliente su login SQL si aún no lo tiene (el llamador guarda el
    /// usuario) y lo crea o sincroniza en el SQL Server común.
    /// </summary>
    Task EnsureClientLoginAsync(User client, CancellationToken ct = default);

    /// <summary>Borra las bases de una instancia (las del login del cliente con el prefijo de la instancia).</summary>
    Task DropApplicationDatabasesAsync(string login, string databasePrefix, CancellationToken ct = default);

    /// <summary>Borra el login del cliente y cualquier base que todavía sea suya.</summary>
    Task DropClientLoginAsync(string login, CancellationToken ct = default);
}

public class SharedSqlProvisioner(IConfiguration configuration, IOptions<AppsSqlOptions> options) : ISharedSqlProvisioner
{
    // Sin ';', '=' ni comillas: la contraseña va dentro de una cadena de conexión y de un .env.
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public bool Enabled => options.Value.Mode == AppsSqlMode.Shared;

    public async Task EnsureClientLoginAsync(User client, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(client.SqlLogin) || string.IsNullOrEmpty(client.SqlPassword))
        {
            client.SqlLogin = $"cenit_{client.ClientSlug.Replace('-', '_')}";
            if (client.SqlLogin.Length > 100)
            {
                client.SqlLogin = client.SqlLogin[..100];
            }

            // Mayúscula, minúscula, número y símbolo: lo exige la política de SQL Server.
            client.SqlPassword = RandomNumberGenerator.GetString(PasswordAlphabet, 32) + "Aa1-";
        }

        // CREATE ANY DATABASE en lugar del rol dbcreator: dbcreator permite además
        // modificar y borrar CUALQUIER base (la de otros clientes y la del portal).
        // Con este permiso el cliente solo puede crear bases, y las que crea son suyas.
        // DENY VIEW ANY DATABASE: solo ve sus propias bases (y master/tempdb).
        const string sql = """
            DECLARE @sql nvarchar(max);
            IF SUSER_ID(@login) IS NULL
                SET @sql = N'CREATE LOGIN ' + QUOTENAME(@login) + N' WITH PASSWORD = ' + QUOTENAME(@password, '''') + N', CHECK_EXPIRATION = OFF';
            ELSE
                SET @sql = N'ALTER LOGIN ' + QUOTENAME(@login) + N' WITH PASSWORD = ' + QUOTENAME(@password, '''');
            EXEC (@sql);
            SET @sql = N'GRANT CREATE ANY DATABASE TO ' + QUOTENAME(@login) + N'; DENY VIEW ANY DATABASE TO ' + QUOTENAME(@login) + N';';
            EXEC (@sql);
            """;

        await ExecuteAsync(sql, ct, ("@login", client.SqlLogin), ("@password", client.SqlPassword));
    }

    public Task DropApplicationDatabasesAsync(string login, string databasePrefix, CancellationToken ct = default) =>
        DropDatabasesAsync(login, databasePrefix, dropLogin: false, ct);

    public Task DropClientLoginAsync(string login, CancellationToken ct = default) =>
        DropDatabasesAsync(login, databasePrefix: null, dropLogin: true, ct);

    private async Task DropDatabasesAsync(string login, string? databasePrefix, bool dropLogin, CancellationToken ct)
    {
        const string sql = """
            DECLARE @sid varbinary(85) = SUSER_SID(@login);
            IF @sid IS NULL RETURN;

            DECLARE @db sysname, @sql nvarchar(max);
            DECLARE dbs CURSOR LOCAL FAST_FORWARD FOR
                SELECT name FROM sys.databases
                WHERE owner_sid = @sid
                  AND (@prefix IS NULL OR name LIKE REPLACE(@prefix, '_', '[_]') + '[_]%');
            OPEN dbs;
            FETCH NEXT FROM dbs INTO @db;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                SET @sql = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@db) + N';';
                EXEC (@sql);
                FETCH NEXT FROM dbs INTO @db;
            END
            CLOSE dbs;
            DEALLOCATE dbs;

            IF @dropLogin = 1
            BEGIN
                DECLARE @session int;
                DECLARE sessions CURSOR LOCAL FAST_FORWARD FOR
                    SELECT session_id FROM sys.dm_exec_sessions WHERE login_name = @login;
                OPEN sessions;
                FETCH NEXT FROM sessions INTO @session;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                    SET @sql = N'KILL ' + CAST(@session AS nvarchar(10));
                    EXEC (@sql);
                    FETCH NEXT FROM sessions INTO @session;
                END
                CLOSE sessions;
                DEALLOCATE sessions;

                SET @sql = N'DROP LOGIN ' + QUOTENAME(@login);
                EXEC (@sql);
            END
            """;

        await ExecuteAsync(sql, ct, ("@login", login), ("@prefix", (object?)databasePrefix ?? DBNull.Value), ("@dropLogin", dropLogin));
    }

    /// <summary>Ejecuta contra master del SQL Server de la plataforma (el de ConnectionStrings:Default, con sa).</summary>
    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        var builder = new SqlConnectionStringBuilder(configuration.GetConnectionString("Default")) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }
}

public static class SharedSqlLogins
{
    /// <summary>
    /// Al dar de alta un cliente en modo motor común, le crea su login SQL. Si falla
    /// no se bloquea el alta: se vuelve a intentar en su primer despliegue.
    /// </summary>
    public static async Task TryCreateAsync(
        User user, AppDbContext db, ISharedSqlProvisioner sharedSql, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!sharedSql.Enabled || user.Role != UserRole.Cliente)
        {
            return;
        }

        try
        {
            await sharedSql.EnsureClientLoginAsync(user, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("AhoraCenit.SharedSql").LogWarning(ex,
                "No se pudo crear el login SQL del cliente {ClientSlug}; se reintentará en su primer despliegue", user.ClientSlug);
        }
    }
}
