using Microsoft.EntityFrameworkCore;

namespace AhoraCenit.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Application> Applications => Set<Application>();

    public DbSet<DeploymentDurationSample> DeploymentDurationSamples => Set<DeploymentDurationSample>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Email).IsUnique();
            entity.HasIndex(u => u.ClientSlug).IsUnique();
            entity.Property(u => u.Email).HasMaxLength(256).IsRequired();
            entity.Property(u => u.Name).HasMaxLength(200).IsRequired();
            entity.Property(u => u.ClientSlug).HasMaxLength(200).IsRequired();
            entity.Property(u => u.Role).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.Property(p => p.Name).HasMaxLength(200).IsRequired();
            entity.Property(p => p.ComposeTemplate).IsRequired();
            entity.Property(p => p.EnvVarsSchemaJson)
                .HasColumnType("nvarchar(max)")
                .IsRequired();
        });

        modelBuilder.Entity<Application>(entity =>
        {
            entity.HasIndex(a => a.Subdomain).IsUnique();
            entity.Property(a => a.Subdomain).HasMaxLength(255).IsRequired();
            entity.Property(a => a.EnvVarValuesJson)
                .HasColumnType("nvarchar(max)")
                .IsRequired();
            entity.Property(a => a.Status).HasConversion<string>().HasMaxLength(32);

            entity.HasOne(a => a.Product)
                .WithMany(p => p.Applications)
                .HasForeignKey(a => a.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(a => a.User)
                .WithMany(u => u.Applications)
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeploymentDurationSample>(entity =>
        {
            entity.HasIndex(s => new { s.ProductId, s.CreatedAt });

            entity.HasOne(s => s.Product)
                .WithMany()
                .HasForeignKey(s => s.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
