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
