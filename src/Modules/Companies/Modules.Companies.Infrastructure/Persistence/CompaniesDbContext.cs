using Microsoft.EntityFrameworkCore;
using Modules.Companies.Domain;

namespace Modules.Companies.Infrastructure.Persistence;

public sealed class CompaniesDbContext(DbContextOptions<CompaniesDbContext> options)
    : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();

    internal DbSet<CompaniesOutboxMessage> Outbox => Set<CompaniesOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureCompany(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    private static void ConfigureCompany(ModelBuilder modelBuilder)
    {
        var company = modelBuilder.Entity<Company>();
        company.ToTable("companies", "companies");
        company.HasKey(value => value.Id);
        company.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new CompanyId(value))
            .ValueGeneratedNever();
        company.Property(value => value.TenantId).HasColumnName("tenant_id");
        company.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(Company.NameMaxLength);
        company.Property(value => value.AccountNumber)
            .HasColumnName("account_number")
            .HasMaxLength(Company.AccountNumberMaxLength);
        company.Property(value => value.TaxId)
            .HasColumnName("tax_id")
            .HasMaxLength(Company.TaxIdMaxLength);
        company.Property(value => value.IsActive).HasColumnName("is_active");
        company.Property(value => value.Phone)
            .HasColumnName("phone")
            .HasMaxLength(CompanyContactInfo.PhoneMaxLength);
        company.Property(value => value.Email)
            .HasColumnName("email")
            .HasMaxLength(CompanyContactInfo.EmailMaxLength);
        company.Property(value => value.Address)
            .HasColumnName("address")
            .HasMaxLength(CompanyContactInfo.AddressMaxLength);
        company.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        company.Property(value => value.CreatedAt).HasColumnName("created_at");
        company.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        company.HasIndex(value => value.TenantId).HasDatabaseName("IX_companies_tenant");

        // La unicidad que promete el numero de cuenta. Nombrado a proposito: la capa de
        // infraestructura discrimina la violacion de unicidad por nombre de indice y no solo por
        // SqlState, porque si no, otros indices unicos de esta base se reportarian con el codigo
        // de dominio equivocado. Esa fue la leccion de SDD-CT-06.
        //
        // **Sin filtro parcial**: desactivar una empresa no libera su numero de cuenta. De eso
        // depende que Company.Activate no tenga que revalidar unicidad.
        company.HasIndex(value => new { value.TenantId, value.AccountNumber })
            .IsUnique()
            .HasDatabaseName("IX_companies_tenant_account_number");
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<CompaniesOutboxMessage>();
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
