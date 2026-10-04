using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using AhoraCenit.Api.Contracts.Products;
using AhoraCenit.Api.Data;
using AhoraCenit.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace AhoraCenit.Api.Features.Products;

public static class ProductsEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapProductsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", ListAsync).AllowAnonymous();
        group.MapGet("/{id:guid}", GetByIdAsync).AllowAnonymous();
        group.MapGet("/{id:guid}/suggest-subdomain", SuggestSubdomainAsync).RequireAuthorization();
        group.MapGet("/{id:guid}/deploy-stats", GetDeployStatsAsync).RequireAuthorization();
        group.MapPost("/", CreateAsync).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization("AdminOnly");
        group.MapPatch("/{id:guid}/active", SetActiveAsync).RequireAuthorization("AdminOnly");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization("AdminOnly");
        group.MapGet("/export", ExportAsync).RequireAuthorization("AdminOnly");
        group.MapPost("/import", ImportAsync).RequireAuthorization("AdminOnly");

        return group;
    }

    private static bool IsAdmin(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true && principal.IsAdmin();

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        bool includeInactive = false,
        CancellationToken ct = default)
    {
        var query = db.Products.AsQueryable();

        var admin = IsAdmin(principal);
        if (!admin || !includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        var products = await query.OrderBy(p => p.Name).ToListAsync(ct);
        return Results.Ok(products.Select(ToResponse));
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken ct)
    {
        var product = await db.Products.FindAsync([id], ct);
        if (product is null)
        {
            return Results.NotFound();
        }

        if (!product.IsActive && !IsAdmin(principal))
        {
            return Results.NotFound();
        }

        return Results.Ok(ToResponse(product));
    }

    private static async Task<IResult> SuggestSubdomainAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken ct)
    {
        var product = await db.Products.FindAsync([id], ct);
        if (product is null)
        {
            return Results.NotFound();
        }

        var userId = principal.GetUserId();
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var baseSlug = SlugGenerator.Slugify(product.Name, user.Name);
        var suggested = await SlugGenerator.ResolveUniqueAsync(
            baseSlug,
            slug => db.Applications.AnyAsync(a => a.Subdomain == slug, ct));

        return Results.Ok(new SuggestSubdomainResponse(suggested));
    }

    /// <summary>Cuántas muestras recientes se usan para estimar la duración del próximo despliegue.</summary>
    private const int DeployStatsSampleWindow = 5;

    private static async Task<IResult> GetDeployStatsAsync(
        Guid id,
        AppDbContext db,
        CancellationToken ct)
    {
        var durations = await db.DeploymentDurationSamples
            .Where(s => s.ProductId == id)
            .OrderByDescending(s => s.CreatedAt)
            .Take(DeployStatsSampleWindow)
            .Select(s => s.DurationSeconds)
            .ToListAsync(ct);

        var average = durations.Count > 0 ? durations.Average() : (double?)null;
        return Results.Ok(new DeployStatsResponse(average, durations.Count));
    }

    private static async Task<IResult> CreateAsync(
        CreateProductRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ComposeTemplate))
        {
            return Results.BadRequest(new { message = "Name y ComposeTemplate son obligatorios." });
        }

        if (ValidateSchema(request.EnvVarsSchema) is { } schemaError)
        {
            return Results.BadRequest(new { message = schemaError });
        }

        var product = new Product
        {
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            ImageUrl = request.ImageUrl?.Trim() ?? string.Empty,
            WebsiteUrl = request.WebsiteUrl?.Trim() ?? string.Empty,
            ComposeTemplate = request.ComposeTemplate,
            EnvVarsSchemaJson = SerializeSchema(request.EnvVarsSchema),
            IsActive = request.IsActive
        };

        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        sw.Stop();

        await auditLogger.RecordAsync(
            principal, "Product", "Create", product.Id,
            parameters: new { request.Name, request.Description, request.WebsiteUrl, request.IsActive, EnvVarsSchema = RedactSchema(request.EnvVarsSchema) },
            result: new { product.Id },
            sw.Elapsed, success: true);

        return Results.Created($"/api/products/{product.Id}", ToResponse(product));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateProductRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var product = await db.Products.FindAsync([id], ct);
        if (product is null)
        {
            return Results.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ComposeTemplate))
        {
            return Results.BadRequest(new { message = "Name y ComposeTemplate son obligatorios." });
        }

        if (ValidateSchema(request.EnvVarsSchema) is { } schemaError)
        {
            return Results.BadRequest(new { message = schemaError });
        }

        product.Name = request.Name.Trim();
        product.Description = request.Description?.Trim() ?? string.Empty;
        product.ImageUrl = request.ImageUrl?.Trim() ?? string.Empty;
        product.WebsiteUrl = request.WebsiteUrl?.Trim() ?? string.Empty;
        product.ComposeTemplate = request.ComposeTemplate;
        product.EnvVarsSchemaJson = SerializeSchema(request.EnvVarsSchema);
        product.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        sw.Stop();

        await auditLogger.RecordAsync(
            principal, "Product", "Update", product.Id,
            parameters: new { request.Name, request.Description, request.WebsiteUrl, EnvVarsSchema = RedactSchema(request.EnvVarsSchema) },
            result: new { product.Id, product.UpdatedAt },
            sw.Elapsed, success: true);

        return Results.Ok(ToResponse(product));
    }

    private static async Task<IResult> SetActiveAsync(
        Guid id,
        SetProductActiveRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var product = await db.Products.FindAsync([id], ct);
        if (product is null)
        {
            return Results.NotFound();
        }

        var previousIsActive = product.IsActive;
        product.IsActive = request.IsActive;
        product.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        sw.Stop();

        await auditLogger.RecordAsync(
            principal, "Product", "SetActive", product.Id,
            parameters: new { request.IsActive },
            result: new { Previous = previousIsActive, Current = product.IsActive },
            sw.Elapsed, success: true);

        return Results.Ok(ToResponse(product));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var product = await db.Products.FindAsync([id], ct);
        if (product is null)
        {
            return Results.NotFound();
        }

        var hasApplications = await db.Applications.AnyAsync(a => a.ProductId == id, ct);
        if (hasApplications)
        {
            return Results.Conflict(new
            {
                message = "No se puede borrar el producto porque tiene aplicaciones (instancias) desplegadas."
            });
        }

        var deletedSnapshot = new { product.Name };

        db.Products.Remove(product);
        await db.SaveChangesAsync(ct);
        sw.Stop();

        await auditLogger.RecordAsync(
            principal, "Product", "Delete", id,
            parameters: null,
            result: deletedSnapshot,
            sw.Elapsed, success: true);

        return Results.NoContent();
    }

    /// <summary>Versión del formato de export/import de productos; súbela si cambias la forma del envelope.</summary>
    private const int ExportFormatVersion = 1;

    private static async Task<IResult> ExportAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var products = await db.Products.OrderBy(p => p.Name).ToListAsync(ct);
        var dtos = products.Select(ToExportDto).ToList();
        var envelope = new ProductExportEnvelope(ExportFormatVersion, DateTime.UtcNow, dtos);
        sw.Stop();

        await auditLogger.RecordAsync(
            principal, "Product", "Export", null,
            parameters: null,
            result: new { Count = dtos.Count },
            sw.Elapsed, success: true);

        return Results.Ok(envelope);
    }

    // Best-effort en vez de una única transacción: un fichero de export puede traer decenas de
    // productos y no queremos que uno mal formado (o con un ComposeTemplate vacío) tire abajo la
    // importación de los demás. Cada producto se procesa y persiste de forma independiente; el
    // admin recibe un resumen con lo creado, lo actualizado y el motivo de cada fallo.
    private static async Task<IResult> ImportAsync(
        ProductExportEnvelope request,
        ClaimsPrincipal principal,
        AppDbContext db,
        IAuditLogger auditLogger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var created = new List<ImportedProductInfo>();
        var updated = new List<ImportedProductInfo>();
        var errors = new List<ImportProductError>();
        var incoming = request.Products ?? [];

        foreach (var dto in incoming)
        {
            var name = dto.Name?.Trim() ?? string.Empty;
            var displayName = string.IsNullOrWhiteSpace(name) ? "(sin nombre)" : name;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dto.ComposeTemplate))
            {
                errors.Add(new ImportProductError(displayName, "Name y ComposeTemplate son obligatorios."));
                continue;
            }

            if (ValidateSchema(dto.EnvVarsSchema) is { } schemaError)
            {
                errors.Add(new ImportProductError(displayName, schemaError));
                continue;
            }

            try
            {
                var existing = await db.Products
                    .FirstOrDefaultAsync(p => p.Name.ToLower() == name.ToLower(), ct);

                if (existing is null)
                {
                    var product = new Product
                    {
                        Name = name,
                        Description = dto.Description?.Trim() ?? string.Empty,
                        ImageUrl = dto.ImageUrl?.Trim() ?? string.Empty,
                        WebsiteUrl = dto.WebsiteUrl?.Trim() ?? string.Empty,
                        ComposeTemplate = dto.ComposeTemplate,
                        EnvVarsSchemaJson = SerializeSchema(dto.EnvVarsSchema),
                        IsActive = dto.IsActive
                    };

                    db.Products.Add(product);
                    await db.SaveChangesAsync(ct);
                    created.Add(new ImportedProductInfo(product.Name, product.Id));
                }
                else
                {
                    existing.Description = dto.Description?.Trim() ?? string.Empty;
                    existing.ImageUrl = dto.ImageUrl?.Trim() ?? string.Empty;
                    existing.WebsiteUrl = dto.WebsiteUrl?.Trim() ?? string.Empty;
                    existing.ComposeTemplate = dto.ComposeTemplate;
                    existing.EnvVarsSchemaJson = SerializeSchema(dto.EnvVarsSchema);
                    existing.IsActive = dto.IsActive;
                    existing.UpdatedAt = DateTime.UtcNow;

                    await db.SaveChangesAsync(ct);
                    updated.Add(new ImportedProductInfo(existing.Name, existing.Id));
                }
            }
            catch (Exception ex)
            {
                errors.Add(new ImportProductError(displayName, ex.Message));
            }
        }

        sw.Stop();
        var result = new ImportProductsResult(created, updated, errors);

        await auditLogger.RecordAsync(
            principal, "Product", "Import", null,
            parameters: new { TotalCount = incoming.Count },
            result: new { CreatedCount = created.Count, UpdatedCount = updated.Count, ErrorCount = errors.Count },
            sw.Elapsed, success: errors.Count == 0);

        return Results.Ok(result);
    }

    private static ProductExportDto ToExportDto(Product product)
    {
        var schema = DeserializeSchema(product.EnvVarsSchemaJson)
            .Select(d => new EnvVarDefinitionDto(d.Key, d.Label, d.DefaultValue, d.Mode))
            .ToList();

        return new ProductExportDto(
            product.Name,
            product.Description,
            product.ImageUrl,
            product.WebsiteUrl,
            product.ComposeTemplate,
            schema,
            product.IsActive);
    }

    /// <summary>Sustituye el valor por defecto de las variables marcadas como secretas u ocultas antes de auditarlas/loguearlas.</summary>
    private static object? RedactSchema(List<EnvVarDefinitionDto>? schema) =>
        schema?.Select(d => new
        {
            d.Key,
            d.Label,
            DefaultValue = d.Mode is EnvVarInputMode.Secret or EnvVarInputMode.Hidden ? "***" : d.DefaultValue,
            d.Mode
        });

    private static string SerializeSchema(List<EnvVarDefinitionDto>? schema)
    {
        var definitions = (schema ?? [])
            .Select(d => new EnvVarDefinition
            {
                Key = d.Key.Trim(),
                Label = string.IsNullOrWhiteSpace(d.Label) ? d.Key.Trim() : d.Label.Trim(),
                DefaultValue = d.DefaultValue ?? string.Empty,
                Mode = d.Mode
            })
            .ToList();

        return JsonSerializer.Serialize(definitions, JsonOptions);
    }

    /// <summary>Variables que el portal inyecta siempre al desplegar: un producto no puede redefinirlas.</summary>
    private static readonly HashSet<string> ReservedEnvVarKeys = new(StringComparer.OrdinalIgnoreCase) { "APP_SUBDOMAIN", "BASE_DOMAIN" };

    private static readonly System.Text.RegularExpressions.Regex EnvVarKeyPattern = new("^[A-Za-z_][A-Za-z0-9_]*$");

    /// <summary>Devuelve un mensaje de error si el esquema de variables no es válido, o null si lo es.</summary>
    private static string? ValidateSchema(List<EnvVarDefinitionDto>? schema)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in schema ?? [])
        {
            var key = definition?.Key?.Trim();
            if (string.IsNullOrEmpty(key) || !EnvVarKeyPattern.IsMatch(key))
            {
                return $"La variable '{key}' no es válida: usa letras, números y '_' (sin empezar por número).";
            }

            if (ReservedEnvVarKeys.Contains(key))
            {
                return $"La variable '{key}' está reservada: el portal la inyecta siempre al desplegar.";
            }

            if (!seen.Add(key))
            {
                return $"La variable '{key}' está repetida.";
            }

            if (!Enum.IsDefined(definition!.Mode))
            {
                return $"El modo de la variable '{key}' no es válido (Text, Secret, ReadOnly o Hidden).";
            }
        }

        return null;
    }

    internal static List<EnvVarDefinition> DeserializeSchema(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<EnvVarDefinition>>(json, JsonOptions) ?? [];
    }

    private static ProductResponse ToResponse(Product product)
    {
        var schema = DeserializeSchema(product.EnvVarsSchemaJson)
            .Select(d => new EnvVarDefinitionDto(d.Key, d.Label, d.DefaultValue, d.Mode))
            .ToList();

        return new ProductResponse(
            product.Id,
            product.Name,
            product.Description,
            product.ImageUrl,
            product.WebsiteUrl,
            product.ComposeTemplate,
            schema,
            product.IsActive,
            product.CreatedAt,
            product.UpdatedAt);
    }
}
