using BuildingBlocks.Domain.Identification;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Customers → ServiceContracts. La importación de contratos es colombiana: ofrece los cuatro códigos de CO aunque
/// Customers ya conozca siete.
/// </summary>
public sealed class ServiceContractsIdentificationTypesTests
{
    [Fact]
    public void TheImportOffersOnlyTheColombianCodes() =>
        Assert.Equal(DocumentTypesByCountry.AllowedFor("CO"), new ServiceContractsIdentificationTypes().SupportedWireValues);
}
