using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Infrastructure.Persistence;

public sealed class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options)
    : DbContext(options)
{
    public const string Schema = "scheduling";

    /// <summary>La restricción de exclusión de las sesiones (spec 2026-11-21, D-8). La crea la migración a mano: EF no modela <c>EXCLUDE</c>.
    /// Se traduce por nombre en <see cref="SchedulingUnitOfWork"/>.</summary>
    public const string SessionOccupancyConstraint = "EX_sessions_resource_occupancy";

    public const string SessionUniqueIndex = "IX_sessions_resource_service_start";

    /// <summary>Un miembro atiende un solo recurso por sede. Se traduce por nombre en <see cref="SchedulingUnitOfWork"/>.</summary>
    public const string ResourceMemberUniqueIndex = "IX_resources_location_member";

    /// <summary>The idempotency key of a booking, unique per tenant. A repeat is answered by the use case before it gets here; the index
    /// only closes the race of two first requests with the same key.</summary>
    public const string AppointmentClientRequestIndex = "IX_appointments_client_request";

    private static readonly ValueConverter<LocationId, Guid> LocationIdConverter = new(id => id.Value, value => new LocationId(value));
    private static readonly ValueConverter<ResourceId, Guid> ResourceIdConverter = new(id => id.Value, value => new ResourceId(value));
    private static readonly ValueConverter<ServiceId, Guid> ServiceIdConverter = new(id => id.Value, value => new ServiceId(value));
    private static readonly ValueConverter<AvailabilityBlockId, Guid> BlockIdConverter = new(id => id.Value, value => new AvailabilityBlockId(value));
    private static readonly ValueConverter<ClosureId, Guid> ClosureIdConverter = new(id => id.Value, value => new ClosureId(value));
    private static readonly ValueConverter<SessionId, Guid> SessionIdConverter = new(id => id.Value, value => new SessionId(value));
    private static readonly ValueConverter<AppointmentId, Guid> AppointmentIdConverter = new(id => id.Value, value => new AppointmentId(value));
    private static readonly ValueConverter<AppointmentEventId, Guid> EventIdConverter = new(id => id.Value, value => new AppointmentEventId(value));

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<Resource> Resources => Set<Resource>();

    public DbSet<Service> Services => Set<Service>();

    public DbSet<AvailabilityBlock> Blocks => Set<AvailabilityBlock>();

    public DbSet<Closure> Closures => Set<Closure>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<Appointment> Appointments => Set<Appointment>();

    public DbSet<SchedulingSettings> Settings => Set<SchedulingSettings>();

    // Proyección de escritura de audit.entries (ExcludeFromMigrations): el módulo Audit es dueño de la tabla.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureLocation(modelBuilder);
        ConfigureResource(modelBuilder);
        ConfigureService(modelBuilder);
        ConfigureBlock(modelBuilder);
        ConfigureClosure(modelBuilder);
        ConfigureSession(modelBuilder);
        ConfigureAppointment(modelBuilder);
        ConfigureSettings(modelBuilder);
    }

    // `column IN ('A','B')` para un enum guardado como texto: la base no depende de quien escribe.
    private static string In<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(',', Enum.GetNames<TEnum>().Select(name => $"'{name}'"))})";

    // La versión es el token de concurrencia de toda fila editable: una escritura con la versión vieja sale como 412.
    private static void ConfigureVersioned<T>(EntityTypeBuilder<T> entity)
        where T : VersionedEntity
    {
        entity.Property(value => value.TenantId).HasColumnName("tenant_id");
        entity.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        entity.Property(value => value.CreatedAt).HasColumnName("created_at");
        entity.Property(value => value.UpdatedAt).HasColumnName("updated_at");
    }

    private static void ConfigureArchivable<T>(EntityTypeBuilder<T> entity)
        where T : ArchivableEntity
    {
        ConfigureVersioned(entity);
        entity.Property(value => value.ArchivedAt).HasColumnName("archived_at");
        entity.Ignore(value => value.Archived);
    }

    private static void ConfigureLocation(ModelBuilder modelBuilder)
    {
        var location = modelBuilder.Entity<Location>();
        location.ToTable("locations", Schema);
        location.HasKey(value => value.Id);
        location.Property(value => value.Id).HasColumnName("id").HasConversion(LocationIdConverter).ValueGeneratedNever();
        ConfigureArchivable(location);
        location.Property(value => value.Name).HasColumnName("name").HasMaxLength(120);
        location.Property(value => value.IsMain).HasColumnName("is_main");
        // Una sola sede principal por tenant: es el blanco del ON CONFLICT del aprovisionador y la garantía de que ningún otro camino la duplica.
        location.HasIndex(value => value.TenantId).IsUnique().HasFilter("is_main").HasDatabaseName("IX_locations_tenant_main");
    }

    private static void ConfigureResource(ModelBuilder modelBuilder)
    {
        var resource = modelBuilder.Entity<Resource>();
        resource.ToTable("resources", Schema);
        resource.HasKey(value => value.Id);
        resource.Property(value => value.Id).HasColumnName("id").HasConversion(ResourceIdConverter).ValueGeneratedNever();
        ConfigureArchivable(resource);
        resource.Property(value => value.LocationId).HasColumnName("location_id").HasConversion(LocationIdConverter);
        resource.Property(value => value.DisplayName).HasColumnName("display_name").HasMaxLength(120);
        // identity.users / tenancy.memberships: otro módulo, sin FK.
        resource.Property(value => value.MemberId).HasColumnName("member_id");
        resource.HasOne<Location>().WithMany().HasForeignKey(value => value.LocationId).OnDelete(DeleteBehavior.Restrict);
        resource.HasIndex(value => new { value.TenantId, value.LocationId }).HasDatabaseName("IX_resources_tenant_location");
        resource.HasIndex(value => new { value.LocationId, value.MemberId })
            .IsUnique()
            .HasFilter("member_id IS NOT NULL")
            .HasDatabaseName(ResourceMemberUniqueIndex);
    }

    private static void ConfigureService(ModelBuilder modelBuilder)
    {
        var service = modelBuilder.Entity<Service>();
        service.ToTable("services", Schema, table =>
        {
            table.HasCheckConstraint("CK_services_duration", "duration_minutes BETWEEN 5 AND 480 AND duration_minutes % 5 = 0");
            table.HasCheckConstraint("CK_services_buffer", "buffer_minutes BETWEEN 0 AND 240 AND buffer_minutes % 5 = 0");
        });
        service.HasKey(value => value.Id);
        service.Property(value => value.Id).HasColumnName("id").HasConversion(ServiceIdConverter).ValueGeneratedNever();
        ConfigureArchivable(service);
        service.Property(value => value.Name).HasColumnName("name").HasMaxLength(120);
        service.Property(value => value.DurationMinutes).HasColumnName("duration_minutes");
        service.Property(value => value.BufferMinutes).HasColumnName("buffer_minutes");
        service.Property(value => value.BookableByChannel).HasColumnName("bookable_by_channel");
        service.Ignore(value => value.OccupiedMinutes);
        service.HasIndex(value => value.TenantId).HasDatabaseName("IX_services_tenant");
    }

    private static void ConfigureBlock(ModelBuilder modelBuilder)
    {
        var block = modelBuilder.Entity<AvailabilityBlock>();
        block.ToTable("availability_blocks", Schema, table =>
        {
            table.HasCheckConstraint("CK_availability_blocks_kind", In<BlockKind>("kind"));
            table.HasCheckConstraint("CK_availability_blocks_session_mode", "session_mode IS NULL OR " + In<SessionMode>("session_mode"));
            table.HasCheckConstraint("CK_availability_blocks_assignment_rule", In<AssignmentRule>("assignment_rule"));
            table.HasCheckConstraint("CK_availability_blocks_capacity", "capacity_per_resource BETWEEN 1 AND 500");
            table.HasCheckConstraint("CK_availability_blocks_validity", "valid_from IS NULL OR valid_to IS NULL OR valid_from <= valid_to");
        });
        block.HasKey(value => value.Id);
        block.Property(value => value.Id).HasColumnName("id").HasConversion(BlockIdConverter).ValueGeneratedNever();
        ConfigureArchivable(block);
        block.Property(value => value.LocationId).HasColumnName("location_id").HasConversion(LocationIdConverter);
        block.Property(value => value.Name).HasColumnName("name").HasMaxLength(120);
        block.Property(value => value.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        block.Property(value => value.Weekly)
            .HasColumnName("weekly")
            .HasColumnType("jsonb")
            .HasConversion(WeeklyConverter, WeeklyComparer());
        block.Property(value => value.OneOff)
            .HasColumnName("one_off")
            .HasColumnType("jsonb")
            .HasConversion(OneOffConverter, OneOffComparer());
        block.Property(value => value.ValidFrom).HasColumnName("valid_from");
        block.Property(value => value.ValidTo).HasColumnName("valid_to");
        block.Property(value => value.ClosesOnHolidays).HasColumnName("closes_on_holidays");
        block.Property(value => value.CapacityPerResource).HasColumnName("capacity_per_resource");
        block.Property(value => value.SessionMode).HasColumnName("session_mode").HasConversion<string>().HasMaxLength(24);
        block.Property(value => value.AllowClientChoice).HasColumnName("allow_client_choice");
        block.Property(value => value.AssignmentRule).HasColumnName("assignment_rule").HasConversion<string>().HasMaxLength(16);
        // Listas ordenadas de ids en columnas uuid[]: el orden de los recursos es el de la asignación «primero libre». Son pocas por tenant y
        // se filtran en memoria; las FK de un id suelto a su recurso o servicio las revisa Application al guardar.
        block.Property(value => value.ServiceIds)
            .HasColumnName("service_ids")
            .HasColumnType("uuid[]")
            .HasConversion(IdListConverter(ServiceIdConverter), IdListComparer<ServiceId>());
        block.Property(value => value.ResourceIds)
            .HasColumnName("resource_ids")
            .HasColumnType("uuid[]")
            .HasConversion(IdListConverter(ResourceIdConverter), IdListComparer<ResourceId>());
        block.HasOne<Location>().WithMany().HasForeignKey(value => value.LocationId).OnDelete(DeleteBehavior.Restrict);
        block.HasIndex(value => new { value.TenantId, value.LocationId }).HasDatabaseName("IX_availability_blocks_tenant_location");
    }

    private static void ConfigureClosure(ModelBuilder modelBuilder)
    {
        var closure = modelBuilder.Entity<Closure>();
        closure.ToTable("closures", Schema, table => table.HasCheckConstraint("CK_closures_range", "ends_at > starts_at"));
        closure.HasKey(value => value.Id);
        closure.Property(value => value.Id).HasColumnName("id").HasConversion(ClosureIdConverter).ValueGeneratedNever();
        ConfigureVersioned(closure);
        closure.Property(value => value.LocationId).HasColumnName("location_id").HasConversion(LocationIdConverter);
        closure.Property(value => value.ResourceId).HasColumnName("resource_id").HasConversion(ResourceIdConverter);
        closure.Property(value => value.StartsAt).HasColumnName("starts_at");
        closure.Property(value => value.EndsAt).HasColumnName("ends_at");
        closure.Property(value => value.Reason).HasColumnName("reason").HasMaxLength(200);
        closure.HasOne<Location>().WithMany().HasForeignKey(value => value.LocationId).OnDelete(DeleteBehavior.Restrict);
        closure.HasOne<Resource>().WithMany().HasForeignKey(value => value.ResourceId).OnDelete(DeleteBehavior.Restrict);
        closure.HasIndex(value => new { value.TenantId, value.LocationId, value.StartsAt }).HasDatabaseName("IX_closures_tenant_location_start");
    }

    private static void ConfigureSession(ModelBuilder modelBuilder)
    {
        var session = modelBuilder.Entity<Session>();
        session.ToTable("sessions", Schema, table =>
        {
            table.HasCheckConstraint("CK_sessions_capacity", "capacity >= 1");
            table.HasCheckConstraint("CK_sessions_booked", "booked_count >= 0 AND booked_count <= capacity");
            table.HasCheckConstraint("CK_sessions_range", "ends_at > starts_at AND occupies_until >= ends_at");
        });
        session.HasKey(value => value.Id);
        session.Property(value => value.Id).HasColumnName("id").HasConversion(SessionIdConverter).ValueGeneratedNever();
        ConfigureVersioned(session);
        session.Property(value => value.ResourceId).HasColumnName("resource_id").HasConversion(ResourceIdConverter);
        session.Property(value => value.ServiceId).HasColumnName("service_id").HasConversion(ServiceIdConverter);
        session.Property(value => value.StartsAt).HasColumnName("starts_at");
        session.Property(value => value.EndsAt).HasColumnName("ends_at");
        session.Property(value => value.OccupiesUntil).HasColumnName("occupies_until");
        session.Property(value => value.Capacity).HasColumnName("capacity");
        session.Property(value => value.BookedCount).HasColumnName("booked_count");
        session.Property(value => value.IsPublished).HasColumnName("is_published");
        session.Property(value => value.IsActive).HasColumnName("is_active");
        session.Ignore(value => value.HasRoom);
        session.HasOne<Resource>().WithMany().HasForeignKey(value => value.ResourceId).OnDelete(DeleteBehavior.Restrict);
        session.HasOne<Service>().WithMany().HasForeignKey(value => value.ServiceId).OnDelete(DeleteBehavior.Restrict);
        // Una sesión es (recurso, servicio, inicio): dos peticiones que abren la misma pelean por este índice y una pierde. La restricción
        // de exclusión (SessionOccupancyConstraint) la agrega la migración a mano.
        session.HasIndex(value => new { value.ResourceId, value.ServiceId, value.StartsAt }).IsUnique().HasDatabaseName(SessionUniqueIndex);
        session.HasIndex(value => new { value.TenantId, value.StartsAt }).HasDatabaseName("IX_sessions_tenant_start");
    }

    private static void ConfigureAppointment(ModelBuilder modelBuilder)
    {
        var appointment = modelBuilder.Entity<Appointment>();
        appointment.ToTable("appointments", Schema, table =>
        {
            table.HasCheckConstraint("CK_appointments_status", In<AppointmentStatus>("status"));
            table.HasCheckConstraint("CK_appointments_source", In<AppointmentSource>("source"));
            table.HasCheckConstraint("CK_appointments_range", "ends_at > starts_at");
        });
        appointment.HasKey(value => value.Id);
        appointment.Property(value => value.Id).HasColumnName("id").HasConversion(AppointmentIdConverter).ValueGeneratedNever();
        ConfigureVersioned(appointment);
        appointment.Property(value => value.LocationId).HasColumnName("location_id").HasConversion(LocationIdConverter);
        appointment.Property(value => value.ResourceId).HasColumnName("resource_id").HasConversion(ResourceIdConverter);
        appointment.Property(value => value.ServiceId).HasColumnName("service_id").HasConversion(ServiceIdConverter);
        // customers.customers: otro módulo, sin FK. La fusión y el borrado de clientes la mueven y la miran por nombre (Tarea 11).
        appointment.Property(value => value.CustomerId).HasColumnName("customer_id");
        appointment.Property(value => value.SessionId).HasColumnName("session_id").HasConversion(SessionIdConverter);
        appointment.Property(value => value.BlockId).HasColumnName("block_id").HasConversion(BlockIdConverter);
        appointment.Property(value => value.StartsAt).HasColumnName("starts_at");
        appointment.Property(value => value.EndsAt).HasColumnName("ends_at");
        appointment.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        appointment.Property(value => value.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(16);
        // messaging.conversations: otro módulo, sin FK.
        appointment.Property(value => value.ConversationId).HasColumnName("conversation_id");
        appointment.Property(value => value.Note).HasColumnName("note").HasMaxLength(1000);
        appointment.Property(value => value.CancelReason).HasColumnName("cancel_reason").HasMaxLength(500);
        appointment.Property(value => value.ReminderSentAt).HasColumnName("reminder_sent_at");
        // The reminder worker claims a row with a single UPDATE (attempts and lease) so two pods never send the same reminder; see AppointmentReminderProcessor.
        appointment.Property(value => value.ReminderAttempts).HasColumnName("reminder_attempts").HasDefaultValue(0);
        appointment.Property(value => value.ReminderClaimedUntil).HasColumnName("reminder_claimed_until");
        appointment.Property(value => value.ReminderLastError).HasColumnName("reminder_last_error").HasMaxLength(100);
        appointment.Property(value => value.ReminderSkippedReason).HasColumnName("reminder_skipped_reason").HasMaxLength(Appointment.ReminderReasonMaxLength);
        appointment.Property(value => value.ClientRequestId).HasColumnName("client_request_id").HasMaxLength(100);
        appointment.HasOne<Location>().WithMany().HasForeignKey(value => value.LocationId).OnDelete(DeleteBehavior.Restrict);
        appointment.HasOne<Resource>().WithMany().HasForeignKey(value => value.ResourceId).OnDelete(DeleteBehavior.Restrict);
        appointment.HasOne<Service>().WithMany().HasForeignKey(value => value.ServiceId).OnDelete(DeleteBehavior.Restrict);
        appointment.HasOne<Session>().WithMany().HasForeignKey(value => value.SessionId).OnDelete(DeleteBehavior.Restrict);
        appointment.HasMany(value => value.Events)
            .WithOne()
            .HasForeignKey(entry => entry.AppointmentId)
            .OnDelete(DeleteBehavior.Cascade);
        appointment.Navigation(value => value.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
        appointment.HasIndex(value => new { value.TenantId, value.ResourceId, value.StartsAt }).HasDatabaseName("IX_appointments_tenant_resource_start");
        appointment.HasIndex(value => new { value.TenantId, value.CustomerId }).HasDatabaseName("IX_appointments_tenant_customer");
        appointment.HasIndex(value => value.SessionId).HasDatabaseName("IX_appointments_session");
        appointment.HasIndex(value => new { value.TenantId, value.ClientRequestId })
            .IsUnique()
            .HasFilter("client_request_id IS NOT NULL")
            .HasDatabaseName(AppointmentClientRequestIndex);
        // Lo que lee el recordatorio: las confirmadas que aún no lo tienen ni se omitieron.
        appointment.HasIndex(value => value.StartsAt)
            .HasFilter("status = 'Confirmed' AND reminder_sent_at IS NULL AND reminder_skipped_reason IS NULL")
            .HasDatabaseName("IX_appointments_reminder_due");

        var entry = modelBuilder.Entity<AppointmentEvent>();
        entry.ToTable("appointment_events", Schema, table => table.HasCheckConstraint("CK_appointment_events_action", In<AppointmentAction>("action")));
        entry.HasKey(value => value.Id);
        entry.Property(value => value.Id).HasColumnName("id").HasConversion(EventIdConverter).ValueGeneratedNever();
        entry.Property(value => value.AppointmentId).HasColumnName("appointment_id").HasConversion(AppointmentIdConverter);
        entry.Property(value => value.Action).HasColumnName("action").HasConversion<string>().HasMaxLength(16);
        entry.Property(value => value.At).HasColumnName("at");
        entry.Property(value => value.ActorMemberId).HasColumnName("actor_member_id");
        entry.Property(value => value.Detail).HasColumnName("detail").HasMaxLength(500);
        // Expand (spec 2026-11-23 D-17): el default cubre las filas que ya existen y a los pods de la versión anterior, que no la escriben.
        entry.Property(value => value.ByChannel).HasColumnName("by_channel").HasDefaultValue(false);
        entry.HasIndex(value => new { value.AppointmentId, value.At }).HasDatabaseName("IX_appointment_events_appointment_at");
    }

    private static void ConfigureSettings(ModelBuilder modelBuilder)
    {
        var settings = modelBuilder.Entity<SchedulingSettings>();
        settings.ToTable("settings", Schema, table =>
        {
            table.HasCheckConstraint("CK_settings_confirmation_mode", In<ConfirmationMode>("confirmation_mode"));
            table.HasCheckConstraint("CK_settings_min_notice", "min_notice_minutes BETWEEN 0 AND 10080");
            table.HasCheckConstraint("CK_settings_horizon", "horizon_days BETWEEN 1 AND 365");
            table.HasCheckConstraint("CK_settings_cutoff", "client_cancel_cutoff_hours BETWEEN 0 AND 720");
            table.HasCheckConstraint("CK_settings_reminder_offset", "reminder_offset_hours BETWEEN 1 AND 168");
        });
        // Una fila por tenant: la clave es el tenant, y es el blanco del ON CONFLICT del aprovisionador.
        settings.HasKey(value => value.TenantId);
        ConfigureVersioned(settings);
        settings.Property(value => value.ConfirmationMode).HasColumnName("confirmation_mode").HasConversion<string>().HasMaxLength(16);
        settings.Property(value => value.MinNoticeMinutes).HasColumnName("min_notice_minutes");
        settings.Property(value => value.HorizonDays).HasColumnName("horizon_days");
        settings.Property(value => value.ClientCancelCutoffHours).HasColumnName("client_cancel_cutoff_hours");
        settings.Property(value => value.ReminderEnabled).HasColumnName("reminder_enabled");
        settings.Property(value => value.ReminderOffsetHours).HasColumnName("reminder_offset_hours");
        // messaging.connections y messaging.message_templates: otro módulo, sin FK.
        settings.Property(value => value.ReminderConnectionId).HasColumnName("reminder_connection_id");
        // Tenancy.memberships: otro módulo, sin FK. Nullable: los pods anteriores no la escriben (expand).
        settings.Property(value => value.ReminderSenderMemberId).HasColumnName("reminder_sender_member_id");
        // Expand/contract (spec 2026-11-23 §2): the nullable column reminder_template_id is still in the table but no longer mapped, so pods of
        // the previous version keep working against this schema. A later migration drops it, once no old pod reads it.
    }

    // --- Conversiones de las columnas compuestas ---

    private sealed record RangeJson(int From, int To);

    private sealed record WeekdayJson(int Day, List<RangeJson> Ranges);

    private sealed record OneOffJson(string Date, List<RangeJson> Ranges);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string WriteWeekly(IReadOnlyList<WeekdayHours> weekly) =>
        JsonSerializer.Serialize(
            weekly.Select(day => new WeekdayJson(day.Day, [.. day.Ranges.Select(range => new RangeJson(range.From, range.To))])), Json);

    private static IReadOnlyList<WeekdayHours> ReadWeekly(string json) =>
        [.. (JsonSerializer.Deserialize<List<WeekdayJson>>(json, Json) ?? [])
            .Select(day => new WeekdayHours(day.Day, day.Ranges.Select(range => new TimeRange(range.From, range.To))))];

    private static string? WriteOneOff(DateHours? window) =>
        window is null
            ? null
            : JsonSerializer.Serialize(
                new OneOffJson(window.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    [.. window.Ranges.Select(range => new RangeJson(range.From, range.To))]),
                Json);

    private static DateHours? ReadOneOff(string? json)
    {
        if (json is null)
        {
            return null;
        }

        var value = JsonSerializer.Deserialize<OneOffJson>(json, Json)!;
        return new DateHours(
            DateOnly.ParseExact(value.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            value.Ranges.Select(range => new TimeRange(range.From, range.To)));
    }

    private static readonly ValueConverter<IReadOnlyList<WeekdayHours>, string> WeeklyConverter = new(weekly => WriteWeekly(weekly), json => ReadWeekly(json));

    private static readonly ValueConverter<DateHours?, string?> OneOffConverter = new(window => WriteOneOff(window), json => ReadOneOff(json));

    private static ValueComparer<IReadOnlyList<WeekdayHours>> WeeklyComparer() =>
        new((left, right) => WriteWeekly(left!) == WriteWeekly(right!), value => WriteWeekly(value).GetHashCode(), value => value);

    private static ValueComparer<DateHours?> OneOffComparer() =>
        new((left, right) => WriteOneOff(left) == WriteOneOff(right), value => (WriteOneOff(value) ?? string.Empty).GetHashCode(), value => value);

    private static ValueConverter<IReadOnlyList<TId>, Guid[]> IdListConverter<TId>(ValueConverter<TId, Guid> single)
        where TId : struct =>
        new(
            list => list.Select(id => (Guid)single.ConvertToProvider(id)!).ToArray(),
            array => array.Select(value => (TId)single.ConvertFromProvider(value)!).ToList());

    private static ValueComparer<IReadOnlyList<TId>> IdListComparer<TId>()
        where TId : struct =>
        new(
            (left, right) => left!.SequenceEqual(right!),
            list => list.Aggregate(0, (hash, id) => HashCode.Combine(hash, id)),
            list => list);
}
