using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>Spec 2026-11-17 (copiloto) D-C11: los topes por miembro, de <c>Ai:Copilot</c> (los números van en opciones, no en código). El
/// freno de abuso con BYOK: un miembro, o un script con su cookie, no vacía la cuenta del tenant.</summary>
public sealed record CopilotLimits(int MessagesPerHour, int MessagesPerDay)
{
    public static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    public static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>Cuánto falta para que el miembro pueda preguntar otra vez, o <c>null</c> si puede ya. Ventanas móviles: una pregunta deja de
    /// contar una hora (o un día) después de hecha, así que la espera es hasta que salga la más vieja de la ventana llena.</summary>
    /// <param name="questions">Los instantes de las preguntas del miembro en las últimas 24 horas, en cualquier orden.</param>
    public TimeSpan? RetryAfter(IReadOnlyList<DateTimeOffset> questions, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(questions);
        var waits = new[] { WaitFor(questions, now, Hour, MessagesPerHour), WaitFor(questions, now, Day, MessagesPerDay) }
            .OfType<TimeSpan>()
            .ToArray();
        return waits.Length == 0 ? null : waits.Max();
    }

    private static TimeSpan? WaitFor(IReadOnlyList<DateTimeOffset> questions, DateTimeOffset now, TimeSpan window, int limit)
    {
        var inWindow = questions.Where(at => at > now - window).OrderByDescending(at => at).ToArray();
        if (inWindow.Length < limit)
        {
            return null;
        }

        // La que tiene que salir de la ventana para que quepa una más: la número «limit» contando desde la más reciente.
        var wait = inWindow[limit - 1] + window - now;
        return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
    }
}

/// <summary>Adenda D-7 del plan fase 2: todo 429 del copiloto lleva <c>retryAfterSeconds</c> en el ProblemDetails, además del header
/// <c>Retry-After</c> (la CORS de la API no expone headers; el frontend lee la extensión). <c>ai.copilot.rate_limited</c> o
/// <c>ai.copilot.budget_exhausted</c>.</summary>
public sealed class CopilotRateLimitedException(string code, string message, TimeSpan retryAfter)
    : RequestRateLimitedException(code, message, retryAfter),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["retryAfterSeconds"] = (int)Math.Ceiling(Math.Max(1, retryAfter.TotalSeconds)),
    };

    public static CopilotRateLimitedException MemberLimit(TimeSpan retryAfter) =>
        new(AiErrorCodes.CopilotRateLimited, "Hiciste muchas preguntas seguidas. Intenta de nuevo más tarde.", retryAfter);

    public static CopilotRateLimitedException Budget(TimeSpan retryAfter) =>
        new(AiErrorCodes.CopilotBudgetExhausted, "Tu organización llegó al tope de uso del copiloto de este mes.", retryAfter);
}
