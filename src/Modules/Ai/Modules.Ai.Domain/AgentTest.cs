using System.Text.Json;

namespace Modules.Ai.Domain;

/// <summary>
/// Spec 2026-10-29 §7: una prueba del agente en el playground, sobre una <b>foto del borrador</b> tomada al abrirla. La transcripción vive aquí
/// y nunca llega a Mensajería. Vence a las <see cref="Lifetime"/> sin mensajes, como <c>automations.simulations</c>; una vencida responde
/// como inexistente. Un mensaje a la vez: con un trabajo pendiente no se acepta otro (<c>ai.test.busy</c>).
/// </summary>
public sealed class AgentTest
{
    public const int TextMaxLength = 4096;

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private List<AgentTestEntry> _transcript = [];

    private AgentTest()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid AgentId { get; private set; }

    /// <summary>Quien abrió la prueba.</summary>
    public Guid MemberId { get; private set; }

    /// <summary>§7: el cliente que la prueba fija para <c>get_order_status</c>; sin él, la herramienta responde <c>customer_unknown</c>.</summary>
    public Guid? CustomerId { get; private set; }

    public string ConfigJson { get; private set; } = string.Empty;

    public string TranscriptJson
    {
        get => JsonSerializer.Serialize(_transcript, Json);
        private set => _transcript = JsonSerializer.Deserialize<List<AgentTestEntry>>(value, Json) ?? [];
    }

    public Guid? PendingJobId { get; private set; }

    public bool HandedOff { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public long Version { get; private set; } = 1;

    /// <summary>Spec 2026-11-17 §7: la prueba en modo bloque (<c>ai.tests.block</c>); <c>null</c> es una prueba de conexión.</summary>
    public string? BlockJson { get; private set; }

    public AgentConfig Config => AgentConfig.FromJson(ConfigJson);

    public AgentTestBlock? Block => BlockJson is null ? null : JsonSerializer.Deserialize<AgentTestBlock>(BlockJson, Json);

    public IReadOnlyList<AgentTestEntry> Transcript => _transcript;

    public static AgentTest Open(
        Guid id, Guid tenantId, Guid agentId, Guid memberId, Guid? customerId, AgentConfig draft, DateTimeOffset now, AgentTestBlock? block = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new AgentTest
        {
            Id = id,
            TenantId = tenantId,
            AgentId = agentId,
            MemberId = memberId,
            CustomerId = customerId,
            ConfigJson = draft.ToJson(),
            BlockJson = block is null ? null : JsonSerializer.Serialize(block, Json),
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now + Lifetime,
        };
    }

    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>El mensaje del cliente de prueba; deja la prueba esperando el trabajo <paramref name="jobId"/>. Quien llama ya verificó que
    /// no había otro pendiente.</summary>
    public void AddCustomerMessage(string text, Guid jobId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (PendingJobId is not null)
        {
            throw new InvalidOperationException("The test already has a pending message.");
        }

        Append(new AgentTestEntry(NextSeq(), AgentTestEntry.Customer, text.Trim(), [], null, null, now));
        PendingJobId = jobId;
        ExpiresAt = now + Lifetime;
        Touch(now);
    }

    public void RecordReply(Guid jobId, string text, IReadOnlyList<string> sourceIds, DateTimeOffset now)
    {
        EnsurePending(jobId);
        Append(new AgentTestEntry(NextSeq(), AgentTestEntry.Agent, text, sourceIds, null, null, now));
        PendingJobId = null;
        Touch(now);
    }

    /// <summary>§7: <c>handoff_to_human</c> sólo marca la transcripción. Un trabajo fallido también termina así, con su código: en un canal
    /// real sería un traspaso.</summary>
    public void RecordHandoff(Guid jobId, string? reason, string? code, DateTimeOffset now)
    {
        EnsurePending(jobId);
        Append(new AgentTestEntry(NextSeq(), AgentTestEntry.Handoff, null, [], reason, code, now));
        PendingJobId = null;
        HandedOff = true;
        Touch(now);
    }

    /// <summary>Spec 2026-11-17 §7: en modo bloque, lo que en un canal sería un traspaso o el fin del objetivo es un retorno al flujo con su
    /// salida, su motivo y el resumen de <c>finish_objective</c>. La prueba sigue abierta: el tester puede seguir escribiendo.</summary>
    public void RecordReturn(Guid jobId, string output, string reason, string? summary, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        EnsurePending(jobId);
        Append(new AgentTestEntry(NextSeq(), AgentTestEntry.Returned, string.IsNullOrWhiteSpace(summary) ? null : summary, [], reason, null, now, output));
        PendingJobId = null;
        Touch(now);
    }

    private void EnsurePending(Guid jobId)
    {
        if (PendingJobId != jobId)
        {
            throw new InvalidOperationException($"Job '{jobId}' is not the pending job of the test.");
        }
    }

    private int NextSeq() => _transcript.Count == 0 ? 1 : _transcript[^1].Seq + 1;

    // Una lista nueva en cada cambio: EF compara la columna por valor.
    private void Append(AgentTestEntry entry) => _transcript = [.. _transcript, entry];

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}

/// <summary>Spec 2026-11-17 §7 y D-B17: el paso del chatbot que simula una prueba del playground. <see cref="Context"/> es texto ya
/// resuelto: en el playground no hay variables del flujo.</summary>
public sealed record AgentTestBlock(string Objective, string? Context)
{
    public const int ObjectiveMaxLength = 2000;

    public const int ContextMaxLength = 2000;
}

/// <param name="Role"><see cref="Customer"/>, <see cref="Agent"/>, <see cref="Handoff"/> o <see cref="Returned"/>.</param>
/// <param name="SourceIds">Las fuentes válidas que citó la respuesta del agente.</param>
/// <param name="Reason">El motivo del traspaso que pidió el modelo, o el del retorno al flujo.</param>
/// <param name="Code">El código de un traspaso por falla (tope, proveedor, clave).</param>
/// <param name="Output">Spec 2026-11-17 §7: con <see cref="Returned"/>, la salida del bloque por la que la prueba habría vuelto al flujo; el
/// resumen de <c>finish_objective</c> va en <paramref name="Text"/>.</param>
public sealed record AgentTestEntry(
    int Seq, string Role, string? Text, IReadOnlyList<string> SourceIds, string? Reason, string? Code, DateTimeOffset At, string? Output = null)
{
    public const string Customer = "customer";
    public const string Agent = "agent";
    public const string Handoff = "handoff";

    /// <summary>Spec 2026-11-17 §7: la prueba en modo bloque volvió al flujo.</summary>
    public const string Returned = "returned";
}
