using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modules.Suppliers.Domain;

namespace Modules.Suppliers.Infrastructure.Persistence;

public sealed class SuppliersDbContext(DbContextOptions<SuppliersDbContext> options)
    : DbContext(options)
{
    public DbSet<Supplier> Suppliers => Set<Supplier>();

    internal DbSet<SuppliersOutboxMessage> Outbox => Set<SuppliersOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureSupplier(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    private static void ConfigureSupplier(ModelBuilder modelBuilder)
    {
        var supplier = modelBuilder.Entity<Supplier>();
        supplier.ToTable("suppliers", "suppliers");
        supplier.HasKey(value => value.Id);
        supplier.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new SupplierId(value))
            .ValueGeneratedNever();
        supplier.Property(value => value.TenantId).HasColumnName("tenant_id");
        supplier.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(Supplier.NameMaxLength);
        supplier.Property(value => value.BusinessName)
            .HasColumnName("business_name")
            .HasMaxLength(Supplier.BusinessNameMaxLength);

        // El documento (tipo + número) es la clave de unicidad del proveedor dentro del tenant,
        // también entre inactivos (spec D-5): por eso el índice NO lleva filtro parcial. Es un
        // complex type y no dos columnas sueltas porque el agregado lo expone como value object.
        supplier.ComplexProperty(value => value.Identification, identification =>
        {
            identification.Property(value => value.Type)
                .HasColumnName("identification_type")
                .HasConversion<string>()
                .HasMaxLength(32);
            identification.Property(value => value.Number)
                .HasColumnName("identification_number")
                .HasMaxLength(SupplierIdentification.NumberMaxLength);
        });

        supplier.ComplexProperty(value => value.Contact, contact =>
        {
            contact.Property(value => value.Phone)
                .HasColumnName("phone")
                .HasMaxLength(SupplierContactInfo.PhoneMaxLength);
            contact.Property(value => value.Email)
                .HasColumnName("email")
                .HasMaxLength(SupplierContactInfo.EmailMaxLength);
            contact.Property(value => value.Address)
                .HasColumnName("address")
                .HasMaxLength(SupplierContactInfo.AddressMaxLength);
            contact.Property(value => value.Country)
                .HasColumnName("country")
                .HasMaxLength(SupplierContactInfo.CountryLength);
        });

        // FK a geography.cities(id): otro esquema, de otro módulo, de otro DbContext. EF Core no
        // modela una relación hacia una entidad que no está en su ModelBuilder, así que la FK real
        // (FK_suppliers_cities_city_id) la agrega a mano la migración. Nula fuera de Colombia.
        supplier.Property(value => value.CityId).HasColumnName("city_id");
        supplier.HasIndex(value => value.CityId).HasDatabaseName("IX_suppliers_city");
        supplier.Property(value => value.CityName)
            .HasColumnName("city_name")
            .HasMaxLength(Supplier.CityNameMaxLength);
        supplier.Property(value => value.PhoneE164)
            .HasColumnName("phone_e164")
            .HasMaxLength(16);
        supplier.Property(value => value.PaymentTermDays).HasColumnName("payment_term_days");
        supplier.Property(value => value.Category)
            .HasColumnName("category")
            .HasMaxLength(Supplier.CategoryMaxLength);
        supplier.Property(value => value.Notes)
            .HasColumnName("notes")
            .HasMaxLength(Supplier.NotesMaxLength);
        supplier.Property(value => value.IsActive).HasColumnName("is_active");
        supplier.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        supplier.Property(value => value.CreatedAt).HasColumnName("created_at");
        supplier.Property(value => value.UpdatedAt).HasColumnName("updated_at");

        // Dos índices sobre columnas de un complex type no se declaran acá: EF Core no admite
        // HasIndex sobre propiedades de un complex type (ni con lambda ni con ruta de texto), así
        // que la migración InitialSuppliers los crea a mano, igual que la FK a geography.cities:
        //   - IX_suppliers_tenant_identification: único (tenant_id, identification_type,
        //     identification_number), SIN filtro parcial (D-5). La arbitra el UnitOfWork por nombre.
        //   - IX_suppliers_identification_number_trgm: GIN trigram para el ILIKE del listado.
        // Quien cambie esas columnas tiene que tocar la migración: el modelo no lo detecta.

        supplier.HasIndex(value => new { value.TenantId, value.Category })
            .HasDatabaseName("IX_suppliers_tenant_category");

        // GIN trigram y no btree: el listado filtra con `ILIKE '%término%'` (comodín a ambos
        // lados) y un btree sólo acelera un prefijo.
        supplier.HasIndex(value => value.Name)
            .HasDatabaseName("IX_suppliers_name_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
        supplier.HasIndex(value => value.BusinessName)
            .HasDatabaseName("IX_suppliers_business_name_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");

        ConfigureBankAccounts(supplier);
        ConfigureContacts(supplier);
    }

    /// <summary>
    /// Las cuentas bancarias, como colección <b>owned</b> en tabla propia: una cuenta no existe
    /// fuera de su proveedor, y asignar la colección entera borra las filas que ya no están e
    /// inserta las nuevas, sin que el handler reconcilie fila por fila. La clave (owner + ordinal
    /// en shadow state) la gestiona EF: un id visible al dominio terminaría usándose como
    /// referencia estable, y no lo es — un PUT que reordene las cuentas las reescribe todas.
    /// </summary>
    private static void ConfigureBankAccounts(EntityTypeBuilder<Supplier> supplier) =>
        supplier.OwnsMany(value => value.BankAccounts, account =>
        {
            account.ToTable("supplier_bank_accounts", "suppliers");
            account.WithOwner().HasForeignKey("supplier_id");
            account.Property<int>("Id").HasColumnName("ordinal");
            account.Property(value => value.BankName)
                .HasColumnName("bank_name")
                .HasMaxLength(SupplierBankAccount.BankNameMaxLength);
            account.Property(value => value.AccountNumber)
                .HasColumnName("account_number")
                .HasMaxLength(SupplierBankAccount.AccountNumberMaxLength);
            // varchar(3) y no char(3): character() rellena con espacios a la derecha.
            account.Property(value => value.Currency)
                .HasColumnName("currency")
                .HasMaxLength(SupplierBankAccount.CurrencyLength);
        });

    // Los contactos, owned por la misma razón y con la misma técnica que las cuentas bancarias.
    private static void ConfigureContacts(EntityTypeBuilder<Supplier> supplier) =>
        supplier.OwnsMany(value => value.Contacts, contact =>
        {
            contact.ToTable("supplier_contacts", "suppliers");
            contact.WithOwner().HasForeignKey("supplier_id");
            contact.Property<int>("Id").HasColumnName("ordinal");
            contact.Property(value => value.Name)
                .HasColumnName("name")
                .HasMaxLength(SupplierContact.NameMaxLength);
            contact.Property(value => value.Role)
                .HasColumnName("role")
                .HasMaxLength(SupplierContact.RoleMaxLength);
            contact.Property(value => value.Phone)
                .HasColumnName("phone")
                .HasMaxLength(SupplierContact.PhoneMaxLength);
            contact.Property(value => value.Email)
                .HasColumnName("email")
                .HasMaxLength(SupplierContact.EmailMaxLength);
        });

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<SuppliersOutboxMessage>();
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
