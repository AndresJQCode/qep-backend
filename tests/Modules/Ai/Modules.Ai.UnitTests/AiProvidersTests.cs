using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>Plan 2026-11-12, C-16: el proveedor lo elige cada agente entre una lista cerrada. El texto es el mismo que la clave del
/// proveedor en Integrations y la sección en <c>Ai:Providers</c>.</summary>
public sealed class AiProvidersTests
{
    [Fact]
    public void AllKeepsTheContractOrder() =>
        Assert.Equal(["anthropic", "openai"], AiProviders.All.Select(provider => provider.Value));

    [Fact]
    public void ParseReturnsTheSameInstanceForEveryProvider()
    {
        foreach (var provider in AiProviders.All)
        {
            Assert.Same(provider, AiProvider.Parse(provider.Value));
        }
    }

    // Igual que el CHECK que llega con las tablas: «Anthropic» no es «anthropic».
    [Theory]
    [InlineData("Anthropic")]
    [InlineData("gemini")]
    [InlineData("")]
    public void AnUnknownTextIsNotAProvider(string value)
    {
        Assert.False(AiProvider.TryParse(value, out var provider));
        Assert.Null(provider);
        Assert.Throws<ArgumentException>(() => AiProvider.Parse(value));
    }
}
