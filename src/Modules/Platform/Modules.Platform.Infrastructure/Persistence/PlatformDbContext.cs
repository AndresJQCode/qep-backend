using Microsoft.EntityFrameworkCore;
using Modules.Platform.Domain;

namespace Modules.Platform.Infrastructure.Persistence;

// Dueño del esquema "platform": hoy, el log de fallas de request. El esquema ya existía --lo usa
// la tabla outbox_messages, que crea BuildingBlocks y cada módulo mapea como proyección
// ExcludeFromMigrations-- pero nadie lo tenía a cargo. Este contexto sólo crea lo suyo.
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options)
    : DbContext(options)
{
    public DbSet<RequestFailure> RequestFailures => Set<RequestFailure>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var failure = modelBuilder.Entity<RequestFailure>();
        failure.ToTable("request_failures", "platform");
        failure.HasKey(value => value.Id);
        failure.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new RequestFailureId(value))
            .ValueGeneratedNever();
        // Nullable: hay endpoints que fallan antes de que haya tenant (login, alta de tenant).
        failure.Property(value => value.TenantId).HasColumnName("tenant_id");
        failure.Property(value => value.SubjectId).HasColumnName("subject_id");
        failure.Property(value => value.Method)
            .HasColumnName("method")
            .HasMaxLength(RequestFailure.MethodMaxLength);
        failure.Property(value => value.Path)
            .HasColumnName("path")
            .HasMaxLength(RequestFailure.PathMaxLength);
        failure.Property(value => value.Module)
            .HasColumnName("module")
            .HasMaxLength(RequestFailure.ModuleMaxLength);
        failure.Property(value => value.StatusCode).HasColumnName("status_code");
        failure.Property(value => value.ErrorCode)
            .HasColumnName("error_code")
            .HasMaxLength(RequestFailure.ErrorCodeMaxLength);
        failure.Property(value => value.Message)
            .HasColumnName("message")
            .HasMaxLength(RequestFailure.MessageMaxLength);
        failure.Property(value => value.Detail)
            .HasColumnName("detail")
            .HasMaxLength(RequestFailure.DetailMaxLength);
        failure.Property(value => value.TraceId)
            .HasColumnName("trace_id")
            .HasMaxLength(RequestFailure.TraceIdMaxLength);
        failure.Property(value => value.OccurredAt).HasColumnName("occurred_at");

        // El reporte lista por tenant en orden cronologico inverso, y el purgado borra por tenant
        // y fecha: el mismo indice sirve a los dos.
        failure.HasIndex(value => new { value.TenantId, value.OccurredAt })
            .HasDatabaseName("IX_request_failures_tenant_occurred_at");
        // Los dos filtros de la pantalla. Van con el tenant adelante porque ninguna consulta
        // cruza tenants.
        failure.HasIndex(value => new { value.TenantId, value.Module })
            .HasDatabaseName("IX_request_failures_tenant_module");
        failure.HasIndex(value => new { value.TenantId, value.ErrorCode })
            .HasDatabaseName("IX_request_failures_tenant_error_code");

        // Sin foreign key al tenant: el log tiene que poder registrar la falla de un request cuyo
        // tenant no existe --que es justamente uno de los errores que se quiere ver-- y sobrevivir
        // a que el tenant se borre.
    }
}
