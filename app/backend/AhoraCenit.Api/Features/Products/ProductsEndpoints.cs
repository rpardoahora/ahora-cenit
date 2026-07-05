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
        group.MapPost("/", CreateAsync).RequireAuthorization("AdminOnly");
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization("AdminOnly");
        group.MapPatch("/{id:guid}/active", SetActiveAsync).RequireAuthorization("AdminOnly");
        group.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization("AdminOnly");

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

    private static async Task<IResult> CreateAsync(
        CreateProductRequest request,
        AppDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ComposeTemplate))
        {
            return Results.BadRequest(new { message = "Name y ComposeTemplate son obligatorios." });
        }

        var product = new Product
        {
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            ImageUrl = request.ImageUrl?.Trim() ?? string.Empty,
            ComposeTemplate = request.ComposeTemplate,
            EnvVarsSchemaJson = SerializeSchema(request.EnvVarsSchema),
            IsActive = request.IsActive
        };

        db.Products.Add(product);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/products/{product.Id}", ToResponse(product));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateProductRequest request,
        AppDbContext db,
        CancellationToken ct)
    {
        var product = await db.Products.FindAsync([id], ct);
        if (product is null)
        {
            return Results.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ComposeTemplate))
        {
            return Results.BadRequest(new { message = "Name y ComposeTemplate son obligatorios." });
        }

        product.Name = request.Name.Trim();
        product.Description = request.Description?.Trim() ?? string.Empty;
        product.ImageUrl = request.ImageUrl?.Trim() ?? string.Empty;
        product.ComposeTemplate = request.ComposeTemplate;
        product.EnvVarsSchemaJson = SerializeSchema(request.EnvVarsSchema);
        product.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(product));
    }

    private static async Task<IResult> SetActiveAsync(
        Guid id,
        SetProductActiveRequest request,
        AppDbContext db,
        CancellationToken ct)
    {
        var product = await db.Products.FindAsync([id], ct);
        if (product is null)
        {
            return Results.NotFound();
        }

        product.IsActive = request.IsActive;
        product.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(product));
    }

    private static async Task<IResult> DeleteAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
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

        db.Products.Remove(product);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static string SerializeSchema(List<EnvVarDefinitionDto>? schema)
    {
        var definitions = (schema ?? [])
            .Select(d => new EnvVarDefinition
            {
                Key = d.Key,
                Label = d.Label,
                DefaultValue = d.DefaultValue,
                IsSecret = d.IsSecret
            })
            .ToList();

        return JsonSerializer.Serialize(definitions, JsonOptions);
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
            .Select(d => new EnvVarDefinitionDto(d.Key, d.Label, d.DefaultValue, d.IsSecret))
            .ToList();

        return new ProductResponse(
            product.Id,
            product.Name,
            product.Description,
            product.ImageUrl,
            product.ComposeTemplate,
            schema,
            product.IsActive,
            product.CreatedAt,
            product.UpdatedAt);
    }
}
