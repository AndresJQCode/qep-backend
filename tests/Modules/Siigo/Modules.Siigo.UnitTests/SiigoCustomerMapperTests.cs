using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using static Modules.Siigo.UnitTests.InvoiceTestData;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-18 §6.2: la parte facturada del pedido como cliente de Siigo.</summary>
public sealed class SiigoCustomerMapperTests
{
    private static SiigoBilledPartyInput Person(string name = "Ana María Gómez Pérez") =>
        new(false, SiigoPersonKind.Natural, SiigoIdentificationKind.Cc, "1020304050", name, "Carrera 7 # 1-2", "05001", "05", "ana@test.co",
            new SiigoPhone("57", "3109998877"));

    [Theory]
    [InlineData("900.123.456-7", "900123456")]
    [InlineData("900123456-7", "900123456")]
    [InlineData("900 123 456", "900123456")]
    [InlineData("900.123.456", "900123456")]
    public void ANitIsSentWithoutCheckDigitAndOnlyDigits(string raw, string expected)
    {
        var request = SiigoCustomerMapper.Map(Company() with { Identification = raw });

        Assert.Equal(expected, request!.Identification);
        Assert.Equal("31", request.IdTypeCode);
    }

    // Spec 2026-11-25: el documento sale sin separadores y el NIT sin su dígito de verificación; lo guardado no cambia.
    [Theory]
    [InlineData(SiigoIdentificationKind.Cc, "1.234.567.890", "1234567890")]
    [InlineData(SiigoIdentificationKind.Cc, " 12 345 678 ", "12345678")]
    [InlineData(SiigoIdentificationKind.Cc, "abc", null)]
    [InlineData(SiigoIdentificationKind.Nit, "900.123.456-8", "900123456")]
    [InlineData(SiigoIdentificationKind.Nit, "9 0 0 1 2 3 4 5 6 - 8", "900123456")]
    [InlineData(SiigoIdentificationKind.Nit, "90-0123456", "900123456")] // un guion en otra parte no corta el número
    [InlineData(SiigoIdentificationKind.Nit, "800197268-4", "800197268")]
    [InlineData(SiigoIdentificationKind.Nit, "8001972684", "800197268")] // 10 dígitos y el último es el de verificación
    [InlineData(SiigoIdentificationKind.Nit, "8909039388", "890903938")]
    [InlineData(SiigoIdentificationKind.Nit, "8600029644", "860002964")]
    [InlineData(SiigoIdentificationKind.Nit, "9001234568", "900123456")]
    [InlineData(SiigoIdentificationKind.Nit, "8001972685", "8001972685")] // el último no es el de verificación
    [InlineData(SiigoIdentificationKind.Nit, "800197268", "800197268")]
    [InlineData(SiigoIdentificationKind.Nit, "1020304050", "1020304050")] // persona natural
    [InlineData(SiigoIdentificationKind.Nit, "1020304058", "1020304058")] // el 8 sería su dígito, pero no empieza por 8 ni 9
    [InlineData(SiigoIdentificationKind.Nit, " - 8 ", null)]
    [InlineData(SiigoIdentificationKind.Ce, "AB-123 456", "AB123456")]
    [InlineData(SiigoIdentificationKind.Pasaporte, " ab.123 ", "ab123")]
    [InlineData(SiigoIdentificationKind.Ce, " .- ", null)]
    [InlineData(SiigoIdentificationKind.Nif, " X-123.4 ", "X-123.4")]
    [InlineData(null, " 12.34 ", "12.34")]
    [InlineData(null, "  ", null)]
    public void TheDocumentIsSentAsSiigoExpectsIt(SiigoIdentificationKind? kind, string raw, string? expected) =>
        Assert.Equal(expected, SiigoCustomerMapper.Identification(Person() with { IdentificationType = kind, Identification = raw }));

    [Fact]
    public void ACompanyNameIsASingleElement()
    {
        var request = SiigoCustomerMapper.Map(Company())!;

        Assert.Equal("Company", request.PersonType);
        Assert.Equal(["ACME SAS"], request.Name);
    }

    [Fact]
    public void APersonNameSplitsFirstTokenFromTheRest()
    {
        var request = SiigoCustomerMapper.Map(Person())!;

        Assert.Equal("Person", request.PersonType);
        Assert.Equal(["Ana", "María Gómez Pérez"], request.Name);
        Assert.Equal("Ana", request.ContactFirstName);
        Assert.Equal("María Gómez Pérez", request.ContactLastName);
    }

    [Fact]
    public void ASingleTokenNameIsRepeatedInBothSlots()
    {
        Assert.Equal(["Ana", "Ana"], SiigoCustomerMapper.Map(Person("Ana"))!.Name);
    }

