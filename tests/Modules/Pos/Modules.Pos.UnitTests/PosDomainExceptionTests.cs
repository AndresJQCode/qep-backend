using BuildingBlocks.Domain;
using Modules.Pos.Domain;

namespace Modules.Pos.UnitTests;

public sealed class PosDomainExceptionTests
{
    // ApiExceptionHandler responde 422 a todo DomainException con su Code: si PosDomainException
    // no heredara de ahí, cada regla del POS saldría como 500.
    [Fact]
    public void IsADomainExceptionThatCarriesItsCode()
    {
        DomainException error = new PosDomainException("pos.session.not_open", "No open session.");

        Assert.Equal("pos.session.not_open", error.Code);
    }
}
