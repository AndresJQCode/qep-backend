using Microsoft.EntityFrameworkCore;
using Modules.Pricing.Domain;

namespace Modules.Pricing.Infrastructure.Persistence;

public sealed class PricingDbContext(DbContextOptions<PricingDbContext> options)
    : DbContext(options)
{
    public DbSet<PriceList> PriceLists => Set<PriceList>();

    internal DbSet<PricingOutboxMessage> Outbox => Set<PricingOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigurePriceList(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    private static void ConfigurePriceList(ModelBuilder modelBuilder)
    {
        var priceList = modelBuilder.Entity<PriceList>();
        priceList.ToTable("price_lists", "pricing");
        priceList.HasKey(value => value.Id);
        priceList.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new PriceListId(value))
            .ValueGeneratedNever();
        priceList.Property(value => value.TenantId).HasColumnName("tenant_id");
        priceList.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(PriceList.NameMaxLength);
        priceList.Property(value => value.Prefix)
            .HasColumnName("prefix")
            .HasMaxLength(PriceList.PrefixMaxLength);
        priceList.Property(value => value.IsActive).HasColumnName("is_active");
        priceList.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        priceList.Property(value => value.CreatedAt).HasColumnName("created_at");
        priceList.Property(value => value.UpdatedAt).HasColumnName("updated_at");

        priceList.HasIndex(value => value.TenantId).HasDatabaseName("IX_price_lists_tenant");

        // La unicidad que promete el nombre. Nombrado a proposito: la capa de infraestructura
        // discrimina la violacion de unicidad por nombre de indice y no solo por SqlState, mismo
        // criterio que ClientClassification y TaxRate — la leccion de SDD-CT-06.
        //
        // Sin filtro parcial: desactivar una lista nunca libera su nombre ni su prefijo, asi que
        // PriceList.Activate no tiene que revalidar unicidad.
        priceList.HasIndex(value => new { value.TenantId, value.Name })
            .IsUnique()
            .HasDatabaseName("IX_price_lists_tenant_name");

        priceList.HasIndex(value => new { value.TenantId, value.Prefix })
            .IsUnique()
            .HasDatabaseName("IX_price_lists_tenant_prefix");
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<PricingOutboxMessage>();
        outbox.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
        outbox.HasKey(value => value.Id);
        outbox.Property(value => value.Id).HasColumnName("id");
        outbox.Property(value => value.EventName).HasColumnName("event_name").HasMaxLength(200);
        outbox.Property(value => value.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
        outbox.Property(value => value.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
        outbox.Property(value => value.OccurredAt).HasColumnName("occurred_at");
        outbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        outbox.Property(value => value.Attempts).HasColumnName("attempts");
        outbox.Property(value => value.LastError).HasColumnName("last_error");
    }
}
