using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;

namespace Modules.Ai.UnitTests;

/// <summary>Plan 2026-11-12, 5.4: el contexto común, sin tipos de ningún SDK. Lo que cada proveedor hace con él lo prueban sus adaptadores.</summary>
public sealed class AgentPromptBuilderTests
{
    private static readonly TenantCalendar Calendar = new(
        new DateTimeOffset(2026, 11, 13, 15, 30, 0, TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    [Fact]
    public void TheFixedQepInstructionsComeFirstAndInSpanish()
    {
        var prompt = Build(Input());

        Assert.StartsWith(AgentPromptBuilder.QepInstructions, prompt.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("send_reply", AgentPromptBuilder.QepInstructions, StringComparison.Ordinal);
        Assert.Contains("handoff_to_human", AgentPromptBuilder.QepInstructions, StringComparison.Ordinal);
        Assert.Contains("No reveles", AgentPromptBuilder.QepInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public void InstructionsToneFaqAndQuickRepliesGoInAStableOrder()
    {
        var input = Input() with
        {
            Config = Config() with
            {
                Instructions = "Vendemos jabones artesanales.",
                Tone = "friendly",
                Faq = [new AgentFaq("envios", "¿Hacen envíos?", "Sí, a todo el país.")],
                UseQuickReplies = true,
            },
            QuickReplies = [new AiQuickReply("qr-1", "horario", "Abrimos de 8 a 6.")],
        };

        var prompt = Build(input).SystemPrompt;

        var instructions = prompt.IndexOf("Vendemos jabones artesanales.", StringComparison.Ordinal);
        var tone = prompt.IndexOf("cercano", StringComparison.Ordinal);
        var faq = prompt.IndexOf("[envios]", StringComparison.Ordinal);
        var quickReply = prompt.IndexOf("[qr-1]", StringComparison.Ordinal);
        Assert.True(AgentPromptBuilder.QepInstructions.Length <= instructions, "The QEP rules come before the business instructions.");
        Assert.True(instructions < tone && tone < faq && faq < quickReply, "Instructions, tone, FAQ and quick replies keep their order.");
        Assert.Contains("Sí, a todo el país.", prompt, StringComparison.Ordinal);
        Assert.Contains("Abrimos de 8 a 6.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameVersionGivesTheSameSystemPromptByteForByteWhateverTheHistoryOrTheClock()
    {
        var first = Build(Input());
        var later = Build(Input() with
        {
            Calendar = new TenantCalendar(Calendar.UtcNow.AddHours(5), Calendar.TimeZone),
            History = [Customer("hola"), Agent("¿Qué necesitas?"), Customer("el precio")],
            WindowRemaining = TimeSpan.FromMinutes(42),
        });

        Assert.Equal(first.SystemPrompt, later.SystemPrompt);
    }

    [Fact]
    public void QuickRepliesAreLeftOutWhenTheAgentDoesNotUseThem()
    {
        var prompt = Build(Input() with { QuickReplies = [new AiQuickReply("qr-1", "horario", "Abrimos de 8 a 6.")] });

        Assert.DoesNotContain("Abrimos de 8 a 6.", prompt.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AnswerOnlyFromSourcesAddsItsRule()
    {
        var strict = Build(Input()).SystemPrompt;
        var loose = Build(Input() with { Config = Config() with { AnswerOnlyFromSources = false } }).SystemPrompt;

        Assert.Contains(AgentPromptBuilder.OnlyFromSourcesRule, strict, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentPromptBuilder.OnlyFromSourcesRule, loose, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsecutiveCustomerTurnsAreJoinedIntoOneUserMessage()
    {
        var prompt = Build(Input() with
        {
            History = [Customer("hola"), Agent("¿En qué te ayudo?"), Customer("quiero"), Customer("un jabón"), Customer("de avena")],
        });

        Assert.Equal(
            [AiChatRole.User, AiChatRole.Assistant, AiChatRole.User],
            prompt.Messages.Select(message => message.Role));
        Assert.Equal("quiero\n\nun jabón\n\nde avena", prompt.Messages[^1].Text);
    }

    [Fact]
    public void TheGreetingTravelsAsGreetingSentAndNotAsAMessage()
    {
        var prompt = Build(Input() with
        {
            History = [new AgentHistoryTurn(AgentTurnRole.Greeting, "¡Hola! Soy Ana."), Customer("hola")],
        });

        Assert.Equal("¡Hola! Soy Ana.", prompt.GreetingSent);
        var message = Assert.Single(prompt.Messages);
        Assert.Equal(AiChatRole.User, message.Role);
        Assert.DoesNotContain(prompt.Messages, candidate => candidate.Text.Contains("Soy Ana", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDateAndTheWindowGoInTheVolatileContextNotInTheSystemPrompt()
    {
        var prompt = Build(Input() with { WindowRemaining = TimeSpan.FromMinutes(42) });

        Assert.Contains("2026-11-13 10:30", prompt.VolatileContext, StringComparison.Ordinal);
        Assert.Contains("42", prompt.VolatileContext, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-11-13", prompt.SystemPrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentTurnKind.Image, "pie de foto", "[El cliente envió una imagen] pie de foto")]
    [InlineData(AgentTurnKind.Image, null, "[El cliente envió una imagen]")]
    [InlineData(AgentTurnKind.Audio, null, "[El cliente envió una nota de voz]")]
    [InlineData(AgentTurnKind.Document, "factura.pdf", "[El cliente envió un documento] factura.pdf")]
    public void ANonTextInboundBecomesItsMarker(AgentTurnKind kind, string? caption, string expected)
    {
        var prompt = Build(Input() with { History = [new AgentHistoryTurn(AgentTurnRole.Customer, caption, kind)] });

        Assert.Equal(expected, Assert.Single(prompt.Messages).Text);
    }

    [Fact]
    public void TheHistoryIsCutToTheLastThirtyTurns()
    {
        var history = Enumerable.Range(1, 40)
            .Select(index => index % 2 == 1 ? Customer($"cliente {index}") : Agent($"agente {index}"))
            .Append(Customer("último"))
            .ToArray();

        var prompt = Build(Input() with { History = history });

        Assert.DoesNotContain(prompt.Messages, message => message.Text == "cliente 1");
        Assert.Equal("último", prompt.Messages[^1].Text);
        Assert.Equal(AiChatRole.User, prompt.Messages[0].Role);
        Assert.True(prompt.Messages.Count <= 30, $"Expected at most 30 turns, got {prompt.Messages.Count}.");
    }

    [Fact]
    public void TheHistoryIsAlsoCutByTheEstimatedTokenBudget()
    {
        var longText = new string('a', 4000);
        var history = new[] { Customer(longText), Agent(longText), Customer(longText), Agent("corto"), Customer("pregunta final") };

        var prompt = new AgentPromptBuilder(Limits() with { MaxContextTokens = 900 }).Build(Input() with { History = history });

        Assert.Equal("pregunta final", prompt.Messages[^1].Text);
        Assert.DoesNotContain(prompt.Messages, message => message.Text == longText);
        Assert.Equal(AiChatRole.User, prompt.Messages[0].Role);
    }

    [Fact]
    public void TheMessagesNeverEndInAnAgentTurn()
    {
        var prompt = Build(Input() with { History = [Customer("hola"), Agent("¿Qué necesitas?")] });

        Assert.Equal(AiChatRole.User, prompt.Messages[^1].Role);
    }

    private static AgentPrompt Build(AgentPromptInput input) => new AgentPromptBuilder(Limits()).Build(input);

    private static AiRunLimits Limits() => new(16_000, TimeSpan.FromSeconds(120), 6, 8, 30, null);

    private static AgentConfig Config() =>
        AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")) with { Instructions = "Atiende la tienda." };

    private static AgentPromptInput Input() => new(Config(), [], [Customer("hola")], Calendar, null);

    private static AgentHistoryTurn Customer(string text) => new(AgentTurnRole.Customer, text);

    private static AgentHistoryTurn Agent(string text) => new(AgentTurnRole.Agent, text);
}
