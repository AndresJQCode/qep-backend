using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Crear una cotización acepta el documento entero (spec 2026-10-18 §7.3), así que sus reglas de campo son las
/// del guardado más el cliente, y la identificación de la facturación se sigue exigiendo como hoy.
/// </summary>
public sealed class CreateQuotationValidatorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();

    [Fact]
    public async Task ARepeatedProductIsAFieldError()
    {
        var productId = Guid.CreateVersion7();

        var result = await new CreateQuotationValidator().ValidateAsync(
            Command(new(productId, 1m), new(productId, 2m)), TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, error => error.PropertyName == "Items");
    }

    [Fact]
    public async Task AZeroQuantityIsAFieldError()
    {
        var result = await new CreateQuotationValidator().ValidateAsync(
            Command(new QuotationItemAddition(Guid.CreateVersion7(), 0m)), TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, error => error.PropertyName == "Items[0].Quantity");
    }

    [Fact]
    public async Task AnEmptyProductIsAFieldError()
    {
        var result = await new CreateQuotationValidator().ValidateAsync(
            Command(new QuotationItemAddition(Guid.Empty, 1m)), TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, error => error.PropertyName == "Items[0].ProductId");
    }

    [Fact]
    public async Task AnEmptyClientIsAFieldError()
    {
        var result = await new CreateQuotationValidator().ValidateAsync(
            Command() with { ClientId = Guid.Empty }, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, error => error.PropertyName == "ClientId");
    }

    // D-L3: el backend no exige líneas al crear; lo pide la pantalla.
    [Fact]
    public async Task NoItemsIsValid()
    {
        var result = await new CreateQuotationValidator().ValidateAsync(
            Command(), TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ABillingPartyWithoutIdentificationIsAFieldError()
    {
        var parties = new QuotationPartiesRequest(
            new QuotationPartyRequest(
                "Distribuciones Andinas S.A.S.", IdentificationNumber: null, null, null, null, null, null),
            null);

        var result = await new CreateQuotationValidator().ValidateAsync(
            Command() with { Parties = parties }, TestContext.Current.CancellationToken);

        Assert.Contains(result.Errors, error => error.PropertyName == "Parties.Billing.IdentificationNumber");
    }

    private static CreateQuotationCommand Command(params QuotationItemAddition[] items) =>
        new(TenantId, ClientId, ValidUntil: null, PaymentMethod: null, Notes: null, Parties: null,
            BillingAccount: null, Items: items);
}
