using Microsoft.EntityFrameworkCore;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

public sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options)
    : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    internal DbSet<CustomerCucCounter> CucCounters => Set<CustomerCucCounter>();

    internal DbSet<CustomersOutboxMessage> Outbox => Set<CustomersOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureCustomer(modelBuilder);
        ConfigureCucCounter(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    private static void ConfigureCustomer(ModelBuilder modelBuilder)
    {
        var customer = modelBuilder.Entity<Customer>();
        customer.ToTable("customers", "customers");
        customer.HasKey(value => value.Id);
        customer.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new CustomerId(value))
            .ValueGeneratedNever();
        customer.Property(value => value.TenantId).HasColumnName("tenant_id");
        customer.Property(value => value.Cuc)
            .HasColumnName("cuc")
            .HasMaxLength(Customer.CucMaxLength);
        customer.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(Customer.NameMaxLength);

        // La identificacion se guarda en dos columnas planas. El agregado la expone ademas como
        // value object calculado (Customer.Identification), que es lo que usan las firmas de
        // Create y Update; esa propiedad se ignora porque no tiene columna propia.
        //
        // Planas y no un complex type: EF Core no soporta indices sobre las propiedades de un
        // complex type, y estas dos son justamente la clave unica del cliente.
        customer.Ignore(value => value.Identification);
        customer.Property(value => value.IdentificationType)
            .HasColumnName("identification_type")
            .HasConversion<string>()
            .HasMaxLength(32);
        customer.Property(value => value.IdentificationNumber)
            .HasColumnName("identification_number")
            .HasMaxLength(CustomerIdentification.NumberMaxLength);

        customer.Property(value => value.IsActive).HasColumnName("is_active");
        customer.Property(value => value.Phone)
            .HasColumnName("phone")
            .HasMaxLength(CustomerContactInfo.PhoneMaxLength);
        customer.Property(value => value.Email)
            .HasColumnName("email")
            .HasMaxLength(CustomerContactInfo.EmailMaxLength);
        customer.Property(value => value.Address)
            .HasColumnName("address")
            .HasMaxLength(CustomerContactInfo.AddressMaxLength);
        customer.Property(value => value.Department)
            .HasColumnName("department")
            .HasMaxLength(CustomerContactInfo.DepartmentMaxLength);
        customer.Property(value => value.City)
            .HasColumnName("city")
            .HasMaxLength(CustomerContactInfo.CityMaxLength);

        // Como cadena y no como int: el valor sobrevive a que alguien reordene el enum, cosa que
        // un ordinal no hace. Un reordenamiento silencioso convertiria a todos los clientes
        // "GRANDE" en "MEDIANO" sin una sola linea de migracion.
        customer.Property(value => value.Classification)
            .HasColumnName("classification")
            .HasConversion<string>()
            .HasMaxLength(32);

        // Sin clave foranea: el modulo `pricing` no existe todavia, asi que no hay tabla a la que
        // apuntar. Ver CustomerCommercialInfo.PriceListId.
        customer.Property(value => value.PriceListId).HasColumnName("price_list_id");
        customer.Property(value => value.WithRetention).HasColumnName("with_retention");
        customer.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        customer.Property(value => value.CreatedAt).HasColumnName("created_at");
        customer.Property(value => value.UpdatedAt).HasColumnName("updated_at");

        customer.HasIndex(value => value.TenantId).HasDatabaseName("IX_customers_tenant");

        // La unicidad que promete la identificacion. Nombrado a proposito: la capa de
        // infraestructura discrimina la violacion de unicidad por nombre de indice y no solo por
        // SqlState, porque si no, otros indices unicos de esta base se reportarian con el codigo de
        // dominio equivocado. Esa fue la leccion de SDD-CT-06.
        //
        // **Sin filtro parcial**: inactivar un cliente no libera su documento. De eso depende que
        // Customer.Activate no tenga que revalidar unicidad.
        customer.HasIndex(value => new
        {
            value.TenantId,
            value.IdentificationType,
            value.IdentificationNumber
        })
            .IsUnique()
            .HasDatabaseName("IX_customers_tenant_identification");

        // El CUC tambien es unico por tenant, y con su **propio** indice y su propio codigo de
        // dominio. Colapsarlo con el de identificacion en una sola rama de traduccion es
        // exactamente el defecto que SDD-CT-06 dejo documentado: el 23505 solo dice que se violo
        // alguno, y responder el codigo del otro manda a corregir el campo equivocado.
        customer.HasIndex(value => new { value.TenantId, value.Cuc })
            .IsUnique()
            .HasDatabaseName("IX_customers_tenant_cuc");
    }

    /// <summary>
    /// El consecutivo del CUC, una fila por tenant.
    ///
    /// Tabla propia y no un <c>MAX(cuc) + 1</c> sobre customers: dos altas concurrentes que lean
    /// el maximo emiten el mismo codigo, y el indice unico las rechaza a las dos con un 500 que el
    /// usuario no puede accionar. Con una fila por tenant, el <c>UPDATE ... RETURNING</c> la
    /// bloquea y serializa la emision — que es exactamente lo que un consecutivo necesita.
    /// </summary>
    private static void ConfigureCucCounter(ModelBuilder modelBuilder)
    {
        var counter = modelBuilder.Entity<CustomerCucCounter>();
        counter.ToTable("cuc_counters", "customers");
        counter.HasKey(value => value.TenantId);
        counter.Property(value => value.TenantId).HasColumnName("tenant_id");
        counter.Property(value => value.NextValue).HasColumnName("next_value");
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<CustomersOutboxMessage>();
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
