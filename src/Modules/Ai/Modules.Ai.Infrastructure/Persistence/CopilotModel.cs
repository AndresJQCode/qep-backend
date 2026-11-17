using Microsoft.EntityFrameworkCore;
using Modules.Ai.Domain;

namespace Modules.Ai.Infrastructure.Persistence;

/// <summary>Spec 2026-11-17 (copiloto) §5.1: configuración, hilos y mensajes del copiloto. Sin FKs a otros esquemas: el dueño del hilo es una
/// referencia blanda al usuario.</summary>
/// <summary>Revisión de la rama, hallazgo 4: una pregunta de un miembro, para el tope por hora y por día.</summary>
internal sealed class CopilotQuestionRow
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid UserId { get; init; }

    public DateTimeOffset AskedAt { get; init; }
}

internal static class CopilotModel
{
    public const string SettingsEnabledCheck = "NOT enabled OR (provider IS NOT NULL AND model IS NOT NULL)";

    public const string MessageRoleCheck = "role IN ('User','Assistant')";

    public const string MessageStatusCheck = "status IN ('Done','Pending','Failed')";

    /// <summary>D-C11: un mensaje pendiente por hilo. <c>AiUnitOfWork</c> traduce el choque a 409 <c>ai.copilot.busy</c>.</summary>
    public const string PendingMessageIndex = "IX_copilot_messages_pending";

    /// <summary>Dos preguntas simultáneas proponen el mismo <c>seq</c>; la segunda choca acá y también es 409 <c>ai.copilot.busy</c>.</summary>
    public const string MessageSeqIndex = "IX_copilot_messages_thread_seq";

    /// <summary>Dos primeros guardados de la configuración con <c>If-Match: "0"</c>: el segundo choca con la clave y es 412.</summary>
    public const string SettingsKey = "PK_copilot_settings";

    public static void Configure(ModelBuilder modelBuilder)
    {
        ConfigureSettings(modelBuilder);
        ConfigureThread(modelBuilder);
        ConfigureMessage(modelBuilder);
        ConfigureQuestion(modelBuilder);
    }

    /// <summary>Revisión de la rama, hallazgo 4: el registro de preguntas del tope por miembro (D-C11). Sin FK al hilo a propósito: borrar
    /// el hilo no devuelve el cupo. Sin texto (D-C13).</summary>
    private static void ConfigureQuestion(ModelBuilder modelBuilder)
    {
        var question = modelBuilder.Entity<CopilotQuestionRow>();
        question.ToTable("copilot_questions", AiDbContext.Schema);
        question.HasKey(value => value.Id);
        question.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        question.Property(value => value.TenantId).HasColumnName("tenant_id");
        question.Property(value => value.UserId).HasColumnName("user_id");
        question.Property(value => value.AskedAt).HasColumnName("asked_at");
        question.HasIndex(value => new { value.TenantId, value.UserId, value.AskedAt }, "IX_copilot_questions_member");
        // El barrido borra por fecha, de todos los tenants.
        question.HasIndex(value => value.AskedAt, "IX_copilot_questions_asked_at");
    }

    private static void ConfigureSettings(ModelBuilder modelBuilder)
    {
        var settings = modelBuilder.Entity<CopilotSettings>();
        settings.ToTable("copilot_settings", AiDbContext.Schema, table => table.HasCheckConstraint("CK_copilot_settings_enabled", SettingsEnabledCheck));
        settings.HasKey(value => value.TenantId).HasName(SettingsKey);
        settings.Property(value => value.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
        settings.Property(value => value.Enabled).HasColumnName("enabled");
        settings.Property(value => value.Provider).HasColumnName("provider").HasMaxLength(AgentModelRef.ProviderMaxLength);
        settings.Property(value => value.Model).HasColumnName("model").HasMaxLength(AgentModelRef.IdMaxLength);
        settings.Property(value => value.Instructions).HasColumnName("instructions");
        settings.Property(value => value.MonthlyTokenBudget).HasColumnName("monthly_token_budget");
        settings.Property(value => value.RetentionDays).HasColumnName("retention_days");
        settings.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        settings.Property(value => value.UpdatedBy).HasColumnName("updated_by");
        settings.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        settings.Ignore(value => value.IsConfigured);
    }

    private static void ConfigureThread(ModelBuilder modelBuilder)
    {
        var thread = modelBuilder.Entity<CopilotThread>();
        thread.ToTable("copilot_threads", AiDbContext.Schema);
        thread.HasKey(value => value.Id);
        thread.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        thread.Property(value => value.TenantId).HasColumnName("tenant_id");
        thread.Property(value => value.UserId).HasColumnName("user_id");
        thread.Property(value => value.Title).HasColumnName("title").HasMaxLength(CopilotThread.TitleMaxLength);
        thread.Property(value => value.CreatedAt).HasColumnName("created_at");
        thread.Property(value => value.LastActivityAt).HasColumnName("last_activity_at");
        thread.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        thread.Ignore(value => value.HasPending);
        thread.HasMany(value => value.Messages).WithOne().HasForeignKey(message => message.ThreadId).OnDelete(DeleteBehavior.Cascade);
        thread.Navigation(value => value.Messages).UsePropertyAccessMode(PropertyAccessMode.Field);
        // §5.1: la lista del miembro, de la actividad más reciente a la más vieja.
        thread.HasIndex(value => new { value.TenantId, value.UserId, value.LastActivityAt }, "IX_copilot_threads_owner")
            .IsDescending(false, false, true);
        // D-C8: el barrido de retención recorre por actividad.
        thread.HasIndex(value => value.LastActivityAt, "IX_copilot_threads_activity");
    }

    private static void ConfigureMessage(ModelBuilder modelBuilder)
    {
        var message = modelBuilder.Entity<CopilotMessage>();
        message.ToTable("copilot_messages", AiDbContext.Schema, table =>
        {
            table.HasCheckConstraint("CK_copilot_messages_role", MessageRoleCheck);
            table.HasCheckConstraint("CK_copilot_messages_status", MessageStatusCheck);
        });
        message.HasKey(value => value.Id);
        message.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        message.Property(value => value.TenantId).HasColumnName("tenant_id");
        message.Property(value => value.ThreadId).HasColumnName("thread_id");
        message.Property(value => value.Seq).HasColumnName("seq");
        message.Property(value => value.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(12);
        message.Property(value => value.Text).HasColumnName("text").HasDefaultValue(string.Empty);
        message.Property(value => value.CitationsJson).HasColumnName("citations").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
        message.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(12);
        message.Property(value => value.Progress).HasColumnName("progress").HasMaxLength(CopilotThread.ProgressMaxLength);
        message.Property(value => value.ErrorCode).HasColumnName("error_code").HasMaxLength(CopilotThread.ErrorCodeMaxLength);
        message.Property(value => value.JobId).HasColumnName("job_id");
        message.Property(value => value.CreatedAt).HasColumnName("created_at");
        message.Ignore(value => value.Citations);
        message.HasIndex(value => new { value.ThreadId, value.Seq }, MessageSeqIndex).IsUnique();
        message.HasIndex(value => value.ThreadId, PendingMessageIndex).IsUnique().HasFilter("status = 'Pending'");
    }
}
