using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;
using Modules.Automations.Domain;

namespace Modules.Automations.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>automations</c> (spec 2026-10-21 §4.2): los chatbots con sus versiones y conexiones, las plantillas del tenant, las
/// corridas del motor, el registro de llamadas HTTP y las simulaciones. Proyecta <c>audit.entries</c> sin ser su dueño
/// (ExcludeFromMigrations). Sin FKs a otros esquemas: conexiones, conversaciones y miembros son referencias blandas. Los dos índices
/// por <c>lower(name)</c> van en la migración con SQL: EF no modela índices por expresión (mismo caso que
/// <c>MessagingDbContext.BusinessHoursNameIndex</c>).
/// </summary>
public sealed class AutomationsDbContext(DbContextOptions<AutomationsDbContext> options) : DbContext(options)
{
    public const string Schema = "automations";

    /// <summary>§4.2: único por <c>(tenant_id, lower(name))</c>, por SQL en <c>AddAutomations</c>. La unidad de trabajo lo traduce por
    /// este nombre a 409 <c>automations.chatbot.name_taken</c>.</summary>
    public const string ChatbotNameIndex = "IX_chatbots_tenant_name";

    /// <summary>D-C4: una conexión, un chatbot activo (<c>(tenant_id, connection_id) WHERE active</c>). La unidad de trabajo lo
    /// traduce por este nombre a 409 <c>automations.chatbot.connection_taken</c>.</summary>
    public const string ActiveConnectionIndex = "IX_chatbot_connections_active";

    /// <summary>§4.2: <c>UNIQUE (chatbot_id, number)</c>. Dos publicaciones concurrentes proponen el mismo número; la segunda choca
    /// con este índice.</summary>
    public const string VersionNumberIndex = "IX_chatbot_versions_chatbot_number";

    /// <summary>§4.2: único por <c>(tenant_id, lower(name))</c>, por SQL en <c>AddAutomations</c>.</summary>
    public const string TemplateNameIndex = "IX_chatbot_templates_tenant_name";

    /// <summary>D-M12: una corrida viva por conversación. El motor (plan 2) discrimina por este nombre la carrera entre dos
    /// arranques.</summary>
    public const string LiveRunIndex = "IX_conversation_runs_live";

    public const string LiveRunFilter = "status IN ('Running','Waiting')";

    public const string StatusCheck = "status IN ('Draft','Active','Paused')";

    public const string TriggerCheck = "trigger IN ('NewConversation','Keyword','OutsideHours')";

    /// <summary>Borrador sin versión publicada; publicado (activo o en pausa) con ella.</summary>
    public const string PublishedCheck = "(status = 'Draft') = (published_version_id IS NULL)";

    public const string MaxAttemptsCheck = "max_attempts BETWEEN 1 AND 5";

    /// <summary>Spec 2026-10-25 D-2: los dos valores de <c>AttemptsExhaustedAction</c>, por nombre.</summary>
    public const string ExhaustedActionCheck = "exhausted_action IN ('Restart','Handoff')";

    /// <summary>Spec 2026-10-25 D-2: sólo <c>Handoff</c> lleva miembro; <c>Restart</c> ⇒ sin miembro (el agregado ya lo garantiza).</summary>
    public const string ExhaustedMemberCheck = "exhausted_action = 'Handoff' OR exhausted_member_id IS NULL";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DbSet<Chatbot> Chatbots => Set<Chatbot>();

    public DbSet<ChatbotVersion> ChatbotVersions => Set<ChatbotVersion>();

    public DbSet<ChatbotConnection> ChatbotConnections => Set<ChatbotConnection>();

    public DbSet<ChatbotTemplate> Templates => Set<ChatbotTemplate>();

    public DbSet<ConversationRun> Runs => Set<ConversationRun>();

    internal DbSet<HttpCallRecord> HttpCalls => Set<HttpCallRecord>();

    public DbSet<Simulation> Simulations => Set<Simulation>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureChatbot(modelBuilder);
        ConfigureVersion(modelBuilder);
        ConfigureConnection(modelBuilder);
        ConfigureTemplate(modelBuilder);
        ConfigureRun(modelBuilder);
        ConfigureHttpCall(modelBuilder);
        ConfigureSimulation(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
    }

