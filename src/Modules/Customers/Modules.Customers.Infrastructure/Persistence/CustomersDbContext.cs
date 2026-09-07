using Microsoft.EntityFrameworkCore;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

public sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options)
    : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    internal DbSet<CustomerAddress> CustomerAddresses => Set<CustomerAddress>();

    public DbSet<ClientClassification> ClientClassifications => Set<ClientClassification>();

    internal DbSet<CustomerCucCounter> CucCounters => Set<CustomerCucCounter>();

    internal DbSet<CustomersOutboxMessage> Outbox => Set<CustomersOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureCustomer(modelBuilder);
        ConfigureCustomerAddress(modelBuilder);
        ConfigureClientClassification(modelBuilder);
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
        // Nullable: la razon social solo existe si el cliente es una empresa. Mismo ancho que
        // name -- es el mismo tipo de dato.
        customer.Property(value => value.BusinessName)
            .HasColumnName("business_name")
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
        // La clasificacion, FK compuesta (tenant_id, classification_id) a
        // customers.client_classifications(tenant_id, id) — compuesta y no simple sobre id, para
        // que un cliente no pueda referenciar la clasificacion de otro tenant. A diferencia de la
        // ciudad, ClientClassification SI vive en este mismo DbContext, asi que la relacion se
        // modela con HasOne/HasForeignKey normal y EF genera la migracion completa.
        customer.Property(value => value.ClassificationId)
            .HasColumnName("classification_id")
            .HasConversion(id => id.Value, value => new ClientClassificationId(value));
        customer.HasIndex(value => value.ClassificationId)
            .HasDatabaseName("IX_customers_classification");
        customer.HasOne<ClientClassification>()
            .WithMany()
            .HasForeignKey(value => new { value.TenantId, value.ClassificationId })
            .HasPrincipalKey(value => new { value.TenantId, value.Id })
            .HasConstraintName("FK_customers_client_classifications_classification_id")
            .OnDelete(DeleteBehavior.Restrict);

        customer.Property(value => value.WithRetention).HasColumnName("with_retention");
        customer.Property(value => value.VatSurplus).HasColumnName("vat_surplus");
        customer.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        customer.Property(value => value.CreatedAt).HasColumnName("created_at");
        customer.Property(value => value.UpdatedAt).HasColumnName("updated_at");

        // La coleccion se lee por el campo de respaldo: el agregado la expone como
        // IReadOnlyCollection y EF no puede escribir en ella.
        customer.Navigation(value => value.Addresses)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

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

        // GIN trigram, no btree: CustomerRepository.SearchAsync filtra con
        // `ILIKE '%termino%'` (comodin a ambos lados), y un btree normal no acelera un
        // "contains" — solo sirve para prefijos. `pg_trgm` es la extension que hace posible este
        // tipo de indice; la migracion que lo crea la habilita con `CREATE EXTENSION IF NOT
        // EXISTS`. Solo en `Name`: `IdentificationNumber` y `Cuc` ya tienen su propio indice
        // unico por tenant, y las busquedas por esos dos campos suelen ser por el valor casi
        // completo, no por un fragmento en cualquier posicion.
        customer.HasIndex(value => value.Name)
            .HasDatabaseName("IX_customers_name_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
    }

    private static void ConfigureCustomerAddress(ModelBuilder modelBuilder)
    {
        var address = modelBuilder.Entity<CustomerAddress>();
        address.ToTable("customer_addresses", "customers");
        address.HasKey(value => value.Id);
        address.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new CustomerAddressId(value))
            .ValueGeneratedNever();
        address.Property(value => value.CustomerId)
            .HasColumnName("customer_id")
            .HasConversion(id => id.Value, value => new CustomerId(value));
        address.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(CustomerAddress.NameMaxLength);
        address.Property(value => value.Address)
            .HasColumnName("address")
            .HasMaxLength(CustomerAddress.AddressMaxLength);
        address.Property(value => value.Phone)
            .HasColumnName("phone")
            .HasMaxLength(CustomerAddress.PhoneMaxLength);
        // La ciudad, FK al modulo Geography. Sin navegacion EF (HasOne<City>()) a proposito:
        // City vive en GeographyDbContext, un modelo distinto, y EF Core no modela relaciones
        // hacia una entidad que no esta en el mismo ModelBuilder. La restriccion real
        // (FK a geography.cities(id), ON DELETE RESTRICT) la agrega a mano la migracion, con
        // migrationBuilder.AddForeignKey — Postgres la impone igual, EF simplemente no la "ve".
        address.Property(value => value.CityId).HasColumnName("city_id");
        address.Property(value => value.IsPrincipal).HasColumnName("is_principal");
        address.Property(value => value.CreatedAt).HasColumnName("created_at");
        address.Property(value => value.UpdatedAt).HasColumnName("updated_at");

        address.HasIndex(value => value.CustomerId)
            .HasDatabaseName("IX_customer_addresses_customer");
        address.HasIndex(value => value.CityId).HasDatabaseName("IX_customer_addresses_city");
        // Buscar la principal de un cliente es la lectura mas frecuente (la cotizacion la
        // propone por defecto). No es unico a proposito: ver el comentario largo en la migracion
        // AddCustomerAddresses — un unico parcial rechaza el cambio de principal, que es una
        // operacion legitima. El invariante vive en Customer.ApplyPrincipal.
        address.HasIndex(value => new { value.CustomerId, value.IsPrincipal })
            .HasDatabaseName("IX_customer_addresses_principal");

        // CASCADE: una direccion no tiene sentido sin su cliente, es parte del mismo agregado.
        address.HasOne<Customer>()
            .WithMany(customer => customer.Addresses)
            .HasForeignKey(value => value.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureClientClassification(ModelBuilder modelBuilder)
    {
        var classification = modelBuilder.Entity<ClientClassification>();
        classification.ToTable("client_classifications", "customers");
        classification.HasKey(value => value.Id);
        classification.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new ClientClassificationId(value))
            .ValueGeneratedNever();
        classification.Property(value => value.TenantId).HasColumnName("tenant_id");
        classification.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(ClientClassification.NameMaxLength);
        classification.Property(value => value.Prefix)
            .HasColumnName("prefix")
            .HasMaxLength(ClientClassification.PrefixMaxLength);
        classification.Property(value => value.IsActive).HasColumnName("is_active");
        classification.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        classification.Property(value => value.CreatedAt).HasColumnName("created_at");
        classification.Property(value => value.UpdatedAt).HasColumnName("updated_at");

        classification.HasIndex(value => value.TenantId)
            .HasDatabaseName("IX_client_classifications_tenant");

        // Clave alterna sobre (tenant_id, id): id solo ya es unico como clave primaria, pero la
        // FK compuesta de Customer.ClassificationId necesita un target unico que incluya
        // tenant_id, para que la base misma impida que un cliente referencie la clasificacion de
        // otro tenant.
        classification.HasAlternateKey(value => new { value.TenantId, value.Id })
            .HasName("AK_client_classifications_tenant_id_id");

        // La unicidad que promete el nombre. Nombrado a proposito: la capa de infraestructura
        // discrimina la violacion de unicidad por nombre de indice y no solo por SqlState, mismo
        // criterio que TaxRate y Customer — la leccion de SDD-CT-06.
        classification.HasIndex(value => new { value.TenantId, value.Name })
            .IsUnique()
            .HasDatabaseName("IX_client_classifications_tenant_name");

        // Su propio indice y su propio codigo de dominio, nunca colapsado con el de arriba: dos
        // indices unicos en el mismo esquema que se traducen con la misma rama mandan a corregir
        // el campo equivocado.
        classification.HasIndex(value => new { value.TenantId, value.Prefix })
            .IsUnique()
            .HasDatabaseName("IX_client_classifications_tenant_prefix");
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
