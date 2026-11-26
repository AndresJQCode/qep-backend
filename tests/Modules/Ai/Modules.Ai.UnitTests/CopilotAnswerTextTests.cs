using Modules.Ai.Application;

namespace Modules.Ai.UnitTests;

/// <summary>Revisión de la rama, hallazgo 7 (spec 2026-11-17 copiloto D-C10 punto 4): ningún enlace sale de la respuesta salvo por una cita.
/// El backend lo quita del texto que guarda, sin depender de cómo lo pinte el frontend: un enlace Markdown deja su texto, y una URL suelta
/// queda neutralizada.</summary>
public sealed class CopilotAnswerTextTests
{
    [Theory]
    [InlineData("Abre [aquí](https://evil.example/robar).", "Abre aquí.")]
    [InlineData("Mira ![logo](https://evil.example/x.png) ahí", "Mira logo ahí")]
    [InlineData("Ve a [la ficha](javascript:alert(1)) del cliente", "Ve a la ficha del cliente")]
    [InlineData("Ve a [la ficha][1] del cliente\n\n[1]: https://evil.example/robar", "Ve a la ficha del cliente")]
    [InlineData("Entra a <https://evil.example/robar> ya", "Entra a [enlace omitido] ya")]
    [InlineData("Entra a https://evil.example/robar?x=1 ya", "Entra a [enlace omitido] ya")]
    [InlineData("Entra a www.evil.example/robar ya", "Entra a [enlace omitido] ya")]
    public void LinksKeepTheirTextAndBareUrlsAreNeutralized(string answer, string expected) =>
        Assert.Equal(expected, CopilotAnswerText.Neutralize(answer));

    [Theory]
    [InlineData("Vendiste **$1.200.000** en noviembre, 12 pedidos.")]
    [InlineData("- Pedido PED-12: $300\n- Pedido PED-13: $500")]
    [InlineData("El cliente [VIP] compró 3 veces.")]
    public void TextWithoutLinksIsUntouched(string answer) => Assert.Equal(answer, CopilotAnswerText.Neutralize(answer));
}
