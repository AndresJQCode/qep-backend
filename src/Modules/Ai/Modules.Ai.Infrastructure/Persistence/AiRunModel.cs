using Microsoft.EntityFrameworkCore;
using Modules.Ai.Domain;

namespace Modules.Ai.Infrastructure.Persistence;

/// <summary>Una fila de <c>ai.tool_calls</c> (spec 2026-10-29 §4.2). Se inserta con SQL (<see cref="AiRunLedger"/>) y se lee con EF.</summary>
internal sealed class ToolCallRow
{
    public long Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid JobId { get; init; }

    public string CallId { get; init; } = string.Empty;

    public string Tool { get; init; } = string.Empty;

    public string Arguments { get; init; } = "{}";

    public string? Result { get; init; }

    public string Outcome { get; init; } = string.Empty;

    public string? ErrorCode { get; init; }

    public int DurationMs { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Una fila de <c>ai.usage_records</c> (§4.2 más C-4: caché y modelo servido).</summary>
internal sealed class UsageRecordRow
{
    public long Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid AgentId { get; init; }

    public Guid? AgentVersionId { get; init; }

    public Guid? ConversationId { get; init; }

    public Guid? SessionId { get; init; }

    public Guid? TestId { get; init; }

    public Guid JobId { get; init; }

    public string Provider { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string? ServedModel { get; init; }

    public int InputTokens { get; init; }

    public int OutputTokens { get; init; }

    public int? CacheCreationInputTokens { get; init; }

    public int? CacheReadInputTokens { get; init; }

    public string Outcome { get; init; } = string.Empty;

    public DateTimeOffset CalledAt { get; init; }
}

/// <summary>
/// Las tablas de la ejecución del modelo (spec 2026-10-29 §4.2): pruebas, sesiones y turnos, trabajos, herramientas y consumo. Los tokens son <c>int</c>, como el spec: una sola llamada no pasa de 2³¹ (el contexto es de 1M); las sumas se hacen en <c>bigint</c>.
/// </summary>
internal static class AiRunModel
{
    public const string ModelJobsOwnerCheck = "(session_id IS NULL) <> (test_id IS NULL)";

    public const string ModelJobsStatusCheck = "status IN ('Pending','Running','Done','Discarded','Failed')";

    /// <summary>D-A14: un trabajo pendiente por sesión.</summary>
    public const string SessionOpenJobIndex = "IX_model_jobs_session_open";

    public const string ToolCallIndex = "IX_tool_calls_job_call";

    /// <summary>§4.2: una sola sesión viva por conversación.</summary>
    public const string LiveSessionIndex = "IX_sessions_live";

    public const string SessionStatusCheck = "status IN ('Live','HandedOff','WindowClosed','Interrupted','Failed','Abandoned')";

    public const string TurnRoleCheck = "role IN ('Customer','Agent','Greeting')";

    public static void Configure(ModelBuilder modelBuilder)
    {
        ConfigureTest(modelBuilder);
        ConfigureSession(modelBuilder);
        ConfigureTurn(modelBuilder);
        ConfigureJob(modelBuilder);
        ConfigureToolCall(modelBuilder);
        ConfigureUsage(modelBuilder);
    }

    private static void ConfigureTest(ModelBuilder modelBuilder)
    {
        var test = modelBuilder.Entity<AgentTest>();
        test.ToTable("tests", AiDbContext.Schema);
        test.HasKey(value => value.Id);
        test.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        test.Property(value => value.TenantId).HasColumnName("tenant_id");
        test.Property(value => value.AgentId).HasColumnName("agent_id");
        test.Property(value => value.MemberId).HasColumnName("member_id");
        test.Property(value => value.CustomerId).HasColumnName("customer_id");
        test.Property(value => value.ConfigJson).HasColumnName("config").HasColumnType("jsonb");
        test.Property(value => value.TranscriptJson).HasColumnName("transcript").HasColumnType("jsonb");
        test.Property(value => value.PendingJobId).HasColumnName("pending_job_id");
        test.Property(value => value.HandedOff).HasColumnName("handed_off");
        test.Property(value => value.CreatedAt).HasColumnName("created_at");
        test.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        test.Property(value => value.ExpiresAt).HasColumnName("expires_at");
        test.Property(value => value.Version).HasColumnName("version").IsConcurrencyToken();
        test.Ignore(value => value.Config);
        test.Ignore(value => value.Transcript);
        test.HasIndex(value => new { value.TenantId, value.AgentId }, "IX_tests_tenant_agent");
    }

    private static void ConfigureJob(ModelBuilder modelBuilder)
    {
        var job = modelBuilder.Entity<ModelJob>();
        job.ToTable("model_jobs", AiDbContext.Schema, table =>
        {
            table.HasCheckConstraint("CK_model_jobs_owner", ModelJobsOwnerCheck);
            table.HasCheckConstraint("CK_model_jobs_status", ModelJobsStatusCheck);
        });
        job.HasKey(value => value.Id);
        job.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        job.Property(value => value.TenantId).HasColumnName("tenant_id");
        job.Property(value => value.SessionId).HasColumnName("session_id");
        job.Property(value => value.TestId).HasColumnName("test_id");
        job.Property(value => value.UpToSeq).HasColumnName("up_to_seq");
        job.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(12);
        job.Property(value => value.Attempts).HasColumnName("attempts");
        job.Property(value => value.NextAttemptAt).HasColumnName("next_attempt_at");
        job.Property(value => value.LeaseUntil).HasColumnName("lease_until");
        job.Property(value => value.Outcome).HasColumnName("outcome").HasMaxLength(32);
        job.Property(value => value.ErrorCode).HasColumnName("error_code").HasMaxLength(64);
        job.Property(value => value.CreatedAt).HasColumnName("created_at");
        job.Property(value => value.CompletedAt).HasColumnName("completed_at");
        // §6.3 paso 4: la respuesta guardada cuando el commit corto no tomó el candado.
        job.Property(value => value.ResultJson).HasColumnName("result").HasColumnType("jsonb");
        job.HasIndex(value => value.SessionId, SessionOpenJobIndex).IsUnique().HasFilter("status = 'Pending'");
        job.HasIndex(value => value.NextAttemptAt, "IX_model_jobs_due").HasFilter("status IN ('Pending','Running')");
        job.HasIndex(value => value.TestId, "IX_model_jobs_test").HasFilter("test_id IS NOT NULL");
    }

    private static void ConfigureSession(ModelBuilder modelBuilder)
    {
        var session = modelBuilder.Entity<AgentSession>();
        session.ToTable("sessions", AiDbContext.Schema, table => table.HasCheckConstraint("CK_sessions_status", SessionStatusCheck));
        session.HasKey(value => value.Id);
        session.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        session.Property(value => value.TenantId).HasColumnName("tenant_id");
        // Referencias blandas a otros esquemas (§4.2): sin FK.
        session.Property(value => value.ConversationId).HasColumnName("conversation_id");
        session.Property(value => value.ConnectionId).HasColumnName("connection_id");
        session.Property(value => value.AgentId).HasColumnName("agent_id");
        session.Property(value => value.AgentVersionId).HasColumnName("agent_version_id");
        session.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        session.Property(value => value.NextSeq).HasColumnName("next_seq").HasDefaultValue(1);
        session.Property(value => value.RepliesWithoutHuman).HasColumnName("replies_without_human").HasDefaultValue(0);
        session.Property(value => value.ConsecutiveFailures).HasColumnName("consecutive_failures").HasDefaultValue(0);
        session.Property(value => value.SupersededInRow).HasColumnName("superseded_in_row").HasDefaultValue(0);
        session.Property(value => value.LastInboundMessageId).HasColumnName("last_inbound_message_id");
        session.Property(value => value.StartedAt).HasColumnName("started_at");
        session.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        session.Property(value => value.EndedAt).HasColumnName("ended_at");
        session.Property(value => value.EndReason).HasColumnName("end_reason").HasMaxLength(AgentSession.ReasonMaxLength);
        session.Property(value => value.FailureCode).HasColumnName("failure_code").HasMaxLength(AgentSession.FailureCodeMaxLength);
        session.Ignore(value => value.IsLive);
        session.Ignore(value => value.CanSupersede);
        session.HasIndex(value => value.ConversationId, LiveSessionIndex).IsUnique().HasFilter("status = 'Live'");
        session.HasIndex(value => new { value.TenantId, value.AgentId, value.StartedAt }, "IX_sessions_tenant_agent_started")
            .IsDescending(false, false, true);
        // D-A19: el barrido de abandonadas recorre sólo las vivas.
        session.HasIndex(value => value.UpdatedAt, "IX_sessions_live_updated").HasFilter("status = 'Live'");
        session.HasIndex(value => new { value.ConversationId, value.StartedAt }, "IX_sessions_conversation_started").IsDescending(false, true);
    }

    private static void ConfigureTurn(ModelBuilder modelBuilder)
    {
        var turn = modelBuilder.Entity<AgentTurn>();
        turn.ToTable("turns", AiDbContext.Schema, table => table.HasCheckConstraint("CK_turns_role", TurnRoleCheck));
        turn.HasKey(value => value.Id);
        turn.Property(value => value.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        turn.Property(value => value.TenantId).HasColumnName("tenant_id");
        turn.Property(value => value.SessionId).HasColumnName("session_id");
        turn.Property(value => value.Seq).HasColumnName("seq");
        turn.Property(value => value.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(12);
        turn.Property(value => value.MessageId).HasColumnName("message_id");
        turn.Property(value => value.JobId).HasColumnName("job_id");
        turn.Property(value => value.SourcesJson).HasColumnName("sources").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
        turn.Property(value => value.Truncated).HasColumnName("truncated").HasDefaultValue(false).HasSentinel(false);
        turn.Property(value => value.CreatedAt).HasColumnName("created_at");
        turn.HasOne<AgentSession>().WithMany().HasForeignKey(value => value.SessionId).OnDelete(DeleteBehavior.Cascade);
        turn.HasIndex(value => new { value.SessionId, value.Seq }, "IX_turns_session_seq").IsUnique();
    }

    private static void ConfigureToolCall(ModelBuilder modelBuilder)
    {
        var call = modelBuilder.Entity<ToolCallRow>();
        call.ToTable("tool_calls", AiDbContext.Schema);
        call.HasKey(value => value.Id);
        call.Property(value => value.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        call.Property(value => value.TenantId).HasColumnName("tenant_id");
        call.Property(value => value.JobId).HasColumnName("job_id");
        call.Property(value => value.CallId).HasColumnName("call_id").HasMaxLength(64);
        call.Property(value => value.Tool).HasColumnName("tool").HasMaxLength(40);
        call.Property(value => value.Arguments).HasColumnName("arguments").HasColumnType("jsonb");
        call.Property(value => value.Result).HasColumnName("result").HasColumnType("jsonb");
        call.Property(value => value.Outcome).HasColumnName("outcome").HasMaxLength(24);
        call.Property(value => value.ErrorCode).HasColumnName("error_code").HasMaxLength(64);
        call.Property(value => value.DurationMs).HasColumnName("duration_ms");
        call.Property(value => value.CreatedAt).HasColumnName("created_at");
        call.HasIndex(value => new { value.JobId, value.CallId }, ToolCallIndex).IsUnique();
        call.HasIndex(value => value.CreatedAt, "IX_tool_calls_created");
    }

    private static void ConfigureUsage(ModelBuilder modelBuilder)
    {
        var usage = modelBuilder.Entity<UsageRecordRow>();
        usage.ToTable("usage_records", AiDbContext.Schema);
        usage.HasKey(value => value.Id);
        usage.Property(value => value.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        usage.Property(value => value.TenantId).HasColumnName("tenant_id");
        usage.Property(value => value.AgentId).HasColumnName("agent_id");
        usage.Property(value => value.AgentVersionId).HasColumnName("agent_version_id");
        usage.Property(value => value.ConversationId).HasColumnName("conversation_id");
        usage.Property(value => value.SessionId).HasColumnName("session_id");
        usage.Property(value => value.TestId).HasColumnName("test_id");
        usage.Property(value => value.JobId).HasColumnName("job_id");
        usage.Property(value => value.Provider).HasColumnName("provider").HasMaxLength(AgentModelRef.ProviderMaxLength);
        usage.Property(value => value.Model).HasColumnName("model").HasMaxLength(AgentModelRef.IdMaxLength);
        usage.Property(value => value.ServedModel).HasColumnName("served_model").HasMaxLength(AgentModelRef.IdMaxLength);
        usage.Property(value => value.InputTokens).HasColumnName("input_tokens");
        usage.Property(value => value.OutputTokens).HasColumnName("output_tokens");
        usage.Property(value => value.CacheCreationInputTokens).HasColumnName("cache_creation_input_tokens");
        usage.Property(value => value.CacheReadInputTokens).HasColumnName("cache_read_input_tokens");
        usage.Property(value => value.Outcome).HasColumnName("outcome").HasMaxLength(24);
        usage.Property(value => value.CalledAt).HasColumnName("called_at");
        usage.HasIndex(value => new { value.TenantId, value.CalledAt }, "IX_usage_records_tenant_called").IsDescending(false, true);
        usage.HasIndex(value => value.TestId, "IX_usage_records_test").HasFilter("test_id IS NOT NULL");
    }
}
