using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Las reglas de campo del <c>config</c>, compartidas por guardar el borrador e importar un agente: las claves de los errores son
/// las del <c>config</c>, y quien lo envuelve les antepone su propiedad (<c>Config.</c>, <c>Export.Config.</c>).</summary>
public sealed class AgentConfigFieldsValidatorTests
{
    private static AgentConfig Valid() => AgentConfig.Default(new AgentModelRef("anthropic", "claude-sonnet-5-5"));

    [Fact]
    public void TheDefaultConfigIsValid() =>
        Assert.True(new AgentConfigFieldsValidator().Validate(Valid()).IsValid);

    [Fact]
    public void AnUnknownToneIsRejected()
    {
        var result = new AgentConfigFieldsValidator().Validate(Valid() with { Tone = "loud" });

        Assert.Equal(["Tone"], result.Errors.Select(error => error.PropertyName));
    }

    [Fact]
    public void AFaqAnswerOverTheLimitIsRejected()
    {
        var result = new AgentConfigFieldsValidator().Validate(
            Valid() with { Faq = [new AgentFaq("a", "¿Hacen envíos?", new string('x', AgentConfig.FaqAnswerMaxLength + 1))] });

        Assert.Equal(["Faq[0].Answer"], result.Errors.Select(error => error.PropertyName));
    }
}
