using System.Text.Json;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>
/// Plan 2026-11-17 A4 (F-4): una sola resolución de fuentes para la actividad real y el playground. Una fuente es una pregunta frecuente de la
/// configuración, una llamada a herramienta del mismo trabajo o una respuesta rápida; la respuesta rápida que ya no existe viaja sin atajo.
/// </summary>
public sealed class AgentSourceResolverTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 14, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, string> Faq =
        AgentSourceResolver.FaqOf(AgentConfig.Default(new AgentModelRef("anthropic", "claude-sonnet-5-5")) with
        {
            Faq = [new AgentFaq("envios", "¿Hacen envíos?", "Sí.")],
        });

    private static readonly IReadOnlyDictionary<string, string> Shortcuts =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["qr-1"] = "/envio" };

    private static readonly IReadOnlyList<AgentTestToolCallDto> Calls =
    [
        new("call_1", AgentToolNames.SearchCatalog, JsonDocument.Parse("""{"query":"jabón"}""").RootElement, null, "ok", null, 40, At),
    ];

    [Fact]
    public void AFaqResolvesToItsQuestion() =>
        Assert.Equal(new AgentActivitySourceDto("faq", "envios", "¿Hacen envíos?"), AgentSourceResolver.Resolve("envios", Faq, Shortcuts, Calls));

    [Fact]
    public void AToolCallResolvesToTheToolName() =>
        Assert.Equal(
            new AgentActivitySourceDto("tool", "call_1", AgentToolNames.SearchCatalog), AgentSourceResolver.Resolve("call_1", Faq, Shortcuts, Calls));

    [Fact]
    public void ALiveQuickReplyResolvesToItsShortcut() =>
        Assert.Equal(new AgentActivitySourceDto("quickReply", "qr-1", "/envio"), AgentSourceResolver.Resolve("qr-1", Faq, Shortcuts, Calls));

    [Fact]
    public void ADeletedQuickReplyHasNoLabel() =>
        Assert.Equal(new AgentActivitySourceDto("quickReply", "qr-borrada", null), AgentSourceResolver.Resolve("qr-borrada", Faq, Shortcuts, Calls));
}
