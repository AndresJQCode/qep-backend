using System.Text.Json;
using Modules.Buyers.Domain;

namespace Modules.Buyers.UnitTests;

public sealed class BuyerProfileTests
{
    private static BuyerProfileData Data(string type, string country) =>
        new(new BuyerBillingData("Ana", type, "X1", "612345678", "ana@correo.es", "Calle Mayor 1", country, null, null, "Madrid"), null, true);

    [Fact]
    public void CreateRejectsATypeTheCountryDoesNotAllow() =>
        Assert.Equal(
            "store.buyer.document_type_not_allowed",
            Assert.Throws<BuyerDomainException>(
                () => BuyerProfile.Create(Guid.CreateVersion7(), Data("CC", "ES"), DateTimeOffset.UnixEpoch)).Code);

    private static BuyerProfileData ColombianData(string type, string number) =>
        new(new BuyerBillingData("Ana", type, number, "3001234567", "ana@correo.com", "Calle 1", "CO", null, null, "Bogotá"), null, true);

    [Theory]
    [InlineData("NIT")]
    [InlineData("CC")]
    [InlineData("CE")]
    public void CreateAndReplaceRejectLettersInANumericNumber(string type)
    {
        var create = Assert.Throws<BuyerDomainException>(
            () => BuyerProfile.Create(Guid.CreateVersion7(), ColombianData(type, "12345A"), DateTimeOffset.UnixEpoch));
        var profile = BuyerProfile.Create(Guid.CreateVersion7(), ColombianData(type, "123456"), DateTimeOffset.UnixEpoch);
        var replace = Assert.Throws<BuyerDomainException>(
            () => profile.Replace(ColombianData(type, "12345A"), DateTimeOffset.UnixEpoch.AddDays(1)));

        Assert.Equal("store.buyer.identification_number_not_numeric", create.Code);
        Assert.Equal("store.buyer.identification_number_not_numeric", replace.Code);
        Assert.Equal("123456", profile.Data.Billing.IdentificationNumber);
    }

    [Fact]
    public void APassportWithLettersIsAccepted() =>
        Assert.Equal(
            "AB123456",
            BuyerProfile.Create(Guid.CreateVersion7(), ColombianData("PASAPORTE", "AB123456"), DateTimeOffset.UnixEpoch)
                .Data.Billing.IdentificationNumber);

    [Fact]
    public void CreateAcceptsATypeTheCountryAllows()
    {
        var profile = BuyerProfile.Create(Guid.CreateVersion7(), Data("NIF", "ES"), DateTimeOffset.UnixEpoch);

        Assert.Equal("NIF", profile.Data.Billing.IdentificationType);
    }

    [Fact]
    public void ReplaceRejectsAndKeepsTheSavedProfile()
    {
        var profile = BuyerProfile.Create(Guid.CreateVersion7(), Data("NIF", "ES"), DateTimeOffset.UnixEpoch);

        var exception = Assert.Throws<BuyerDomainException>(
            () => profile.Replace(Data("CC", "ES"), DateTimeOffset.UnixEpoch.AddDays(1)));

        Assert.Equal("store.buyer.document_type_not_allowed", exception.Code);
        Assert.Equal("NIF", profile.Data.Billing.IdentificationType);
        Assert.Equal(DateTimeOffset.UnixEpoch, profile.UpdatedAt);
    }

    [Fact]
    public void AcceptsDocumentIgnoresCaseAndSpaces()
    {
        Assert.True(BuyerProfile.AcceptsDocument(Data(" nie ", " es ").Billing));
        Assert.False(BuyerProfile.AcceptsDocument(Data("CC", "ES").Billing));
    }

    [Fact]
    public void ALegacyStoredPairStillDeserializes() =>
        Assert.Equal(
            "CC",
            JsonSerializer.Deserialize<BuyerProfileData>(
                JsonSerializer.Serialize(Data("CC", "ES"), JsonSerializerOptions.Web), JsonSerializerOptions.Web)!
                .Billing.IdentificationType);
}
