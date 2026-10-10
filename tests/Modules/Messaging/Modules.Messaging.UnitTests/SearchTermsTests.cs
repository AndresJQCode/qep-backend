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
    public void ComposeQuotesEveryLexemeUsesPrefixOnlyFromThreeCharactersAndSkipsTokensWithoutLexeme()
    {
        var query = SearchTerms.Compose([("drogueria", ["drogueri"]), ("de", []), ("pedidos", ["ped"]), ("ab", ["ab"])]);

        Assert.Equal("'drogueri':* & 'ped':* & 'ab'", query);
        Assert.Null(SearchTerms.Compose([("de", []), ("la", [])]));
        Assert.Null(SearchTerms.Compose([]));
    }

    [Fact]
    public void EveryLexemeOfATokenIsKeptSoNumbersEmailsAndHyphenatedWordsStaySearchable()
    {
        var query = SearchTerms.Compose([("covid-19", ["covid-19", "covid", "19"]), ("laura@acme.co", ["laura@acme.co"])]);

        Assert.Equal("'covid-19':* & 'covid':* & '19':* & 'laura@acme.co':*", query);
    }

    [Fact]
    public void ALexemeWithQuotesOrBackslashesIsEscapedInsideItsLiteral()
    {
        // Comillas dobladas y barra escapada: el lexema nunca sale de su literal de tsquery.
        Assert.Equal(@"'a''b' & 'c\\d':*", SearchTerms.Compose([("x", ["a'b"]), ("yyy", [@"c\d"])]));
    }

    [Fact]
    public void ASqlLikeInputLeavesOnlyPlainTokens()
    {
        Assert.Equal([";", "drop", "table"], SearchTerms.Tokenize("'); drop table"));
    }
}
