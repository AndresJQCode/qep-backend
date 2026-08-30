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

        // GIN trigram, no btree: CompanyRepository.SearchAsync filtra `Name` con
        // `ILIKE '%termino%'` (comodin a ambos lados), y un btree normal solo acelera un
        // prefijo, no un "contains". Mismo razonamiento que `IX_customers_name_trgm`.
        company.HasIndex(value => value.Name)
            .HasDatabaseName("IX_companies_name_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");

        // La columna account_number y su IX_companies_tenant_account_number se fueron en EMP-08.
        // El indice hacia cumplir que dos empresas del mismo tenant no compartieran numero de
        // cuenta, y esa regla no salia de ningun requisito: RF-091 pide "registrar los datos de la
        // empresa, incluido el numero de cuenta" y nada mas. La unicidad que quedo —que una
        // empresa no repita la misma cuenta— es invariante del agregado y se comprueba en memoria.
        ConfigureBankAccounts(company);
    }

    /// <summary>
    /// Las cuentas bancarias, como coleccion **owned** en tabla propia.
    ///
    /// Owned y no una entidad con su propio DbSet porque una cuenta no existe fuera de su empresa
    /// y nadie la referencia desde afuera. Eso le da a EF la semantica que el PUT necesita gratis:
    /// asignar la coleccion entera borra las filas que ya no estan e inserta las nuevas, sin que
    /// el handler tenga que reconciliar fila por fila.
    ///
    /// La clave primaria la gestiona EF por convencion (owner + ordinal en shadow state). Deliberado:
    /// una clave propia visible al dominio seria un id que alguien terminaria usando como
    /// referencia estable, y no lo es — un PUT que reordene las cuentas las reescribe todas.
    /// </summary>
    private static void ConfigureBankAccounts(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Company> company) =>
        company.OwnsMany(value => value.BankAccounts, account =>
        {
            account.ToTable("company_bank_accounts", "companies");
            account.WithOwner().HasForeignKey("company_id");

            // La clave que EF crea por convencion se llama "Id"; renombrarla a ordinal la deja en
            // snake_case como el resto del esquema y dice lo que es. No la ve el dominio: es
            // shadow state, y ningun consumidor la recibe.
            account.Property<int>("Id").HasColumnName("ordinal");
            account.Property(value => value.BankName)
                .HasColumnName("bank_name")
                .HasMaxLength(CompanyBankAccount.BankNameMaxLength);
            account.Property(value => value.AccountNumber)
                .HasColumnName("account_number")
                .HasMaxLength(CompanyBankAccount.AccountNumberMaxLength);
            account.Property(value => value.Currency)
                .HasColumnName("currency")
                // varchar(3) y no char(3): character() rellena con espacios a la derecha, asi que
                // una comparacion contra "COP" dependeria de que el driver recorte. El dominio ya
                // garantiza que siempre son tres letras exactas.
                .HasMaxLength(CompanyBankAccount.CurrencyLength);

            // Buscar por numero de cuenta es lo que hace el listado (CompanyRepository.SearchAsync)
            // con `ILIKE '%termino%'` (comodin a ambos lados) — GIN trigram, no el btree que tenia
            // antes: un btree solo acelera un prefijo, no un "contains", y un trigram cubre igual
            // de bien la igualdad exacta, asi que no hace falta mantener los dos. No es unico: la
            // unicidad vive en el agregado, no aca.
            account.HasIndex(value => value.AccountNumber)
                .HasDatabaseName("IX_company_bank_accounts_account_number_trgm")
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
        });

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
