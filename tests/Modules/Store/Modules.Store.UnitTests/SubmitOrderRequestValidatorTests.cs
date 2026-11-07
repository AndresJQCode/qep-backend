using FluentValidation;
using Modules.Store.Application;

namespace Modules.Store.UnitTests;

public sealed class SubmitOrderRequestValidatorTests
{
    private static readonly Guid Department = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid City = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static PublicBillingInput Billing() =>
        new("Ana Pérez", "CC", "123456", "3001234567", "ana@test.co", "Calle 1 # 2-3", "CO", Department, City, null);

    private static PublicShippingInput Shipping() =>
        new(null, "3007654321", "Carrera 9 # 8-7", "CO", Department, City, null);

    private static PublicOrderRequestBody Body(
        PublicBillingInput? billing = null, PublicShippingInput? shipping = null, IReadOnlyList<PublicCartLineRequest>? lines = null) =>
        new(
            Guid.NewGuid(),
            "turnstile-token",
            "COP",
            billing ?? Billing(),
            shipping,
            null,
            true,
            lines ?? [new PublicCartLineRequest(Guid.NewGuid(), 2)]);

    private static List<string> Errors(PublicOrderRequestBody body) =>
        new PublicOrderRequestBodyValidator().Validate(body).Errors.Select(error => error.PropertyName).ToList();

    [Fact]
    public void AFullBodyIsValid() => Assert.Empty(Errors(Body(shipping: Shipping())));

    [Fact]
    public void ABillingBlockWithoutDocumentIsInvalid()
    {
        Assert.Contains("Billing.IdentificationNumber", Errors(Body(Billing() with { IdentificationNumber = " " })));
        Assert.Contains("Billing.IdentificationType", Errors(Body(Billing() with { IdentificationType = "" })));
    }

    [Fact]
    public void AnUnknownIdentificationTypeIsInvalid() =>
        Assert.Contains("Billing.IdentificationType", Errors(Body(Billing() with { IdentificationType = "RUT" })));

    [Fact]
    public void AShippingBlockWithoutPhoneIsInvalid() =>
        Assert.Contains("Shipping.Phone", Errors(Body(shipping: Shipping() with { Phone = "" })));

    [Fact]
    public void ABillingBlockWithoutEmailIsInvalid() =>
        Assert.Contains("Billing.Email", Errors(Body(Billing() with { Email = null! })));

    [Fact]
    public void ABillingEmailWithoutShapeIsInvalid() =>
        Assert.Contains("Billing.Email", Errors(Body(Billing() with { Email = "not-an-email" })));

    [Fact]
    public void AMissingBillingBlockIsInvalidAndDoesNotThrow() =>
        Assert.Contains("Billing", Errors(Body() with { Billing = null! }));

    [Fact]
    public void ANoteOver1000CharactersIsInvalid() =>
        Assert.Contains("Note", Errors(Body() with { Note = new string('a', 1001) }));

    [Fact]
    public void AForeignBlockNeedsACityName() =>
        Assert.Contains("Billing.CityName", Errors(Body(Billing() with { Country = "ES", DepartmentId = null, CityId = null, CityName = null })));

    [Fact]
    public void AForeignBlockWithACityNameIsValid() =>
        Assert.Empty(Errors(Body(Billing() with { Country = "ES", DepartmentId = null, CityId = null, CityName = "Madrid" })));

    [Fact]
    public void AColombianBlockWithHalfTheIdsIsInvalid() =>
        Assert.Contains("Billing.CityId", Errors(Body(Billing() with { CityId = null })));

    [Fact]
    public void AForeignBlockWithIdsIsLeftToTheHandler() =>
        // Ids para otro país no son un error de forma: son city_invalid en el manejador.
        Assert.Empty(Errors(Body(Billing() with { Country = "ES", CityName = null })));

    [Fact]
    public void ACountryThatIsNotTwoLettersIsInvalid() =>
        Assert.Contains("Billing.Country", Errors(Body(Billing() with { Country = "COL" })));

    [Fact]
    public void ACartFollowsTheQuoteLimits()
    {
        Assert.Contains("Lines", Errors(Body(lines: [])));
        Assert.Contains("Lines", Errors(Body(lines: Enumerable.Range(0, 101).Select(_ => new PublicCartLineRequest(Guid.NewGuid(), 1)).ToList())));
        Assert.NotEmpty(Errors(Body(lines: [new PublicCartLineRequest(Guid.NewGuid(), 100_001)])));
        Assert.NotEmpty(Errors(Body(lines: [new PublicCartLineRequest(Guid.Empty, 1)])));
    }

    [Fact]
    public void AMissingCurrencyIsNotAShapeError() =>
        // La moneda ausente es store.public.currency_invalid del manejador, nunca validation.failed.
        Assert.Empty(Errors(Body() with { Currency = null }));

    [Fact]
    public void AMissingTokenIsInvalid() =>
        Assert.Contains("TurnstileToken", Errors(Body() with { TurnstileToken = "" }));
}
