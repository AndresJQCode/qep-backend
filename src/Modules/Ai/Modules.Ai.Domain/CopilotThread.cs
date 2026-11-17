using System.Text.Json;
using System.Text.RegularExpressions;

namespace Modules.Ai.Domain;

public enum CopilotMessageRole
{
    User,
    Assistant,
}

public enum CopilotMessageStatus
{
    Done,
    Pending,
    Failed,
}

/// <summary>D-C6: una cita a un registro que salió de una herramienta del trabajo. El enlace lo arma el frontend con <see cref="Kind"/> e
/// <see cref="Id"/>, nunca con una URL del modelo.</summary>
/// <param name="Kind"><c>order</c>, <c>quotation</c>, <c>customer</c>, <c>product</c>, <c>loan</c>, <c>service</c> o <c>charge</c>.</param>
public sealed record CopilotCitation(string Kind, string Id, string Label);

/// <summary>
/// Spec 2026-11-17 (copiloto) D-C8 y §5.2: un hilo privado del miembro que lo creó (<see cref="UserId"/>), con sus mensajes. A diferencia
/// de las sesiones de agente (D-A28), el texto sí se guarda: es la única copia. Invariantes: un solo <see cref="CopilotMessageStatus.Pending"/>
/// por hilo (también lo cierra el índice único parcial), la pregunta hasta <see cref="QuestionMaxLength"/>, la respuesta hasta
/// <see cref="AnswerMaxLength"/> y el título hasta <see cref="TitleMaxLength"/>. <see cref="Version"/> es del <c>If-Match</c> de renombrar:
/// preguntar no la mueve, así la lista no se queda vieja por cada mensaje.
/// </summary>
public sealed partial class CopilotThread
{
    public const int QuestionMaxLength = 2000;

    public const int AnswerMaxLength = 8000;

    public const int TitleMaxLength = 120;

    /// <summary>§5.1: <c>progress varchar(40)</c>, el nombre de la herramienta en curso.</summary>
    public const int ProgressMaxLength = 40;

    public const int ErrorCodeMaxLength = 64;

    private readonly List<CopilotMessage> _messages = [];

    private CopilotThread()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>El <c>SubjectId</c> de quien lo creó: su único dueño (D-C8).</summary>
    public Guid UserId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastActivityAt { get; private set; }

    public long Version { get; private set; } = 1;

    /// <summary>En orden de <see cref="CopilotMessage.Seq"/>.</summary>
    public IReadOnlyList<CopilotMessage> Messages => _messages;

    public bool HasPending => _messages.Any(message => message.Status == CopilotMessageStatus.Pending);

    /// <summary>§8 <c>POST /threads</c>: el hilo nace con su primera pregunta (no hay hilos vacíos) y la respuesta pendiente del trabajo
    /// <paramref name="jobId"/>. El título es la pregunta en una línea, recortada.</summary>
    public static CopilotThread Create(Guid id, Guid tenantId, Guid userId, string question, Guid jobId, DateTimeOffset now)
    {
        EnsureQuestion(question);
        var thread = new CopilotThread
        {
            Id = id,
            TenantId = tenantId,
            UserId = userId,
            Title = Cut(Spaces().Replace(question.Trim(), " "), TitleMaxLength),
            CreatedAt = now,
            LastActivityAt = now,
        };
        thread.Ask(question, jobId, now);
        return thread;
    }

    /// <summary>Otra pregunta en el hilo. Quien llama ya respondió 409 <c>ai.copilot.busy</c> si había una pendiente; esto es el candado
    /// del dominio.</summary>
    public void AppendUserMessage(string question, Guid jobId, DateTimeOffset now)
    {
        EnsureQuestion(question);
        if (HasPending)
        {
            throw new InvalidOperationException("The thread already has a pending answer.");
        }

        Ask(question, jobId, now);
        LastActivityAt = now;
    }

    public void Rename(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var trimmed = title.Trim();
        if (trimmed.Length == 0 || trimmed.Length > TitleMaxLength)
        {
            throw new ArgumentException($"A title has 1 to {TitleMaxLength} characters.", nameof(title));
        }

        Title = trimmed;
        Version++;
    }

    /// <summary>D-C7: la herramienta en curso, mientras la respuesta del trabajo <paramref name="jobId"/> está pendiente.</summary>
    public void SetProgress(Guid jobId, string? progress) =>
        PendingOf(jobId).Progress = progress is null ? null : Cut(progress, ProgressMaxLength);

