using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-10 §6.2: el nombre del incompleto (perfil → username → teléfono → texto fijo) y su
/// país (prefijo del BSUID si es una región conocida → región del teléfono → nada).</summary>
public sealed class IncompleteCustomerProfileTests
{
    private sealed class RegionsOnlyNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) => phone;

        public bool IsKnownRegion(string regionCode) => regionCode is "CO" or "US";

        public string? RegionOf(string? e164) => e164?.StartsWith("+1", StringComparison.Ordinal) == true ? "US" : null;
    }

    [Theory]
    [InlineData(" Laura Pérez ", "laura.p", "+573001234567", "Laura Pérez")]
    [InlineData(null, "laura.p", "+573001234567", "laura.p")]
    [InlineData("  ", null, "+573001234567", "+573001234567")]
    [InlineData(null, null, null, IncompleteCustomerProfile.FallbackName)]
    public void TheNameFollowsTheOwnersOrder(string? profileName, string? username, string? phone, string expected) =>
        Assert.Equal(expected, IncompleteCustomerProfile.NameFor(new WhatsAppContact("CO.1", phone, profileName, username)));

    [Fact]
    public void ALongProfileNameIsCutToTheColumn() =>
        Assert.Equal(Customer.NameMaxLength, IncompleteCustomerProfile.NameFor(new WhatsAppContact("CO.1", null, new string('a', 300), null)).Length);

    [Theory]
    [InlineData("CO.1349120865530274", "+14155550100", "CO")]
    [InlineData("XX.1349120865530274", "+14155550100", "US")]
    [InlineData("XX.1349120865530274", null, null)]
    public void TheCountryComesFromTheBsuidThenThePhone(string userId, string? phone, string? expected) =>
        Assert.Equal(expected, IncompleteCustomerProfile.CountryFor(new WhatsAppContact(userId, phone, null, null), new RegionsOnlyNormalizer()));
}
