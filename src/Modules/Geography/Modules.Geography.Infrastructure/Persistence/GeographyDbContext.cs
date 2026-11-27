using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>geography</c>: la DIVIPOLA (departamentos y ciudades) y, desde la spec 2026-10-26 §5, los sinónimos y la
/// bandeja de ciudades sin resolver. Proyecta <c>audit.entries</c> y <c>platform.outbox_messages</c> sin ser su dueño
/// (<c>ExcludeFromMigrations</c>), como Shopify y Quotations. Todo es dato global salvo las ocurrencias, que llevan el
/// tenant del pedido.
/// </summary>
public sealed class GeographyDbContext(DbContextOptions<GeographyDbContext> options)
    : DbContext(options)
{
    public const string Schema = "geography";

    /// <summary>Un solo sinónimo vivo por texto y departamento. <c>GeographyUnitOfWork</c> discrimina por este nombre.</summary>
    public const string SynonymTextActiveIndex = "IX_city_synonyms_department_text_active";

    /// <summary>Una sola fila no resuelta por texto y departamento: el blanco del reporte por SQL (SC-03).</summary>
    public const string UnresolvedTextOpenIndex = "IX_unresolved_cities_department_text_open";

    /// <summary>Una ocurrencia por fila de origen: el blanco del <c>ON CONFLICT (source, source_ref)</c> (SC-03).</summary>
    public const string OccurrenceSourceRefIndex = "IX_unresolved_city_occurrences_source_ref";

    /// <summary>Una ciudad de Siigo por país, estado y código: el blanco del <c>ON CONFLICT</c> del importador.</summary>
    public const string BillingCityCodesIndex = "IX_billing_cities_country_state_city";

    /// <summary>Busca por país y nombre. No es único: la lista de Siigo repite nombres dentro de un país (Boston, Berlín).</summary>
    public const string BillingCityNameIndex = "IX_billing_cities_country_name";

    /// <summary>Un solo sinónimo vivo del exterior por país y texto. <c>GeographyUnitOfWork</c> discrimina por este nombre.</summary>
    public const string ForeignSynonymTextActiveIndex = "IX_foreign_city_synonyms_country_text_active";

    /// <summary>Una sola fila del exterior no resuelta por país y texto: el blanco del reporte por SQL.</summary>
    public const string UnresolvedForeignTextOpenIndex = "IX_unresolved_foreign_cities_country_text_open";

    /// <summary>Una ocurrencia por pedido y fila: el blanco del <c>ON CONFLICT (unresolved_city_id, order_id)</c>.</summary>
    public const string ForeignOccurrenceOrderIndex = "IX_unresolved_foreign_city_occurrences_city_order";

    private const int StatusMaxLength = 16;
    private const int SourceMaxLength = 32;

    // Conversor del tipo no anulable: EF lo aplica a UnresolvedCity.SynonymId sin pasarle nunca un null (como
    // Customer.ClassificationId).
    private static readonly ValueConverter<CitySynonymId, Guid> SynonymIdConverter =
        new(id => id.Value, value => new CitySynonymId(value));

    private static readonly ValueConverter<ForeignCitySynonymId, Guid> ForeignSynonymIdConverter =
        new(id => id.Value, value => new ForeignCitySynonymId(value));

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<Country> Countries => Set<Country>();

    public DbSet<City> Cities => Set<City>();

    public DbSet<Region> Regions => Set<Region>();

    public DbSet<RegionCity> RegionCities => Set<RegionCity>();

    public DbSet<BillingCity> BillingCities => Set<BillingCity>();

    public DbSet<CitySynonym> CitySynonyms => Set<CitySynonym>();

    public DbSet<UnresolvedCity> UnresolvedCities => Set<UnresolvedCity>();

    public DbSet<UnresolvedCityOccurrence> UnresolvedCityOccurrences => Set<UnresolvedCityOccurrence>();

    public DbSet<ForeignCitySynonym> ForeignCitySynonyms => Set<ForeignCitySynonym>();

    public DbSet<UnresolvedForeignCity> UnresolvedForeignCities => Set<UnresolvedForeignCity>();

    public DbSet<UnresolvedForeignCityOccurrence> UnresolvedForeignCityOccurrences => Set<UnresolvedForeignCityOccurrence>();

    internal DbSet<SeedState> SeedStates => Set<SeedState>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    internal DbSet<GeographyOutboxMessage> OutboxMessages => Set<GeographyOutboxMessage>();

    internal static string StatusCheck() =>
        "status IN (" + string.Join(",", Enum.GetNames<UnresolvedCityStatus>().Select(name => $"'{name}'")) + ")";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureCountry(modelBuilder);
        ConfigureDepartment(modelBuilder);
        ConfigureCity(modelBuilder);
        ConfigureRegion(modelBuilder);
        ConfigureRegionCity(modelBuilder);
        ConfigureSeedState(modelBuilder);
        ConfigureBillingCity(modelBuilder);
        ConfigureCitySynonym(modelBuilder);
        ConfigureUnresolvedCity(modelBuilder);
        ConfigureUnresolvedCityOccurrence(modelBuilder);
        ConfigureForeignCitySynonym(modelBuilder);
        ConfigureUnresolvedForeignCity(modelBuilder);
        ConfigureUnresolvedForeignCityOccurrence(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
    }

    private static void ConfigureCountry(ModelBuilder modelBuilder)
    {
        var country = modelBuilder.Entity<Country>();
        country.ToTable("countries", "geography");
        country.HasKey(value => value.Code);
        country.Property(value => value.Code)
            .HasColumnName("code")
            .HasMaxLength(2)
            .ValueGeneratedNever();
        country.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(80)
            .IsRequired();
        country.HasIndex(value => value.Name).IsUnique();
    }

    private static void ConfigureDepartment(ModelBuilder modelBuilder)
    {
        var department = modelBuilder.Entity<Department>();
        department.ToTable("departments", "geography");
        department.HasKey(value => value.Id);
        department.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new DepartmentId(value))
            .ValueGeneratedNever();
        department.Property(value => value.DivipolaCode)
            .HasColumnName("divipola_code")
            .HasMaxLength(2)
            .IsRequired();
        department.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(120)
            .IsRequired();
        department.HasIndex(value => value.DivipolaCode).IsUnique();
    }

    private static void ConfigureCity(ModelBuilder modelBuilder)
    {
        var city = modelBuilder.Entity<City>();
        city.ToTable("cities", "geography");
        city.HasKey(value => value.Id);
        city.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new CityId(value))
            .ValueGeneratedNever();
        city.Property(value => value.DivipolaCode)
            .HasColumnName("divipola_code")
            .HasMaxLength(5)
            .IsRequired();
        city.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(120)
            .IsRequired();
        city.Property(value => value.DivipolaName)
            .HasColumnName("divipola_name")
            .HasMaxLength(120)
            .IsRequired();
        city.Property(value => value.CoordinadoraName)
            .HasColumnName("coordinadora_name")
            .HasMaxLength(120);
        city.Property(value => value.DepartmentId)
            .HasColumnName("department_id")
            .HasConversion(id => id.Value, value => new DepartmentId(value));
        city.HasIndex(value => value.DivipolaCode).IsUnique();
        city.HasIndex(value => value.DepartmentId);
        city.HasOne<Department>()
            .WithMany()
            .HasForeignKey(value => value.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    // Países fuera de Colombia (spec 2026-11-20): Colombia vive en departments/cities con DIVIPOLA y no se copia acá.
    private static void ConfigureRegion(ModelBuilder modelBuilder)
    {
        var region = modelBuilder.Entity<Region>();
        region.ToTable("regions", "geography");
        region.HasKey(value => value.Id);
        region.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new RegionId(value))
            .ValueGeneratedNever();
        region.Property(value => value.CountryCode)
            .HasColumnName("country_code")
            .HasMaxLength(2)
            .IsRequired();
        region.Property(value => value.Code)
            .HasColumnName("code")
            .HasMaxLength(10)
            .IsRequired();
        region.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(120)
            .IsRequired();
        region.HasIndex(value => new { value.CountryCode, value.Code }).IsUnique();
    }

    private static void ConfigureRegionCity(ModelBuilder modelBuilder)
    {
        var city = modelBuilder.Entity<RegionCity>();
        city.ToTable("region_cities", "geography");
        city.HasKey(value => value.Id);
        city.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new RegionCityId(value))
            .ValueGeneratedNever();
        city.Property(value => value.RegionId)
            .HasColumnName("region_id")
            .HasConversion(id => id.Value, value => new RegionId(value));
        city.Property(value => value.Code)
            .HasColumnName("code")
            .HasMaxLength(10)
            .IsRequired();
        city.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(120)
            .IsRequired();
        city.HasIndex(value => new { value.RegionId, value.Code }).IsUnique();
        city.HasOne<Region>()
            .WithMany()
            .HasForeignKey(value => value.RegionId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    // La huella del último archivo aplicado por cada importador de datos de referencia (RegionSeeder): con ella el
    // arranque se salta un país que ya está al día sin leer sus tablas.
    private static void ConfigureSeedState(ModelBuilder modelBuilder)
    {
        var state = modelBuilder.Entity<SeedState>();
        state.ToTable("seed_state", "geography");
        state.HasKey(value => value.Key);
        state.Property(value => value.Key)
            .HasColumnName("key")
            .HasMaxLength(64)
            .ValueGeneratedNever();
        state.Property(value => value.ContentHash)
            .HasColumnName("content_hash")
            .HasMaxLength(64)
            .IsRequired();
    }

    // La lista de ciudades de Siigo fuera de Colombia (spec 2026-11-26): los códigos con los que se factura a otro país.
    private static void ConfigureBillingCity(ModelBuilder modelBuilder)
    {
        var city = modelBuilder.Entity<BillingCity>();
        city.ToTable("billing_cities", Schema);
        city.HasKey(value => value.Id);
        city.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new BillingCityId(value))
            .ValueGeneratedNever();
        city.Property(value => value.CountryCode)
            .HasColumnName("country_code")
            .HasMaxLength(2)
            .IsRequired();
        city.Property(value => value.SiigoCountryCode)
            .HasColumnName("siigo_country_code")
            .HasMaxLength(2)
            .IsRequired();
        city.Property(value => value.CountryName)
            .HasColumnName("country_name")
            .HasMaxLength(BillingCity.CountryNameMaxLength)
            .IsRequired();
        city.Property(value => value.StateCode)
            .HasColumnName("state_code")
            .HasMaxLength(BillingCity.CodeMaxLength)
            .IsRequired();
        city.Property(value => value.StateName)
            .HasColumnName("state_name")
            .HasMaxLength(BillingCity.NameMaxLength)
            .IsRequired();
        city.Property(value => value.CityCode)
            .HasColumnName("city_code")
            .HasMaxLength(BillingCity.CodeMaxLength)
            .IsRequired();
        city.Property(value => value.CityName)
            .HasColumnName("city_name")
            .HasMaxLength(BillingCity.NameMaxLength)
            .IsRequired();
        city.Property(value => value.NormalizedName)
            .HasColumnName("normalized_name")
            .HasMaxLength(CityText.MaxLength)
            .IsRequired();
        city.HasIndex(value => new { value.CountryCode, value.StateCode, value.CityCode })
            .IsUnique()
            .HasDatabaseName(BillingCityCodesIndex);
        city.HasIndex(value => new { value.CountryCode, value.NormalizedName })
            .HasDatabaseName(BillingCityNameIndex);
    }

    private static void ConfigureCitySynonym(ModelBuilder modelBuilder)
    {
        var synonym = modelBuilder.Entity<CitySynonym>();
        synonym.ToTable("city_synonyms", Schema);
        synonym.HasKey(value => value.Id);
        synonym.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new CitySynonymId(value))
            .ValueGeneratedNever();
        synonym.Property(value => value.DepartmentId)
            .HasColumnName("department_id")
            .HasConversion(id => id.Value, value => new DepartmentId(value));
        synonym.Property(value => value.NormalizedText)
            .HasColumnName("normalized_text")
            .HasMaxLength(CityText.MaxLength);
        synonym.Property(value => value.DisplayText)
            .HasColumnName("display_text")
            .HasMaxLength(CityText.MaxLength);
        synonym.Property(value => value.CityId)
            .HasColumnName("city_id")
            .HasConversion(id => id.Value, value => new CityId(value));
        synonym.Property(value => value.CreatedBy).HasColumnName("created_by");
        synonym.Property(value => value.CreatedAt).HasColumnName("created_at");
        synonym.Property(value => value.UpdatedBy).HasColumnName("updated_by");
        synonym.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        synonym.Property(value => value.DeletedAt).HasColumnName("deleted_at");
        synonym.Property(value => value.DeletedBy).HasColumnName("deleted_by");
        synonym.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        synonym.Ignore(value => value.IsDeleted);

        // Parcial: borrar un sinónimo libera su texto para uno nuevo, y el borrado conserva su historia.
        synonym.HasIndex(value => new { value.DepartmentId, value.NormalizedText })
            .IsUnique()
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName(SynonymTextActiveIndex);
        synonym.HasIndex(value => value.CityId).HasDatabaseName("IX_city_synonyms_city");
        synonym.HasOne<Department>()
            .WithMany()
            .HasForeignKey(value => value.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);
        synonym.HasOne<City>()
            .WithMany()
            .HasForeignKey(value => value.CityId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureUnresolvedCity(ModelBuilder modelBuilder)
    {
        var unresolved = modelBuilder.Entity<UnresolvedCity>();
        unresolved.ToTable("unresolved_cities", Schema, table =>
            table.HasCheckConstraint("CK_unresolved_cities_status", StatusCheck()));
        unresolved.HasKey(value => value.Id);
        unresolved.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new UnresolvedCityId(value))
            .ValueGeneratedNever();
        unresolved.Property(value => value.DepartmentId)
            .HasColumnName("department_id")
            .HasConversion(id => id.Value, value => new DepartmentId(value));
        unresolved.Property(value => value.NormalizedText)
            .HasColumnName("normalized_text")
            .HasMaxLength(CityText.MaxLength);
        unresolved.Property(value => value.CityText)
            .HasColumnName("city_text")
            .HasMaxLength(CityText.MaxLength);
        unresolved.Property(value => value.SampleAddress)
            .HasColumnName("sample_address")
            .HasMaxLength(UnresolvedCity.SampleAddressMaxLength);
        unresolved.Property(value => value.ProvinceCode)
            .HasColumnName("province_code")
            .HasMaxLength(UnresolvedCity.ProvinceCodeMaxLength);
        // Texto y no el entero por defecto, como el estado de una cotización: el CHECK y el filtro del índice lo nombran.
        unresolved.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(StatusMaxLength);
        unresolved.Property(value => value.SynonymId)
            .HasColumnName("synonym_id")
            .HasConversion(SynonymIdConverter);
        unresolved.Property(value => value.FirstSeenAt).HasColumnName("first_seen_at");
        unresolved.Property(value => value.LastSeenAt).HasColumnName("last_seen_at");
        unresolved.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        unresolved.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        // Parcial: un texto resuelto que vuelve a aparecer (el sinónimo se borró) abre una fila nueva; uno descartado no,
        // suma ocurrencias a la suya (D-C9).
        unresolved.HasIndex(value => new { value.DepartmentId, value.NormalizedText })
            .IsUnique()
            .HasFilter("status <> 'Resolved'")
            .HasDatabaseName(UnresolvedTextOpenIndex);
        // La bandeja lista por estado, lo más reciente primero.
        unresolved.HasIndex(value => new { value.Status, value.LastSeenAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_unresolved_cities_status_last_seen");
        unresolved.HasOne<Department>()
            .WithMany()
            .HasForeignKey(value => value.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);
        unresolved.HasOne<CitySynonym>()
            .WithMany()
            .HasForeignKey(value => value.SynonymId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureUnresolvedCityOccurrence(ModelBuilder modelBuilder)
    {
        var occurrence = modelBuilder.Entity<UnresolvedCityOccurrence>();
        occurrence.ToTable("unresolved_city_occurrences", Schema);
        occurrence.HasKey(value => value.Id);
        occurrence.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        occurrence.Property(value => value.UnresolvedCityId)
            .HasColumnName("unresolved_city_id")
            .HasConversion(id => id.Value, value => new UnresolvedCityId(value));
        occurrence.Property(value => value.TenantId).HasColumnName("tenant_id");
        occurrence.Property(value => value.Source)
            .HasColumnName("source")
            .HasMaxLength(SourceMaxLength);
        // El id de shopify.orders: referencia blanda, otro esquema.
        occurrence.Property(value => value.SourceRef).HasColumnName("source_ref");
        occurrence.Property(value => value.OrderId).HasColumnName("order_id");
        occurrence.Property(value => value.CustomerId).HasColumnName("customer_id");
        occurrence.Property(value => value.PartyCityOnly).HasColumnName("party_city_only");
        occurrence.Property(value => value.CustomerCityOnly).HasColumnName("customer_city_only");
        occurrence.Property(value => value.SeenAt).HasColumnName("seen_at");

        occurrence.HasIndex(value => new { value.Source, value.SourceRef })
            .IsUnique()
            .HasDatabaseName(OccurrenceSourceRefIndex);
        occurrence.HasIndex(value => value.UnresolvedCityId)
            .HasDatabaseName("IX_unresolved_city_occurrences_unresolved_city");
        occurrence.HasOne<UnresolvedCity>()
            .WithMany()
            .HasForeignKey(value => value.UnresolvedCityId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    // Sinónimos y bandeja del exterior (spec 2026-11-26): mismo patrón que los colombianos, por país y contra billing_cities.
    private static void ConfigureForeignCitySynonym(ModelBuilder modelBuilder)
    {
        var synonym = modelBuilder.Entity<ForeignCitySynonym>();
        synonym.ToTable("foreign_city_synonyms", Schema);
        synonym.HasKey(value => value.Id);
        synonym.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(ForeignSynonymIdConverter)
            .ValueGeneratedNever();
        synonym.Property(value => value.CountryCode)
            .HasColumnName("country_code")
            .HasMaxLength(2);
        synonym.Property(value => value.NormalizedText)
            .HasColumnName("normalized_text")
            .HasMaxLength(CityText.MaxLength);
        synonym.Property(value => value.DisplayText)
            .HasColumnName("display_text")
            .HasMaxLength(CityText.MaxLength);
        synonym.Property(value => value.BillingCityId)
            .HasColumnName("billing_city_id")
            .HasConversion(id => id.Value, value => new BillingCityId(value));
        synonym.Property(value => value.CreatedBy).HasColumnName("created_by");
        synonym.Property(value => value.CreatedAt).HasColumnName("created_at");
        synonym.Property(value => value.UpdatedBy).HasColumnName("updated_by");
        synonym.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        synonym.Property(value => value.DeletedAt).HasColumnName("deleted_at");
        synonym.Property(value => value.DeletedBy).HasColumnName("deleted_by");
        synonym.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        synonym.Ignore(value => value.IsDeleted);

        // Parcial: borrar un sinónimo libera su texto para uno nuevo, y el borrado conserva su historia.
        synonym.HasIndex(value => new { value.CountryCode, value.NormalizedText })
            .IsUnique()
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName(ForeignSynonymTextActiveIndex);
        synonym.HasIndex(value => value.BillingCityId).HasDatabaseName("IX_foreign_city_synonyms_billing_city");
        synonym.HasOne<BillingCity>()
            .WithMany()
            .HasForeignKey(value => value.BillingCityId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureUnresolvedForeignCity(ModelBuilder modelBuilder)
    {
        var unresolved = modelBuilder.Entity<UnresolvedForeignCity>();
        unresolved.ToTable("unresolved_foreign_cities", Schema, table =>
            table.HasCheckConstraint("CK_unresolved_foreign_cities_status", StatusCheck()));
        unresolved.HasKey(value => value.Id);
        unresolved.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new UnresolvedForeignCityId(value))
            .ValueGeneratedNever();
        unresolved.Property(value => value.CountryCode)
            .HasColumnName("country_code")
            .HasMaxLength(2);
        unresolved.Property(value => value.NormalizedText)
            .HasColumnName("normalized_text")
            .HasMaxLength(CityText.MaxLength);
        unresolved.Property(value => value.CityText)
            .HasColumnName("city_text")
            .HasMaxLength(CityText.MaxLength);
        // Texto y no el entero por defecto: el CHECK y el filtro del índice lo nombran.
        unresolved.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(StatusMaxLength);
        unresolved.Property(value => value.SynonymId)
            .HasColumnName("synonym_id")
            .HasConversion(ForeignSynonymIdConverter);
        unresolved.Property(value => value.FirstSeenAt).HasColumnName("first_seen_at");
        unresolved.Property(value => value.LastSeenAt).HasColumnName("last_seen_at");
        unresolved.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        unresolved.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        // Parcial: un texto resuelto que vuelve a aparecer (el sinónimo se borró) abre una fila nueva; uno descartado no,
        // suma ocurrencias a la suya.
        unresolved.HasIndex(value => new { value.CountryCode, value.NormalizedText })
            .IsUnique()
            .HasFilter("status <> 'Resolved'")
            .HasDatabaseName(UnresolvedForeignTextOpenIndex);
        unresolved.HasIndex(value => new { value.Status, value.LastSeenAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_unresolved_foreign_cities_status_last_seen");
        unresolved.HasOne<ForeignCitySynonym>()
            .WithMany()
            .HasForeignKey(value => value.SynonymId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureUnresolvedForeignCityOccurrence(ModelBuilder modelBuilder)
    {
        var occurrence = modelBuilder.Entity<UnresolvedForeignCityOccurrence>();
        occurrence.ToTable("unresolved_foreign_city_occurrences", Schema);
        occurrence.HasKey(value => value.Id);
        occurrence.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        occurrence.Property(value => value.UnresolvedCityId)
            .HasColumnName("unresolved_city_id")
            .HasConversion(id => id.Value, value => new UnresolvedForeignCityId(value));
        occurrence.Property(value => value.TenantId).HasColumnName("tenant_id");
        occurrence.Property(value => value.OrderId).HasColumnName("order_id");
        occurrence.Property(value => value.SeenAt).HasColumnName("seen_at");

        // Una ocurrencia por pedido y fila: la misma orden vista muchas veces no suma.
        occurrence.HasIndex(value => new { value.UnresolvedCityId, value.OrderId })
            .IsUnique()
            .HasDatabaseName(ForeignOccurrenceOrderIndex);
        occurrence.HasOne<UnresolvedForeignCity>()
            .WithMany()
            .HasForeignKey(value => value.UnresolvedCityId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    // Copia de la proyección de Quotations: platform.outbox_messages la crea BuildingBlocks; acá sólo se escribe.
    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<GeographyOutboxMessage>();
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
