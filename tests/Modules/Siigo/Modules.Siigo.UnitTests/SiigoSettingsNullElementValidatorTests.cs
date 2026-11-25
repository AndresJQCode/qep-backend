using Modules.Siigo.Application.Settings;

namespace Modules.Siigo.UnitTests;

/// <summary>Un elemento nulo en una lista de la configuración (<c>vat: [null]</c>) falla en su propio campo, también cuando hay varios:
/// sin esto la regla de «sin repetir» lee el nulo y es un 500.</summary>
public sealed class SiigoSettingsNullElementValidatorTests
{
    [Fact]
    public void ANullVatLinkFailsOnItsElement()
    {
        var result = new UpdateSiigoTaxesValidator().Validate(
            new UpdateSiigoTaxesCommand(
                Guid.CreateVersion7(), Guid.CreateVersion7(), [new SiigoVatLinkInput(19, "1"), null!, null!], [], 0));

        Assert.Equal(["vat[1]", "vat[2]"], result.Errors.Select(error => error.PropertyName));
    }

    [Fact]
    public void ANullWithholdingLinkFailsOnItsElement()
    {
        var result = new UpdateSiigoTaxesValidator().Validate(
            new UpdateSiigoTaxesCommand(
                Guid.CreateVersion7(), Guid.CreateVersion7(), [], [new SiigoWithholdingLinkInput(Guid.CreateVersion7(), "1"), null!], 0));

        Assert.Equal("withholdings[1]", Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public void ANullAccountLinkFailsOnItsElement()
    {
        var result = new UpdateSiigoPaymentsValidator().Validate(
            new UpdateSiigoPaymentsCommand(
                Guid.CreateVersion7(), Guid.CreateVersion7(), [new SiigoAccountLinkInput("bank|1", "5101"), null!], null, null, null, null,
                0));

        Assert.Equal("accounts[1]", Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public void ANullSellerLinkFailsOnItsElement()
    {
        var result = new UpdateSiigoSellersValidator().Validate(
            new UpdateSiigoSellersCommand(
                Guid.CreateVersion7(), Guid.CreateVersion7(), [null!, new SiigoSellerLinkInput(Guid.CreateVersion7(), null, null)], 0));

        Assert.Equal("sellers[0]", Assert.Single(result.Errors).PropertyName);
    }

    // La regla de «sin repetir» sigue igual con los elementos que sí vienen.
    [Fact]
    public void ARepeatedElementStillFailsBesideANullOne()
    {
        var membershipId = Guid.CreateVersion7();
        var rate = Guid.CreateVersion7();

        var taxes = new UpdateSiigoTaxesValidator().Validate(
            new UpdateSiigoTaxesCommand(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                [new SiigoVatLinkInput(19, "1"), null!, new SiigoVatLinkInput(19, "2")],
                [new SiigoWithholdingLinkInput(rate, "1"), new SiigoWithholdingLinkInput(rate, "2"), null!],
                0));
        var payments = new UpdateSiigoPaymentsValidator().Validate(
            new UpdateSiigoPaymentsCommand(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                [new SiigoAccountLinkInput("bank|1", "5101"), null!, new SiigoAccountLinkInput("bank|1", null)],
                null, null, null, null, 0));
        var sellers = new UpdateSiigoSellersValidator().Validate(
            new UpdateSiigoSellersCommand(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                [new SiigoSellerLinkInput(membershipId, null, null), null!, new SiigoSellerLinkInput(membershipId, null, null)],
                0));

        Assert.Equal(["vat", "vat[1]", "withholdings", "withholdings[2]"], taxes.Errors.Select(error => error.PropertyName).Order());
        Assert.Equal(["accounts", "accounts[1]"], payments.Errors.Select(error => error.PropertyName).Order());
        Assert.Equal(["sellers", "sellers[1]"], sellers.Errors.Select(error => error.PropertyName).Order());
    }
}
