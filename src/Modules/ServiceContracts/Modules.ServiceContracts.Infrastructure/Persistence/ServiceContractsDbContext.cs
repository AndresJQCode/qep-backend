using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Infrastructure.Persistence;

public sealed class ServiceContractsDbContext(DbContextOptions<ServiceContractsDbContext> options)
    : DbContext(options)
{
    public const string Schema = "billing";

    public DbSet<ServiceContract> Services => Set<ServiceContract>();

    public DbSet<Charge> Charges => Set<Charge>();

    public DbSet<CustomerBillingProfile> Profiles => Set<CustomerBillingProfile>();

    public DbSet<ServiceContractsSettings> Settings => Set<ServiceContractsSettings>();

    internal DbSet<CustomerMessage> CustomerMessages => Set<CustomerMessage>();

    internal DbSet<ReminderStep> ReminderSteps => Set<ReminderStep>();

    internal DbSet<DailyDigest> DailyDigests => Set<DailyDigest>();

    internal DbSet<TenantRun> TenantRuns => Set<TenantRun>();

    // Proyección de escritura de audit.entries (ExcludeFromMigrations): el módulo Audit es dueño de la tabla.
    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
        ConfigureProfile(modelBuilder);
        ConfigureService(modelBuilder);
        ConfigureCharge(modelBuilder);
        ConfigureCustomerMessage(modelBuilder);
        ConfigureReminderStep(modelBuilder);
        ConfigureDailyDigest(modelBuilder);
        ConfigureTenantRun(modelBuilder);
        ConfigureSettings(modelBuilder);
    }

    // Todos los `version` son bigint en la base (spec §2) aunque el dominio los lleve como int: el
    // tipo de columna se fija a mano para que un cambio futuro del CLR no cambie el esquema.
    private static void ConfigureProfile(ModelBuilder modelBuilder)
    {
        var profile = modelBuilder.Entity<CustomerBillingProfile>();
        profile.ToTable("customer_profiles", Schema, table =>
            table.HasCheckConstraint("CK_customer_profiles_channel", "channel IN ('WhatsApp','Email')"));
        profile.HasKey(value => new { value.TenantId, value.CustomerId });
        profile.Property(value => value.TenantId).HasColumnName("tenant_id");
        profile.Property(value => value.CustomerId).HasColumnName("customer_id");
        profile.Property(value => value.Channel)
            .HasColumnName("channel")
            .HasConversion<string>()
            .HasMaxLength(16);
        profile.Property(value => value.AssigneeMemberId).HasColumnName("assignee_member_id");
        profile.Property(value => value.Version)
            .HasColumnName("version")
            .HasColumnType("bigint")
            .IsConcurrencyToken();
        profile.Property(value => value.CreatedAt).HasColumnName("created_at");
        profile.Property(value => value.UpdatedAt).HasColumnName("updated_at");
    }

    private static void ConfigureService(ModelBuilder modelBuilder)
    {
        var service = modelBuilder.Entity<ServiceContract>();
        service.ToTable("services", Schema, table =>
        {
            table.HasCheckConstraint("CK_services_cycle_months", "cycle_months IN (1, 6, 12)");
            table.HasCheckConstraint("CK_services_status", "status IN ('Active','Paused','Cancelled')");
        });
        service.HasKey(value => value.Id);
        service.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new ServiceContractId(value))
            .ValueGeneratedNever();
        service.Property(value => value.TenantId).HasColumnName("tenant_id");
        service.Property(value => value.CustomerId).HasColumnName("customer_id");
        service.Property(value => value.Name)
            .HasColumnName("name")
            .HasMaxLength(ServiceContract.NameMaxLength);
        service.Property(value => value.Price).HasColumnName("price").HasPrecision(18, 2);
        service.Property(value => value.Currency).HasColumnName("currency").HasMaxLength(3);
        // El valor numérico del enum son los meses (BillingCycle): `cycle_months` guarda 1, 6 o 12.
        service.Property(value => value.Cycle)
            .HasColumnName("cycle_months")
            .HasConversion<short>();
        service.Property(value => value.FirstDueDate).HasColumnName("first_due_date");
        service.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);
        service.Property(value => value.ReactivatedOn).HasColumnName("reactivated_on");
        service.Property(value => value.LastGeneratedDueDate).HasColumnName("last_generated_due_date");
        service.Property(value => value.Version)
            .HasColumnName("version")
            .HasColumnType("bigint")
            .IsConcurrencyToken();
        service.Property(value => value.CreatedAt).HasColumnName("created_at");
        service.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        service.Property(value => value.StatusChangedAt).HasColumnName("status_changed_at");
        service.HasIndex(value => new { value.TenantId, value.CustomerId })
            .HasDatabaseName("IX_services_tenant_customer");
    }

    private static void ConfigureCharge(ModelBuilder modelBuilder)
    {
        var charge = modelBuilder.Entity<Charge>();
        charge.ToTable("charges", Schema, table =>
            table.HasCheckConstraint("CK_charges_status", "status IN ('Pending','Paid','Cancelled')"));
        charge.HasKey(value => value.Id);
        charge.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        charge.Property(value => value.TenantId).HasColumnName("tenant_id");
        charge.Property(value => value.ServiceId)
            .HasColumnName("service_id")
            .HasConversion(id => id.Value, value => new ServiceContractId(value));
        charge.Property(value => value.CustomerId).HasColumnName("customer_id");
        charge.Property(value => value.Amount).HasColumnName("amount").HasPrecision(18, 2);
        charge.Property(value => value.Currency).HasColumnName("currency").HasMaxLength(3);
        charge.Property(value => value.DueDate).HasColumnName("due_date");
        // `Overdue` no se guarda: es Pending con due_date < hoy del tenant, y se calcula al responder.
        charge.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);
        charge.Property(value => value.PaidOn).HasColumnName("paid_on");
        charge.Property(value => value.PaymentNote)
            .HasColumnName("payment_note")
            .HasMaxLength(Charge.PaymentNoteMaxLength);
        charge.Property(value => value.PaidByMemberId).HasColumnName("paid_by_member_id");
        charge.Property(value => value.PaidAt).HasColumnName("paid_at");
        charge.Property(value => value.Version)
            .HasColumnName("version")
            .HasColumnType("bigint")
            .IsConcurrencyToken();
        charge.Property(value => value.CreatedAt).HasColumnName("created_at");
        charge.HasIndex(value => new { value.TenantId, value.Status, value.DueDate })
            .HasDatabaseName("IX_charges_tenant_status_due");
        charge.HasIndex(value => new { value.TenantId, value.CustomerId, value.Status })
            .HasDatabaseName("IX_charges_tenant_customer_status");
        // La deduplicación de la generación: dos réplicas que generen el mismo período chocan acá y
        // no duplican el cobro (Review Focus 2).
        charge.HasIndex(value => new { value.ServiceId, value.DueDate })
            .IsUnique()
            .HasDatabaseName("IX_charges_service_due");
    }

    private static void ConfigureCustomerMessage(ModelBuilder modelBuilder)
    {
        var message = modelBuilder.Entity<CustomerMessage>();
        message.ToTable("customer_messages", Schema, table =>
        {
            table.HasCheckConstraint("CK_customer_messages_kind", "kind IN ('Reminder','Overdue')");
            table.HasCheckConstraint("CK_customer_messages_status", "status IN ('Sending','Sent','Failed')");
        });
        message.HasKey(value => value.Id);
        message.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        message.Property(value => value.TenantId).HasColumnName("tenant_id");
        message.Property(value => value.CustomerId).HasColumnName("customer_id");
        message.Property(value => value.RunDate).HasColumnName("run_date");
        message.Property(value => value.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(16);
        message.Property(value => value.Channel)
            .HasColumnName("channel")
            .HasConversion<string>()
            .HasMaxLength(16);
        message.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);
        message.Property(value => value.FailureCode).HasColumnName("failure_code").HasMaxLength(64);
        message.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        message.Property(value => value.AttemptedAt).HasColumnName("attempted_at");
        message.Property(value => value.SentAt).HasColumnName("sent_at");
        message.HasIndex(value => new { value.TenantId, value.CustomerId, value.RunDate })
            .IsUnique()
            .HasDatabaseName("IX_customer_messages_tenant_customer_day");
    }

    private static void ConfigureReminderStep(ModelBuilder modelBuilder)
    {
        var step = modelBuilder.Entity<ReminderStep>();
        step.ToTable("reminder_steps", Schema);
        step.HasKey(value => new { value.ChargeId, value.Step });
        step.Property(value => value.ChargeId).HasColumnName("charge_id");
        step.Property(value => value.Step).HasColumnName("step").HasMaxLength(16);
        step.Property(value => value.MessageId).HasColumnName("message_id");
        step.Property(value => value.TenantId).HasColumnName("tenant_id");
        // El resultado de un paso es el de su mensaje (spec §2), por eso la FK es real.
        step.HasOne<CustomerMessage>().WithMany().HasForeignKey(value => value.MessageId);
        step.HasIndex(value => value.MessageId).HasDatabaseName("IX_reminder_steps_message");
    }

    private static void ConfigureDailyDigest(ModelBuilder modelBuilder)
    {
        var digest = modelBuilder.Entity<DailyDigest>();
        digest.ToTable("daily_digests", Schema, table =>
            table.HasCheckConstraint("CK_daily_digests_status", "status IN ('Sending','Sent','Failed')"));
        digest.HasKey(value => new { value.TenantId, value.MemberId, value.RunDate });
        digest.Property(value => value.TenantId).HasColumnName("tenant_id");
        digest.Property(value => value.MemberId).HasColumnName("member_id");
        digest.Property(value => value.RunDate).HasColumnName("run_date");
        digest.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16);
        digest.Property(value => value.FailureCode).HasColumnName("failure_code").HasMaxLength(64);
        digest.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        digest.Property(value => value.SentAt).HasColumnName("sent_at");
    }

    private static void ConfigureTenantRun(ModelBuilder modelBuilder)
    {
        var run = modelBuilder.Entity<TenantRun>();
        run.ToTable("tenant_runs", Schema);
        run.HasKey(value => new { value.TenantId, value.RunDate });
        run.Property(value => value.TenantId).HasColumnName("tenant_id");
        run.Property(value => value.RunDate).HasColumnName("run_date");
        run.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        run.Property(value => value.FinishedAt).HasColumnName("finished_at");
    }

    private static void ConfigureSettings(ModelBuilder modelBuilder)
    {
        var settings = modelBuilder.Entity<ServiceContractsSettings>();
        settings.ToTable("settings", Schema);
        settings.HasKey(value => value.TenantId);
        settings.Property(value => value.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
        settings.Property(value => value.WhatsAppConnectionId).HasColumnName("whatsapp_connection_id");
        settings.Property(value => value.Version)
            .HasColumnName("version")
            .HasColumnType("bigint")
            .IsConcurrencyToken();
        settings.Property(value => value.UpdatedAt).HasColumnName("updated_at");
    }
}