    [Fact]
    public void AFinalConsumerUsesTheDianGenericDocument()
    {
        var request = SiigoCustomerMapper.Map(new SiigoBilledPartyInput(true, null, null, "", "", null, null, null, null, null))!;

        Assert.Equal("222222222222", request.Identification);
        Assert.Equal("13", request.IdTypeCode);
        Assert.Equal("Person", request.PersonType);
        Assert.Equal("11001", request.CityCode);
        Assert.Equal("11", request.StateCode);
    }

    [Fact]
    public void TheAddressCarriesTheDaneCodes()
    {
        var request = SiigoCustomerMapper.Map(Person())!;

        Assert.Equal(("Co", "05", "05001"), (request.CountryCode, request.StateCode, request.CityCode));
        Assert.Equal(new SiigoPhone("57", "3109998877"), request.Phone);
        Assert.Equal("ana@test.co", request.ContactEmail);
    }

    [Theory]
    [InlineData("57", "300 123-4567", "57", "3001234567")]
    [InlineData("+57", "3001234567", "57", "3001234567")]
    [InlineData(null, "12345", null, "12345")]
    [InlineData("12345678901", "3001234567", null, "3001234567")] // indicativo de más de 10: viaja el número solo
    [InlineData("", "3001234567", null, "3001234567")]
    public void ThePhoneIsCleanedToDigits(string? indicative, string number, string? expectedIndicative, string expectedNumber) =>
        Assert.Equal(
            new SiigoPhone(expectedIndicative, expectedNumber),
            SiigoCustomerMapper.Map(Person() with { Phone = new SiigoPhone(indicative, number) })!.Phone);

    [Theory]
    [InlineData("+57573001234567")] // no partido: 14 dígitos
    [InlineData("584121234567")]
    [InlineData("sin número")]
    [InlineData("  ")]
    public void ANumberThatIsEmptyOrLongerThanTenDigitsSendsNoPhone(string number) =>
        Assert.Null(SiigoCustomerMapper.Map(Person() with { Phone = new SiigoPhone(null, number) })!.Phone);

    [Fact]
    public void AFinalConsumerFollowsTheSamePhoneRules()
    {
        var party = new SiigoBilledPartyInput(
            true, null, null, "", "", null, null, null, null, new SiigoPhone("57", "+57 300 123 4567"));

        Assert.Null(SiigoCustomerMapper.Map(party)!.Phone); // «57300…» son 12 dígitos en el número
        Assert.Null(SiigoCustomerMapper.Map(party with { Phone = null })!.Phone);
        Assert.Equal(
            new SiigoPhone("57", "3001234567"),
            SiigoCustomerMapper.Map(party with { Phone = new SiigoPhone("57", "300 123 4567") })!.Phone);
    }

    [Fact]
    public void ThePhoneNeverPrints() =>
        Assert.DoesNotContain("3001234567", new SiigoPhone("57", "3001234567").ToString(), StringComparison.Ordinal);

    [Theory]
    [InlineData(SiigoIdentificationKind.Cc, "13")]
    [InlineData(SiigoIdentificationKind.Nit, "31")]
    [InlineData(SiigoIdentificationKind.Ce, "22")]
    [InlineData(SiigoIdentificationKind.Pasaporte, "41")]
    public void EachSupportedTypeHasItsSiigoCode(SiigoIdentificationKind kind, string code) =>
        Assert.Equal(code, SiigoIdentificationTypes.CodeFor(kind));

    [Theory]
    [InlineData(SiigoIdentificationKind.Nif)]
    [InlineData(SiigoIdentificationKind.Nie)]
    [InlineData(SiigoIdentificationKind.TaxId)]
    public void ANifNieOrTaxIdIsUnsupported(SiigoIdentificationKind kind)
    {
        Assert.Null(SiigoIdentificationTypes.CodeFor(kind));
        Assert.Null(SiigoCustomerMapper.Map(Person() with { IdentificationType = kind }));
    }

    [Fact]
    public void TheRequestNeverPrintsTheCustomer()
    {
        var text = SiigoCustomerMapper.Map(Person())!.ToString();

        Assert.DoesNotContain("1020304050", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana", text, StringComparison.Ordinal);
        Assert.DoesNotContain("3109998877", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ana@test.co", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRequestToStringIsRedactedEvenWithoutTheMapper()
    {
        var request = new SiigoCustomerRequest(
            "Person", "13", "123", ["Ana", "Gomez"], "Calle", "Co", "11", "11001", new SiigoPhone(null, "300"), "Ana", "Gomez", "a@b.co");

        Assert.DoesNotContain("123", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Gomez", request.ToString(), StringComparison.Ordinal);
    }
}
