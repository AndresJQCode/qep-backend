using Microsoft.EntityFrameworkCore;
using Modules.Ai.Domain;
using Modules.Audit.Domain;
using Modules.Audit.Infrastructure.Persistence;

namespace Modules.Ai.Infrastructure.Persistence;

/// <summary>
/// Esquema <c>ai</c> (spec 2026-10-29 §4.2): los agentes con sus versiones y conexiones. Las sesiones, los trabajos del modelo, las
/// herramientas y el consumo llegan con las tareas que los producen. Proyecta <c>audit.entries</c> sin ser su dueño
/// (ExcludeFromMigrations). Sin FKs a otros esquemas: conexiones y miembros son referencias blandas. El índice por <c>lower(name)</c> va
/// en la migración con SQL: EF no modela índices por expresión (mismo caso que <c>AutomationsDbContext.ChatbotNameIndex</c>).
/// </summary>
public sealed class AiDbContext(DbContextOptions<AiDbContext> options) : DbContext(options)
{
    public const string Schema = "ai";

    /// <summary>D-A11: nombre único por tenant sin importar mayúsculas. <c>AiUnitOfWork</c> lo traduce por nombre a 409.</summary>
    public const string AgentNameIndex = "IX_agents_tenant_name";

    /// <summary>§4.2: dos publicaciones simultáneas proponen el mismo número; la segunda choca acá y es 412.</summary>
    public const string VersionNumberIndex = "IX_agent_versions_agent_number";

    public const string StatusCheck = "status IN ('Draft','Active','Paused')";

    /// <summary>§4.2: publicado si y sólo si tiene versión publicada.</summary>
    public const string PublishedCheck = "(status = 'Draft') = (published_version_id IS NULL)";

    public DbSet<Agent> Agents => Set<Agent>();

    public DbSet<AgentVersion> AgentVersions => Set<AgentVersion>();

    public DbSet<AgentConnection> AgentConnections => Set<AgentConnection>();

    internal DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureAgent(modelBuilder);
        ConfigureVersion(modelBuilder);
        ConfigureConnection(modelBuilder);
        AuditDbContext.ConfigureEntry(modelBuilder, ownsTable: false);
    }

    private static void ConfigureAgent(ModelBuilder modelBuilder)
    {
        var agent = modelBuilder.Entity<Agent>();
        agent.ToTable("agents", Schema, table =>
        {
            table.HasCheckConstraint("CK_agents_status", StatusCheck);
            table.HasCheckConstraint("CK_agents_published", PublishedCheck);
        });
        agent.HasKey(value => value.Id);
        agent.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        agent.Property(value => value.TenantId).HasColumnName("tenant_id");
        agent.Property(value => value.Name).HasColumnName("name").HasMaxLength(Agent.NameMaxLength);
        // Por nombre, con el DEFAULT del spec. El centinela hace que EF escriba siempre la columna, también el 0 del enum (Draft).
        agent.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16)
            .HasDefaultValue(AgentStatus.Draft).HasSentinel((AgentStatus)(-1));
        agent.Property(value => value.DraftConfigJson).HasColumnName("draft_config").HasColumnType("jsonb");
        agent.Property(value => value.DraftUpdatedAt).HasColumnName("draft_updated_at");
        agent.Property(value => value.DraftUpdatedBy).HasColumnName("draft_updated_by");
        // Sin FK a agent_versions: la versión apunta al agente (cascade) y una FK de vuelta haría el ciclo.
        agent.Property(value => value.PublishedVersionId).HasColumnName("published_version_id");
        agent.Property(value => value.CreatedAt).HasColumnName("created_at");
        agent.Property(value => value.CreatedBy).HasColumnName("created_by");
        agent.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        agent.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken().HasDefaultValue(1L);
        agent.HasMany(value => value.Versions).WithOne().HasForeignKey(version => version.AgentId).OnDelete(DeleteBehavior.Cascade);
        agent.Navigation(value => value.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);
        agent.HasMany(value => value.Connections).WithOne().HasForeignKey(connection => connection.AgentId).OnDelete(DeleteBehavior.Cascade);
        agent.Navigation(value => value.Connections).UsePropertyAccessMode(PropertyAccessMode.Field);
        agent.Ignore(value => value.DraftConfig);
        agent.Ignore(value => value.PublishedVersion);
        agent.Ignore(value => value.HasDraftChanges);
    }

    private static void ConfigureVersion(ModelBuilder modelBuilder)
    {
        var version = modelBuilder.Entity<AgentVersion>();
        version.ToTable("agent_versions", Schema);
        version.HasKey(value => value.Id);
        version.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        version.Property(value => value.AgentId).HasColumnName("agent_id");
        version.Property(value => value.TenantId).HasColumnName("tenant_id");
        version.Property(value => value.Number).HasColumnName("number");
        version.Property(value => value.ConfigJson).HasColumnName("config").HasColumnType("jsonb");
        // Plan 2026-11-12 C-17: la sesión toma el proveedor de su versión, nunca del borrador.
        version.Property(value => value.Provider).HasColumnName("provider").HasMaxLength(AgentModelRef.ProviderMaxLength);
        version.Property(value => value.Model).HasColumnName("model").HasMaxLength(AgentModelRef.IdMaxLength);
        version.Property(value => value.PublishedAt).HasColumnName("published_at");
        version.Property(value => value.PublishedBy).HasColumnName("published_by");
        version.HasIndex(value => new { value.AgentId, value.Number }, VersionNumberIndex).IsUnique();
    }

    private static void ConfigureConnection(ModelBuilder modelBuilder)
    {
        var connection = modelBuilder.Entity<AgentConnection>();
        connection.ToTable("agent_connections", Schema);
        connection.HasKey(value => new { value.AgentId, value.ConnectionId });
        connection.Property(value => value.AgentId).HasColumnName("agent_id");
        connection.Property(value => value.TenantId).HasColumnName("tenant_id");
        connection.Property(value => value.ConnectionId).HasColumnName("connection_id");
        connection.HasIndex(value => new { value.TenantId, value.ConnectionId }, "IX_agent_connections_tenant_connection");
    }
}
