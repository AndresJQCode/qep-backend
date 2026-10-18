using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

public sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options)
    : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    internal DbSet<CustomerAddress> CustomerAddresses => Set<CustomerAddress>();

    public DbSet<ClientClassification> ClientClassifications => Set<ClientClassification>();

    internal DbSet<CustomerNote> CustomerNotes => Set<CustomerNote>();

    internal DbSet<Tag> Tags => Set<Tag>();

    internal DbSet<CustomerTagAssignment> CustomerTagAssignments => Set<CustomerTagAssignment>();

    internal DbSet<CustomerCucCounter> CucCounters => Set<CustomerCucCounter>();

    internal DbSet<CustomersOutboxMessage> Outbox => Set<CustomersOutboxMessage>();

    /// <summary>Spec 2026-10-10 §7.2 (D-A11): exactamente las columnas que antes eran NOT NULL. Teléfono, correo y
    /// ciudad siguen exigidos por CustomerWriteRules, como antes.</summary>
    public const string CompleteFieldsCheck =
        "completeness = 'Incomplete' OR (cuc IS NOT NULL AND identification_type IS NOT NULL AND identification_number IS NOT NULL "
        + "AND address IS NOT NULL AND country IS NOT NULL AND classification_id IS NOT NULL)";

    /// <summary>El único por <c>(tenant_id, lower(name))</c>. Va en la migración con SQL —EF no modela índices por
    /// expresión— y <c>CustomersUnitOfWork</c> lo traduce por este nombre a 409 <c>customers.tag.name_taken</c>.</summary>
    public const string TagNameIndex = "IX_tags_tenant_name";

    /// <summary>El de <c>(tenant_id, lower(email))</c> que usa <c>CustomerRepository.FindIdByEmailAsync</c> (spec de Shopify
    /// 2026-10-15 §9). Va en la migración <c>AddCustomerEmailIndex</c> con SQL —EF no modela índices por expresión— y
    /// no es único: dos clientes pueden compartir correo, así que no hay violación que traducir.</summary>
    public const string EmailIndex = "IX_customers_tenant_email";

    /// <summary>La clave de <c>customer_tags</c>: dos <c>PUT</c> simultáneos que agregan la misma etiqueta chocan acá.</summary>
    public const string CustomerTagPrimaryKey = "PK_customer_tags";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureCustomer(modelBuilder);
        ConfigureCustomerAddress(modelBuilder);
        ConfigureClientClassification(modelBuilder);
        ConfigureCucCounter(modelBuilder);
        ConfigureCustomerNote(modelBuilder);
        ConfigureTag(modelBuilder);
        ConfigureCustomerTagAssignment(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
    }

    private static void ConfigureCustomer(ModelBuilder modelBuilder)
    {
        var customer = modelBuilder.Entity<Customer>();
        customer.ToTable("customers", "customers", table =>
        {
            table.HasCheckConstraint("CK_customers_completeness", "completeness IN ('Complete', 'Incomplete')");
            table.HasCheckConstraint("CK_customers_complete_fields", CompleteFieldsCheck);
            table.HasCheckConstraint(
                "CK_customers_marketing_opt_out_source",
                "marketing_opt_out_source IS NULL OR marketing_opt_out_source IN ('manual', 'meta', 'button')");
        });
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
        // Spec 2026-10-09 §6.5: E.164 con «+» (máximo 15 dígitos). Índice parcial por tenant para el
        // emparejamiento de Messaging (phone_e164 = ANY(@phones)).
        customer.Property(value => value.PhoneE164)
            .HasColumnName("phone_e164")
            .HasMaxLength(16);
        customer.HasIndex(value => new { value.TenantId, value.PhoneE164 })
            .HasDatabaseName("IX_customers_tenant_phone_e164")
            .HasFilter("phone_e164 IS NOT NULL");
        customer.Property(value => value.Email)
            .HasColumnName("email")
            .HasMaxLength(CustomerContactInfo.EmailMaxLength);
        // El domicilio del cliente (spec 2026-09-18): vuelve a customers despues de CLI-DIR-01,
        // separado de la libreta. Misma FK blanda que customer_addresses.city_id: sin navegacion EF
        // hacia City, que vive en GeographyDbContext; la FK real (FK_customers_cities_city_id, ON
        // DELETE RESTRICT) la agrega a mano la migracion AddCustomerContactAddress, con el mismo
        // motivo que se explica en ConfigureCustomerAddress.
        customer.Property(value => value.Address)
            .HasColumnName("address")
            .HasMaxLength(CustomerContactInfo.AddressMaxLength);
        // El pais, ISO-3166-1 alpha-2, discriminante de cual de los dos carriles de ciudad de abajo
        // viene lleno. Obligatorio en una ficha completa (CK_customers_complete_fields); un
        // incompleto puede no tenerlo (spec 2026-10-10 §6.2).
        customer.Property(value => value.Country)
            .HasColumnName("country")
            .HasMaxLength(CustomerContactInfo.CountryLength);
        // city_id es nulo para un cliente que no es de Colombia: DIVIPOLA no describe ninguna
        // ciudad de afuera. La FK real sigue valiendo — Postgres admite NULL bajo una FK.
        customer.Property(value => value.CityId).HasColumnName("city_id");
        customer.HasIndex(value => value.CityId).HasDatabaseName("IX_customers_city");
        // Y city_name es el carril contrario: la ciudad escrita a mano de ese cliente de afuera.
        // Nulo para uno colombiano. Los dos nunca vienen juntos (Customer.EnsureValidLocation).
        customer.Property(value => value.CityName)
            .HasColumnName("city_name")
            .HasMaxLength(CustomerContactInfo.CityNameMaxLength);
        // La clasificacion, FK compuesta (tenant_id, classification_id) a
        // customers.client_classifications(tenant_id, id) — compuesta y no simple sobre id, para
        // que un cliente no pueda referenciar la clasificacion de otro tenant. A diferencia de la
        // ciudad, ClientClassification SI vive en este mismo DbContext, asi que la relacion se
        // modela con HasOne/HasForeignKey normal y EF genera la migracion completa.
        customer.Property(value => value.ClassificationId)
            .HasColumnName("classification_id")
            // Conversor del tipo no anulable: EF lo aplica a la propiedad anulable sin pasarle
            // nunca un null (un incompleto no tiene clasificación, spec 2026-10-10 §6.2).
            .HasConversion(new ValueConverter<ClientClassificationId, Guid>(
                id => id.Value, value => new ClientClassificationId(value)));
        customer.HasIndex(value => value.ClassificationId)
            .HasDatabaseName("IX_customers_classification");
        customer.HasOne<ClientClassification>()
            .WithMany()
            .HasForeignKey(value => new { value.TenantId, value.ClassificationId })
            .HasPrincipalKey(value => new { value.TenantId, value.Id })
            .HasConstraintName("FK_customers_client_classifications_classification_id")
            .OnDelete(DeleteBehavior.Restrict);

        // Spec 2026-10-11 §4.1. El DEFAULT existe para las filas anteriores a la migración; el código siempre escribe
        // la columna.
        customer.Property(value => value.MarketingOptOut).HasColumnName("marketing_opt_out").HasDefaultValue(false);
        customer.Property(value => value.MarketingOptOutAt).HasColumnName("marketing_opt_out_at");
        customer.Property(value => value.MarketingOptOutSource)
            .HasColumnName("marketing_opt_out_source")
            .HasMaxLength(16)
            .HasConversion(new ValueConverter<MarketingConsentSource, string>(
                source => source.ToWireValue(), wire => MarketingConsentSourceParser.Parse(wire)));

        customer.Property(value => value.WithRetention).HasColumnName("with_retention");
        customer.Property(value => value.VatSurplus).HasColumnName("vat_surplus");
        customer.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        customer.Property(value => value.CreatedAt).HasColumnName("created_at");
        customer.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        // El DEFAULT existe sólo para el binario viejo durante el despliegue (spec 2026-10-10 D-A11). El centinela
        // inválido hace que EF nunca omita la columna en el INSERT: el código nuevo siempre la escribe, también
        // cuando vale Complete (el valor por defecto del enum).
        customer.Property(value => value.Completeness).HasColumnName("completeness").HasConversion<string>().HasMaxLength(16)
            .HasDefaultValue(CustomerCompleteness.Complete)
            .HasSentinel((CustomerCompleteness)(-1));
        customer.Ignore(value => value.IsComplete);
        customer.Property(value => value.WhatsAppUserId).HasColumnName("whatsapp_user_id").HasMaxLength(Customer.WhatsAppUserIdMaxLength);
        // Spec 2026-10-10 §7.2: la identidad de WhatsApp y el árbitro de la carrera de §9.4. CustomersUnitOfWork la
        // traduce por este nombre (T2).
        customer.HasIndex(value => new { value.TenantId, value.WhatsAppUserId }, "IX_customers_tenant_whatsapp_user_id")
            .IsUnique().HasFilter("whatsapp_user_id IS NOT NULL");
        customer.HasIndex(value => value.TenantId, "IX_customers_tenant_incomplete").HasFilter("completeness = 'Incomplete'");
        // Spec 2026-10-14 decisión 10: la identidad de Instagram, con el mismo esquema que la de WhatsApp. CustomersUnitOfWork la
        // traduce por este nombre.
        customer.Property(value => value.InstagramUserId).HasColumnName("instagram_user_id").HasMaxLength(Customer.InstagramUserIdMaxLength);
        customer.Property(value => value.InstagramUsername).HasColumnName("instagram_username").HasMaxLength(Customer.InstagramUsernameMaxLength);
        customer.HasIndex(value => new { value.TenantId, value.InstagramUserId }, "IX_customers_tenant_instagram_user_id")
            .IsUnique().HasFilter("instagram_user_id IS NOT NULL");

        // La coleccion se lee por el campo de respaldo: el agregado la expone como
        // IReadOnlyCollection y EF no puede escribir en ella.
        customer.Navigation(value => value.Addresses)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // Con nombre: sin él, EF funde este índice con IX_customers_tenant_incomplete (misma columna) y
        // el segundo pisa el filtro del primero.
        customer.HasIndex(value => value.TenantId, "IX_customers_tenant");

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

    // Spec 2026-10-11 §4.1. La nota es su propio agregado (no una colección de Customer): una nota no sube la versión
    // del cliente. La FK al cliente es CASCADE por completitud —los clientes no se borran—.
    private static void ConfigureCustomerNote(ModelBuilder modelBuilder)
    {
        var note = modelBuilder.Entity<CustomerNote>();
        note.ToTable("customer_notes", "customers");
        note.HasKey(value => value.Id);
        note.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new CustomerNoteId(value))
            .ValueGeneratedNever();
        note.Property(value => value.TenantId).HasColumnName("tenant_id");
        note.Property(value => value.CustomerId)
            .HasColumnName("customer_id")
            .HasConversion(id => id.Value, value => new CustomerId(value));
        note.Property(value => value.Body).HasColumnName("body").HasMaxLength(CustomerNote.BodyMaxLength);
        note.Property(value => value.AuthorUserId).HasColumnName("author_user_id");
        note.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        note.Property(value => value.CreatedAt).HasColumnName("created_at");
        note.Property(value => value.UpdatedAt).HasColumnName("updated_at");

        // La lista de la ficha: las notas de un cliente, de la más nueva a la más vieja.
        note.HasIndex(value => new { value.TenantId, value.CustomerId, value.CreatedAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("IX_customer_notes_tenant_customer_created");
        note.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(value => value.CustomerId)
            .HasConstraintName("FK_customer_notes_customers_customer_id")
            .OnDelete(DeleteBehavior.Cascade);
    }

    // Spec 2026-10-11 §4.1. La unicidad del nombre (lower(name)) NO está en el modelo: EF no modela índices por
    // expresión. La crea la migración a mano, con el nombre de TagNameIndex.
    private static void ConfigureTag(ModelBuilder modelBuilder)
    {
        var tag = modelBuilder.Entity<Tag>();
        tag.ToTable("tags", "customers", table => table.HasCheckConstraint(
            "CK_tags_color", "color IN ('gray', 'red', 'orange', 'yellow', 'green', 'teal', 'blue', 'purple')"));
        tag.HasKey(value => value.Id);
        tag.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new TagId(value))
            .ValueGeneratedNever();
        tag.Property(value => value.TenantId).HasColumnName("tenant_id");
        tag.Property(value => value.Name).HasColumnName("name").HasMaxLength(Tag.NameMaxLength);
        tag.Property(value => value.Color)
            .HasColumnName("color")
            .HasMaxLength(16)
            .HasConversion(new ValueConverter<TagColor, string>(color => color.ToWireValue(), wire => TagColorParser.Parse(wire)));
        tag.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        tag.Property(value => value.CreatedAt).HasColumnName("created_at");
        tag.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        tag.HasIndex(value => value.TenantId).HasDatabaseName("IX_tags_tenant");
    }

    // Spec 2026-10-11 §4.1: la clave (customer_id, tag_id) impide la repetida; borrar la etiqueta borra sus
    // asignaciones (CASCADE), y el índice (tenant_id, tag_id) sirve para contar y para armar audiencias.
    private static void ConfigureCustomerTagAssignment(ModelBuilder modelBuilder)
    {
        var assignment = modelBuilder.Entity<CustomerTagAssignment>();
        assignment.ToTable("customer_tags", "customers");
        assignment.HasKey(value => new { value.CustomerId, value.TagId }).HasName(CustomerTagPrimaryKey);
        assignment.Property(value => value.CustomerId)
            .HasColumnName("customer_id")
            .HasConversion(id => id.Value, value => new CustomerId(value));
        assignment.Property(value => value.TagId)
            .HasColumnName("tag_id")
            .HasConversion(id => id.Value, value => new TagId(value));
        assignment.Property(value => value.TenantId).HasColumnName("tenant_id");
        assignment.Property(value => value.AssignedAt).HasColumnName("assigned_at");
        assignment.Property(value => value.AssignedByUserId).HasColumnName("assigned_by_user_id");
        assignment.HasIndex(value => new { value.TenantId, value.TagId }).HasDatabaseName("IX_customer_tags_tenant_tag");
        assignment.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(value => value.CustomerId)
            .HasConstraintName("FK_customer_tags_customers_customer_id")
            .OnDelete(DeleteBehavior.Cascade);
        assignment.HasOne<Tag>()
            .WithMany()
            .HasForeignKey(value => value.TagId)
            .HasConstraintName("FK_customer_tags_tags_tag_id")
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