    /// <summary>§7 paso 3: la respuesta del trabajo, con las citas ya validadas contra los <c>ref</c> del trabajo.</summary>
    public void CompleteAssistant(Guid jobId, string text, IReadOnlyList<CopilotCitation> citations, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(citations);
        var answer = PendingOf(jobId);
        answer.Text = Cut(text, AnswerMaxLength);
        answer.SetCitations(citations);
        answer.Status = CopilotMessageStatus.Done;
        answer.Progress = null;
        answer.ErrorCode = null;
        LastActivityAt = now;
    }

    /// <summary>§7 paso 4: sin traspaso; la respuesta queda <c>Failed</c> con su código y la UI ofrece reintentar.</summary>
    public void FailAssistant(Guid jobId, string errorCode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        var answer = PendingOf(jobId);
        answer.Status = CopilotMessageStatus.Failed;
        answer.ErrorCode = Cut(errorCode, ErrorCodeMaxLength);
        answer.Progress = null;
        LastActivityAt = now;
    }

    /// <summary>§8: sólo la última respuesta, y sólo si falló.</summary>
    public bool CanRetry(Guid messageId) =>
        _messages.Count > 0
        && _messages[^1] is { Role: CopilotMessageRole.Assistant, Status: CopilotMessageStatus.Failed } last
        && last.Id == messageId;

    /// <summary>La respuesta fallida vuelve a <c>Pending</c> con un trabajo nuevo.</summary>
    public void RetryLast(Guid messageId, Guid jobId, DateTimeOffset now)
    {
        if (!CanRetry(messageId))
        {
            throw new InvalidOperationException("Only the last failed answer can be retried.");
        }

        var answer = _messages[^1];
        answer.Status = CopilotMessageStatus.Pending;
        answer.ErrorCode = null;
        answer.JobId = jobId;
        LastActivityAt = now;
    }

    private void Ask(string question, Guid jobId, DateTimeOffset now)
    {
        var seq = _messages.Count == 0 ? 1 : _messages[^1].Seq + 1;
        _messages.Add(CopilotMessage.Question(Id, TenantId, seq, question.Trim(), now));
        _messages.Add(CopilotMessage.PendingAnswer(Id, TenantId, seq + 1, jobId, now));
    }

    private CopilotMessage PendingOf(Guid jobId) =>
        _messages.SingleOrDefault(message => message.Status == CopilotMessageStatus.Pending && message.JobId == jobId)
        ?? throw new InvalidOperationException($"Job '{jobId}' has no pending answer in the thread.");

    private static void EnsureQuestion(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var length = question.Trim().Length;
        if (length == 0 || length > QuestionMaxLength)
        {
            throw new ArgumentException($"A question has 1 to {QuestionMaxLength} characters.", nameof(question));
        }
    }

    private static string Cut(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>Un mensaje de <c>ai.copilot_messages</c>. Lo cambia sólo su hilo.</summary>
public sealed class CopilotMessage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private List<CopilotCitation> _citations = [];

    private CopilotMessage()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ThreadId { get; private set; }

    public int Seq { get; private set; }

    public CopilotMessageRole Role { get; private set; }

    public string Text { get; internal set; } = string.Empty;

    public string CitationsJson
    {
        get => JsonSerializer.Serialize(_citations, Json);
        private set => _citations = JsonSerializer.Deserialize<List<CopilotCitation>>(value, Json) ?? [];
    }

    public IReadOnlyList<CopilotCitation> Citations => _citations;

    public CopilotMessageStatus Status { get; internal set; }

    public string? Progress { get; internal set; }

    public string? ErrorCode { get; internal set; }

    /// <summary>El trabajo de modelo que contesta una respuesta; <c>null</c> en una pregunta.</summary>
    public Guid? JobId { get; internal set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal static CopilotMessage Question(Guid threadId, Guid tenantId, int seq, string text, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ThreadId = threadId,
            Seq = seq,
            Role = CopilotMessageRole.User,
            Text = text,
            Status = CopilotMessageStatus.Done,
            CreatedAt = now,
        };

    internal static CopilotMessage PendingAnswer(Guid threadId, Guid tenantId, int seq, Guid jobId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ThreadId = threadId,
            Seq = seq,
            Role = CopilotMessageRole.Assistant,
            Status = CopilotMessageStatus.Pending,
            JobId = jobId,
            CreatedAt = now,
        };

    // Una lista nueva en cada cambio: EF compara la columna por valor.
    internal void SetCitations(IReadOnlyList<CopilotCitation> citations) => _citations = [.. citations];
}
