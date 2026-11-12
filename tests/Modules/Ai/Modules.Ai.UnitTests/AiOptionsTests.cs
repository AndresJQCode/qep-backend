using Modules.Ai.Infrastructure.Options;

namespace Modules.Ai.UnitTests;

/// <summary>Plan 2026-11-12, C-3: la lista curada por proveedor se valida al arrancar. Un error acá tumba el pod a propósito: un agente
/// con un modelo que no existe en la lista no se puede publicar, y la pantalla no tendría qué ofrecer.</summary>
public sealed class AiOptionsTests
{
    [Fact]
    public void TheShapeOfAppsettingsIsValid() => Assert.Empty(Valid().Validate());

    [Fact]
    public void TheDefaultProviderMustBeKnown()
    {
        var options = Valid();
        options.DefaultProvider = "gemini";

        Assert.Contains(options.Validate(), failure => failure.Contains("Ai:DefaultProvider", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownProviderSectionFails()
    {
        var options = Valid();
        options.Providers["gemini"] = new AiProviderOptions { Models = [Model("gemini-x", isDefault: true)] };

        Assert.Contains(options.Validate(), failure => failure.Contains("Ai:Providers:gemini", StringComparison.Ordinal));
    }

    // C-16: la pantalla ofrece los dos proveedores siempre; uno sin sección no tendría modelos que mostrar.
    [Fact]
    public void EveryKnownProviderNeedsItsSection()
    {
        var options = Valid();
        options.Providers.Remove("openai");

        Assert.Contains(options.Validate(), failure => failure.Contains("Ai:Providers:openai", StringComparison.Ordinal));
    }

    [Fact]
    public void AProviderNeedsAtLeastOneModel()
    {
        var options = Valid();
        options.Providers["openai"].Models.Clear();

        Assert.Contains(options.Validate(), failure => failure.Contains("Ai:Providers:openai:Models", StringComparison.Ordinal));
    }

    [Fact]
    public void AProviderNeedsExactlyOneDefault()
    {
        var none = Valid();
        none.Providers["anthropic"].Models[0].IsDefault = false;
        var two = Valid();
        two.Providers["anthropic"].Models[1].IsDefault = true;

        Assert.Contains(none.Validate(), failure => failure.Contains("exactly one", StringComparison.Ordinal));
        Assert.Contains(two.Validate(), failure => failure.Contains("exactly one", StringComparison.Ordinal));
    }

    [Fact]
    public void ModelIdsAreUniqueWithinAProvider()
    {
        var options = Valid();
        options.Providers["anthropic"].Models[1].Id = options.Providers["anthropic"].Models[0].Id;

        Assert.Contains(options.Validate(), failure => failure.Contains("repeated", StringComparison.Ordinal));
    }

    // varchar(64) en ai.agent_versions.model (spec §4.2).
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AModelNeedsAnId(string id)
    {
        var options = Valid();
        options.Providers["anthropic"].Models[1].Id = id;

        Assert.Contains(options.Validate(), failure => failure.Contains(":Id", StringComparison.Ordinal));
    }

    [Fact]
    public void AModelIdFitsTheColumn()
    {
        var options = Valid();
        options.Providers["anthropic"].Models[1].Id = new string('x', 65);

        Assert.Contains(options.Validate(), failure => failure.Contains("64", StringComparison.Ordinal));
    }

    [Fact]
    public void AModelNeedsALabel()
    {
        var options = Valid();
        options.Providers["anthropic"].Models[1].Label = " ";

        Assert.Contains(options.Validate(), failure => failure.Contains(":Label", StringComparison.Ordinal));
    }

    // C-7: con Claude el esfuerzo va siempre explícito; Opus 5.5 rechaza apagar el pensamiento y el esfuerzo es la única palanca.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("extreme")]
    [InlineData("High")]
    public void AnAnthropicModelNeedsAKnownEffort(string? effort)
    {
        var options = Valid();
        options.Providers["anthropic"].Models[0].Effort = effort;

        Assert.Contains(options.Validate(), failure => failure.Contains(":Effort", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void EveryAnthropicEffortLevelIsAccepted(string effort)
    {
        var options = Valid();
        options.Providers["anthropic"].Models[0].Effort = effort;

        Assert.Empty(options.Validate());
    }

    // Lo que se ofrece por defecto a un borrador nuevo tiene que poder publicarse.
    [Fact]
    public void TheDefaultModelOfTheDefaultProviderMustBeAvailable()
    {
        var options = Valid();
        options.Providers["anthropic"].Models[0].Available = false;

        Assert.Contains(options.Validate(), failure => failure.Contains("available", StringComparison.Ordinal));
    }

    // Plan 2026-11-12, Tarea 6: hasta que exista el adaptador de OpenAI sus modelos están en la lista sin poder elegirse.
    [Fact]
    public void AnotherProviderMayHaveEveryModelUnavailable() => Assert.Empty(Valid().Validate());

    internal static AiOptions Valid() => new()
    {
        DefaultProvider = "anthropic",
        Providers = new(StringComparer.Ordinal)
        {
            ["anthropic"] = new AiProviderOptions
            {
                Models =
                [
                    Model("claude-opus-5-5", isDefault: true, effort: "medium"),
                    Model("claude-sonnet-5-5", effort: "medium"),
                    Model("claude-haiku-5-5", effort: "medium"),
                ],
            },
            ["openai"] = new AiProviderOptions
            {
                Models =
                [
                    Model("gpt-6-luna", isDefault: true, available: false),
                    Model("gpt-6.1-terra", available: false),
                ],
            },
        },
    };

    private static AiModelOptions Model(string id, bool isDefault = false, bool available = true, string? effort = null) => new()
    {
        Id = id,
        Label = id,
        Description = $"Descripción de {id}",
        IsDefault = isDefault,
        Available = available,
        Effort = effort,
    };
}
