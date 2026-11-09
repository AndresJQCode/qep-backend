using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>
/// Datos de dominio compartidos por las pruebas que arman un <see cref="Customer"/> a mano: un
/// contacto colombiano, uno español y un helper de <c>Customer.Create</c>. Se importa con
/// <c>using static</c>.
/// </summary>
internal static class CustomerDomainFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    public static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");
    public static readonly Guid CityId = Guid.Parse("01900000-0000-7000-8000-000000000010");

    public static readonly ClientClassificationId ClassificationId =
        new(Guid.Parse("01900000-0000-7000-8000-000000000020"));

    public static CustomerIdentification Identification(IdentificationType type, string number = "900123456") =>
        new() { Type = type, Number = number };

    public static CustomerContactInfo Colombian() =>
        new()
        {
            Phone = "310 935 2187",
            Email = "compras@verde.co",
            Address = "Calle 10 # 45-12",
            Country = "CO",
            CityId = CityId
        };

    public static CustomerContactInfo Spanish() =>
        new()
        {
            Phone = "+34 910 000 000",
            Email = "compras@verde.es",
            Address = "Calle Gran Via 28",
            Country = "ES",
            CityName = "Madrid"
        };

    public static CustomerCommercialInfo Commercial() => new() { ClassificationId = ClassificationId };

    public static Customer Create(
        IdentificationType document,
        PersonType? personType,
        CustomerContactInfo? contact = null) =>
        Customer.Create(
            CustomerId.New(), TenantId, "CLI08000142", "Verde Esencial", null, null,
            Identification(document), personType, contact ?? Colombian(), Commercial(), Now);

    public sealed class FakeNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) => phone is null ? null : "+57" + phone;

        public bool IsKnownRegion(string regionCode) => regionCode == "CO";

        public string? RegionOf(string? e164) => null;
    }
}
