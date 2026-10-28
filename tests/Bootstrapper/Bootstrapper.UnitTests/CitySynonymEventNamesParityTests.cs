using Modules.Geography.Application;
using Modules.Shopify.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-26 «Contrato de eventos»: Geography escribe los eventos de sinónimos en el outbox con
/// <see cref="CitySynonymEventNames"/> y Shopify se suscribe con <see cref="ShopifyCitySynonymEventNames"/>: son dos copias, porque
/// ningún módulo referencia al otro. Si un nombre cambia en uno solo, el consumidor nunca recibe el evento y ningún pedido se
/// corrige, sin error en ningún lado. Este proyecto ve los dos módulos. Los campos de los payloads los amarran
/// <c>GeographyEventPublisherTests</c> y <c>CitySynonymEventPayloadsTests</c> con el mismo literal.
/// </summary>
public sealed class CitySynonymEventNamesParityTests
{
    [Fact]
    public void TheResolvedEventNameIsTheOneShopifySubscribesTo() =>
        Assert.Equal(CitySynonymEventNames.Resolved, ShopifyCitySynonymEventNames.Resolved);

    [Fact]
    public void TheCorrectedEventNameIsTheOneShopifySubscribesTo() =>
        Assert.Equal(CitySynonymEventNames.Corrected, ShopifyCitySynonymEventNames.Corrected);

    // Los nombres del contrato, literales: un cambio en las dos copias a la vez también tiene que ser deliberado.
    [Fact]
    public void BothCopiesAreTheNamesInTheEventContract()
    {
        string[] contract = ["geography.city_synonym.resolved.v1", "geography.city_synonym.corrected.v1"];

        Assert.Equal(contract, new[] { CitySynonymEventNames.Resolved, CitySynonymEventNames.Corrected });
        Assert.Equal(contract, new[] { ShopifyCitySynonymEventNames.Resolved, ShopifyCitySynonymEventNames.Corrected });
    }
}
