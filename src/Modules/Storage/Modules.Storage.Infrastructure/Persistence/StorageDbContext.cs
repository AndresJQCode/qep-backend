using Microsoft.EntityFrameworkCore;
using Modules.Storage.Domain;

namespace Modules.Storage.Infrastructure.Persistence;

public sealed class StorageDbContext(DbContextOptions<StorageDbContext> options)
    : DbContext(options)
{
    public DbSet<FileResource> FileResources => Set<FileResource>();

    internal DbSet<StorageOutboxMessage> Outbox => Set<StorageOutboxMessage>();

    internal DbSet<StorageInboxMessage> Inbox => Set<StorageInboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureFileResource(modelBuilder);
        ConfigureOutboxProjection(modelBuilder);
        ConfigureInbox(modelBuilder);
    }

    // Spec 2026-09-16, D9: el inbox de los consumidores de Storage. Tabla propia del esquema storage,
    // igual que identity.inbox_messages.
    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        var inbox = modelBuilder.Entity<StorageInboxMessage>();
        inbox.ToTable("inbox_messages", "storage");
        inbox.HasKey(value => new { value.Consumer, value.MessageId });
        inbox.Property(value => value.Consumer).HasColumnName("consumer").HasMaxLength(200);
        inbox.Property(value => value.MessageId).HasColumnName("message_id");
        inbox.Property(value => value.ProcessedAt).HasColumnName("processed_at");
        // Spec 2026-11-26: los nombres van a mano porque StorageInboxClaims los escribe en SQL crudo.
        inbox.Property(value => value.ClaimedUntil).HasColumnName("claimed_until");
        // Default 1 en la base: las filas que ya existen, y las que insertan los consumidores que no reclaman, quedan con un intento.
        inbox.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(1);
    }

    private static void ConfigureFileResource(ModelBuilder modelBuilder)
    {
        var file = modelBuilder.Entity<FileResource>();
        file.ToTable("file_resources", "storage");
        file.HasKey(value => value.Id);
        file.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new FileResourceId(value))
            .ValueGeneratedNever();
        file.Property(value => value.TenantId).HasColumnName("tenant_id");
        file.Property(value => value.OwnerId).HasColumnName("owner_id");
        file.Property(value => value.OwnerType)
            .HasColumnName("owner_type")
            .HasConversion<string>()
            .HasMaxLength(20);
        file.Property(value => value.Name).HasColumnName("name").HasMaxLength(260);
        file.Property(value => value.MimeType).HasColumnName("mime_type").HasMaxLength(150);
        file.Property(value => value.SizeBytes).HasColumnName("size_bytes");
        file.Property(value => value.StorageKey).HasColumnName("storage_key").HasMaxLength(512);
        file.Property(value => value.Checksum).HasColumnName("checksum").HasMaxLength(128);
        file.Property(value => value.Category).HasColumnName("category").HasMaxLength(80);
        file.Property(value => value.Tags).HasColumnName("tags").HasColumnType("text[]");
        file.Property(value => value.PublicStorageKey).HasColumnName("public_storage_key").HasMaxLength(512);
        file.Property(value => value.PublishedAt).HasColumnName("published_at");
        file.Ignore(value => value.IsPublic);
        file.Property(value => value.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20);
        file.Property(value => value.CreatedAt).HasColumnName("created_at");
        file.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        file.Property(value => value.DeletedAt).HasColumnName("deleted_at");
        file.HasIndex(value => new { value.TenantId, value.Status });
        file.HasIndex(value => new { value.TenantId, value.Status, value.CreatedAt, value.Id });
        file.HasIndex(value => new { value.TenantId, value.Category });
        file.HasIndex(value => value.Tags).HasMethod("gin");
        // CAT-09. Ninguno de los índices de arriba sirve para la galería de una entidad: filtran
        // por status, categoría o tags. El tenant va primero porque está en todas las consultas,
        // y el tipo antes del id por ser el de menor cardinalidad.
        file.HasIndex(value => new { value.TenantId, value.OwnerType, value.OwnerId })
            .HasDatabaseName("IX_file_resources_tenant_owner");
        // Soporte fase 3 (Block E, E-3): el tope de subidas anónimas del formulario público cuenta las de un tenant y un dueño en las
        // últimas 24 h (SupportAttachmentStorage.CountPendingAsync, SQL crudo en Bootstrapper). Sin la hora en el índice recorrería todos
        // los adjuntos que el tenant tuvo alguna vez, también los reclamados, que siguen Available para siempre.
        file.HasIndex(value => new { value.TenantId, value.OwnerType, value.CreatedAt })
            .HasDatabaseName("IX_file_resources_tenant_owner_created");

        var variant = modelBuilder.Entity<FileVariant>();
        variant.ToTable("file_variants", "storage");
        variant.HasKey(value => new { value.FileResourceId, value.Name });
        variant.Property(value => value.FileResourceId)
            .HasColumnName("file_resource_id")
            .HasConversion(id => id.Value, value => new FileResourceId(value));
        variant.Property(value => value.Name).HasColumnName("name").HasMaxLength(40);
        variant.Property(value => value.StorageKey).HasColumnName("storage_key").HasMaxLength(512);
        variant.Property(value => value.MimeType).HasColumnName("mime_type").HasMaxLength(150);
        variant.Property(value => value.Width).HasColumnName("width");
        variant.Property(value => value.Height).HasColumnName("height");
        variant.Property(value => value.SizeBytes).HasColumnName("size_bytes");
        file.HasMany(value => value.Variants)
            .WithOne()
            .HasForeignKey(value => value.FileResourceId)
            .OnDelete(DeleteBehavior.Cascade);
        file.Navigation(value => value.Variants)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureOutboxProjection(ModelBuilder modelBuilder)
    {
        // Proyección de escritura del Outbox de plataforma, propiedad de Tenancy.
        var outbox = modelBuilder.Entity<StorageOutboxMessage>();
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
