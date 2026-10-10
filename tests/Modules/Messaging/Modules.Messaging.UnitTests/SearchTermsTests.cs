using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §7.3, «Cómo se arma @q»: operadores quitados, tope de 8 tokens, prefijo sólo
/// con 3 o más caracteres, stop words (sin lexema) omitidas, sin lexemas → nada que consultar.</summary>
public sealed class SearchTermsTests
{
    [Fact]
    public void TokensDropOperatorsControlsAndEmptiesAndKeepEight()
    {
        Assert.Equal(["drog", "pedido"], SearchTerms.Tokenize("  drog & pedido  "));
        Assert.Equal(["a", "b"], SearchTerms.Tokenize("a:*|!(b)'\\<"));
        Assert.Empty(SearchTerms.Tokenize("&|!:*()'\\<"));
        Assert.Empty(SearchTerms.Tokenize("\u0000\u0001"));
        Assert.Equal(["t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8"], SearchTerms.Tokenize("t1 t2 t3 t4 t5 t6 t7 t8 t9 t10"));
    }

    [Fact]
    public void ComposeUsesPrefixOnlyFromThreeCharactersAndSkipsTokensWithoutLexeme()
    {
        var query = SearchTerms.Compose([("drogueria", "drogueri"), ("de", null), ("pedidos", "pedid"), ("ab", "ab")]);

        Assert.Equal("drogueri:* & pedid:* & ab", query);
        Assert.Null(SearchTerms.Compose([("de", null), ("la", null)]));
        Assert.Null(SearchTerms.Compose([]));
    }

    [Fact]
    public void ALexemeWithStrangeCharactersIsQuotedNeverInjected()
    {
        // Un lexema sólo tiene letras y dígitos; cualquier otra cosa se descarta por seguridad.
        Assert.Null(SearchTerms.Compose([("x", "a b"), ("y", "a'b")]));
    }
}