    private static void ConfigureChatbot(ModelBuilder modelBuilder)
    {
        var chatbot = modelBuilder.Entity<Chatbot>();
        chatbot.ToTable("chatbots", Schema, table =>
        {
            table.HasCheckConstraint("CK_chatbots_status", StatusCheck);
            table.HasCheckConstraint("CK_chatbots_trigger", TriggerCheck);
            table.HasCheckConstraint("CK_chatbots_published", PublishedCheck);
            table.HasCheckConstraint("CK_chatbots_max_attempts", MaxAttemptsCheck);
            table.HasCheckConstraint("CK_chatbots_exhausted_action", ExhaustedActionCheck);
            table.HasCheckConstraint("CK_chatbots_exhausted_member", ExhaustedMemberCheck);
        });
        chatbot.HasKey(value => value.Id);
        chatbot.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        chatbot.Property(value => value.TenantId).HasColumnName("tenant_id");
        chatbot.Property(value => value.Name).HasColumnName("name").HasMaxLength(Chatbot.NameMaxLength);
        chatbot.Property(value => value.Description).HasColumnName("description").HasMaxLength(Chatbot.DescriptionMaxLength)
            .HasDefaultValue(string.Empty);
        // Por nombre, con el DEFAULT del spec. El centinela hace que EF escriba siempre la columna, también cuando vale el 0 del enum
        // (mismo criterio que MessagingDbContext con channel).
        chatbot.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16)
            .HasDefaultValue(ChatbotStatus.Draft).HasSentinel((ChatbotStatus)(-1));
        chatbot.Property(value => value.Trigger).HasColumnName("trigger").HasConversion<string>().HasMaxLength(24)
            .HasDefaultValue(ChatbotTrigger.NewConversation).HasSentinel((ChatbotTrigger)(-1));
        chatbot.Property(value => value.Keywords).HasColumnName("keywords").HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, JsonOptions),
                value => JsonSerializer.Deserialize<List<string>>(value, JsonOptions)!,
                new ValueComparer<IReadOnlyList<string>>(
                    (left, right) => SameStrings(left, right),
                    value => value.Aggregate(0, (hash, keyword) => HashCode.Combine(hash, keyword)),
                    value => value.ToList()))
            .HasDefaultValueSql("'[]'");
        chatbot.Property(value => value.RestartKeyword).HasColumnName("restart_keyword").HasMaxLength(Chatbot.RestartKeywordMaxLength)
            .HasDefaultValue(Chatbot.DefaultRestartKeyword);
        chatbot.Property(value => value.MaxAttempts).HasColumnName("max_attempts").HasConversion<short>()
            .HasDefaultValue(Chatbot.DefaultMaxAttempts).HasSentinel(-1);
        // Spec 2026-10-25 D-2: por nombre, como trigger; el centinela hace que EF escriba también el 0 del enum (Restart).
        chatbot.Property(value => value.ExhaustedAction).HasColumnName("exhausted_action").HasConversion<string>().HasMaxLength(16)
            .HasDefaultValue(AttemptsExhaustedAction.Restart).HasSentinel((AttemptsExhaustedAction)(-1));
        chatbot.Property(value => value.ExhaustedMemberId).HasColumnName("exhausted_member_id");
        chatbot.Property(value => value.DraftFlowJson).HasColumnName("draft_flow").HasColumnType("jsonb");
        chatbot.Property(value => value.DraftLayoutJson).HasColumnName("draft_layout").HasColumnType("jsonb").HasDefaultValueSql("'{}'");
        chatbot.Property(value => value.DraftUpdatedAt).HasColumnName("draft_updated_at");
        chatbot.Property(value => value.DraftUpdatedBy).HasColumnName("draft_updated_by");
        // Sin FK a chatbot_versions: la versión apunta al chatbot (cascade) y una FK de vuelta haría el ciclo.
        chatbot.Property(value => value.PublishedVersionId).HasColumnName("published_version_id");
        chatbot.Property(value => value.CreatedAt).HasColumnName("created_at");
        chatbot.Property(value => value.CreatedBy).HasColumnName("created_by");
        chatbot.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        chatbot.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken().HasDefaultValue(1L);
        chatbot.HasMany(value => value.Versions).WithOne().HasForeignKey(version => version.ChatbotId).OnDelete(DeleteBehavior.Cascade);
        chatbot.Navigation(value => value.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);
        chatbot.HasMany(value => value.Connections).WithOne().HasForeignKey(connection => connection.ChatbotId).OnDelete(DeleteBehavior.Cascade);
        chatbot.Navigation(value => value.Connections).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureVersion(ModelBuilder modelBuilder)
    {
        var version = modelBuilder.Entity<ChatbotVersion>();
        version.ToTable("chatbot_versions", Schema);
        version.HasKey(value => value.Id);
        version.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        version.Property(value => value.ChatbotId).HasColumnName("chatbot_id");
        version.Property(value => value.TenantId).HasColumnName("tenant_id");
        version.Property(value => value.Number).HasColumnName("number");
        version.Property(value => value.FlowJson).HasColumnName("flow").HasColumnType("jsonb");
        version.Property(value => value.LayoutJson).HasColumnName("layout").HasColumnType("jsonb");
        version.Property(value => value.PublishedAt).HasColumnName("published_at");
        version.Property(value => value.PublishedBy).HasColumnName("published_by");
        // §4.2: UNIQUE (chatbot_id, number). Dos publicaciones concurrentes proponen el mismo número; la segunda choca acá.
        version.HasIndex(value => new { value.ChatbotId, value.Number }, VersionNumberIndex).IsUnique();
    }

    private static void ConfigureConnection(ModelBuilder modelBuilder)
    {
        var connection = modelBuilder.Entity<ChatbotConnection>();
        connection.ToTable("chatbot_connections", Schema);
        connection.HasKey(value => new { value.ChatbotId, value.ConnectionId });
        connection.Property(value => value.ChatbotId).HasColumnName("chatbot_id");
        connection.Property(value => value.TenantId).HasColumnName("tenant_id");
        connection.Property(value => value.ConnectionId).HasColumnName("connection_id");
        connection.Property(value => value.Active).HasColumnName("active").HasDefaultValue(false);
        connection.HasIndex(value => new { value.TenantId, value.ConnectionId }, ActiveConnectionIndex).IsUnique().HasFilter("active");
    }

    private static void ConfigureTemplate(ModelBuilder modelBuilder)
    {
        var template = modelBuilder.Entity<ChatbotTemplate>();
        template.ToTable("chatbot_templates", Schema);
        template.HasKey(value => value.Id);
        template.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        template.Property(value => value.TenantId).HasColumnName("tenant_id");
        template.Property(value => value.Name).HasColumnName("name").HasMaxLength(ChatbotTemplate.NameMaxLength);
        template.Property(value => value.Category).HasColumnName("category").HasMaxLength(ChatbotTemplate.CategoryMaxLength);
        template.Property(value => value.Description).HasColumnName("description").HasMaxLength(ChatbotTemplate.DescriptionMaxLength)
            .HasDefaultValue(string.Empty);
        template.Property(value => value.FlowJson).HasColumnName("flow").HasColumnType("jsonb");
        template.Property(value => value.LayoutJson).HasColumnName("layout").HasColumnType("jsonb");
        template.Property(value => value.ParametersJson).HasColumnName("parameters").HasColumnType("jsonb").HasDefaultValueSql("'[]'");
        template.Property(value => value.CreatedAt).HasColumnName("created_at");
        template.Property(value => value.CreatedBy).HasColumnName("created_by");
    }

    private static void ConfigureRun(ModelBuilder modelBuilder)
    {
        var run = modelBuilder.Entity<ConversationRun>();
        run.ToTable("conversation_runs", Schema);
        run.HasKey(value => value.Id);
        run.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        run.Property(value => value.TenantId).HasColumnName("tenant_id");
        run.Property(value => value.ConversationId).HasColumnName("conversation_id");
        run.Property(value => value.ConnectionId).HasColumnName("connection_id");
        run.Property(value => value.ChatbotId).HasColumnName("chatbot_id");
        run.Property(value => value.ChatbotVersionId).HasColumnName("chatbot_version_id");
        run.Property(value => value.Status).HasColumnName("status").HasMaxLength(16).HasConversion<string>();
        run.Property(value => value.CurrentNodeId).HasColumnName("current_node_id").HasMaxLength(64);
        run.Property(value => value.StepNumber).HasColumnName("step_number").HasDefaultValue(0);
        run.Property(value => value.Attempts).HasColumnName("attempts").HasColumnType("smallint").HasDefaultValue(0);
        run.Property(value => value.VariablesJson).HasColumnName("variables").HasColumnType("jsonb").HasDefaultValueSql("'{}'");
        run.Property(value => value.TraceJson).HasColumnName("trace").HasColumnType("jsonb").HasDefaultValueSql("'[]'");
        run.Property(value => value.StartedAt).HasColumnName("started_at");
        run.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        run.Property(value => value.EndedAt).HasColumnName("ended_at");
        run.Property(value => value.FailureCode).HasColumnName("failure_code").HasMaxLength(64);
        run.Property(value => value.LastInboundMessageId).HasColumnName("last_inbound_message_id");
        run.HasIndex(value => value.ConversationId, LiveRunIndex).IsUnique().HasFilter(LiveRunFilter);
        // «Hoy» de la lista y el historial por chatbot, lo más nuevo primero.
        run.HasIndex(value => new { value.TenantId, value.ChatbotId, value.StartedAt }, "IX_conversation_runs_tenant_chatbot_started")
            .IsDescending(false, false, true);
        // Ruling P2-R44: FindLatestAsync corre en cada unidad sin corrida viva (el marcador de P2-R24), y sin esto es un seq scan.
        run.HasIndex(value => new { value.TenantId, value.ConversationId, value.StartedAt }, "IX_conversation_runs_tenant_conversation_started")
            .IsDescending(false, false, true);
        // D-M13 y P2-R44: el barrido de abandonadas lee sólo las Waiting por updated_at; parcial, porque casi todas las filas ya terminaron.
        run.HasIndex(value => value.UpdatedAt, "IX_conversation_runs_waiting_updated").HasFilter("status = 'Waiting'");
    }

    private static void ConfigureHttpCall(ModelBuilder modelBuilder)
    {
        var call = modelBuilder.Entity<HttpCallRecord>();
        call.ToTable("http_calls", Schema);
        call.HasKey(value => value.Id);
        call.Property(value => value.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        call.Property(value => value.TenantId).HasColumnName("tenant_id");
        call.Property(value => value.RunId).HasColumnName("run_id");
        call.Property(value => value.SimulationId).HasColumnName("simulation_id");
        call.Property(value => value.NodeId).HasColumnName("node_id").HasMaxLength(64);
        call.Property(value => value.ConnectionId).HasColumnName("connection_id");
        call.Property(value => value.Method).HasColumnName("method").HasMaxLength(8);
        call.Property(value => value.Path).HasColumnName("path").HasMaxLength(512);
        call.Property(value => value.StatusCode).HasColumnName("status_code");
        call.Property(value => value.DurationMs).HasColumnName("duration_ms");
        // §4.2 decía varchar(16); el ejecutor (plan 2, tarea 11) registra también response_too_large (18). 24 deja margen a los de
        // HttpCallOutcomes sin cambiar ningún nombre.
        call.Property(value => value.Outcome).HasColumnName("outcome").HasMaxLength(24);
        call.Property(value => value.ErrorCode).HasColumnName("error_code").HasMaxLength(64);
        call.Property(value => value.CalledAt).HasColumnName("called_at");
        call.HasIndex(value => new { value.TenantId, value.CalledAt }, "IX_http_calls_tenant_called").IsDescending(false, true);
    }

    private static void ConfigureSimulation(ModelBuilder modelBuilder)
    {
        var simulation = modelBuilder.Entity<Simulation>();
        simulation.ToTable("simulations", Schema);
        simulation.HasKey(value => value.Id);
        simulation.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        simulation.Property(value => value.TenantId).HasColumnName("tenant_id");
        simulation.Property(value => value.ChatbotId).HasColumnName("chatbot_id");
        simulation.Property(value => value.MemberId).HasColumnName("member_id");
        simulation.Property(value => value.LiveHttp).HasColumnName("live_http").HasDefaultValue(false);
        simulation.Property(value => value.StateJson).HasColumnName("state").HasColumnType("jsonb");
        simulation.Property(value => value.TranscriptJson).HasColumnName("transcript").HasColumnType("jsonb").HasDefaultValueSql("'[]'");
        simulation.Property(value => value.CreatedAt).HasColumnName("created_at");
        simulation.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        simulation.Property(value => value.ExpiresAt).HasColumnName("expires_at");
    }

    private static bool SameStrings(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        ReferenceEquals(left, right) || (left is not null && right is not null && left.SequenceEqual(right, StringComparer.Ordinal));
}
