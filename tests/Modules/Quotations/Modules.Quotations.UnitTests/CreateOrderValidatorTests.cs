using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-10-15 §5.2: el cuerpo del pedido manual. El preview comparte las reglas salvo el
/// documento de la parte de facturación, que el cálculo previo no exige.</summary>
public sealed class CreateOrderValidatorTests
{
    private static CreateOrderCommand Valid() => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), "Transferencia", null, false, null, null,
        new QuotationBillingAccountRequest(Guid.CreateVersion7(), "Bancolombia", "7542", "COP"),
        [new OrderItemAddition(Guid.CreateVersion7(), 1m)], []);

    [Fact]
    public void AcceptsAMinimalManualOrder() => Assert.True(new CreateOrderValidator().Validate(Valid()).IsValid);

    [Fact]
    public void RequiresAtLeastOneItem()
    {
        var result = new CreateOrderValidator().Validate(Valid() with { Items = [] });
        Assert.Contains(result.Errors, error => error.PropertyName == "Items");
    }

    [Fact]
    public void RejectsTheSameProductTwice()
    {
        var productId = Guid.CreateVersion7();
        var result = new CreateOrderValidator().Validate(
            Valid() with { Items = [new OrderItemAddition(productId, 1m), new OrderItemAddition(productId, 2m)] });
        Assert.Contains(result.Errors, error => error.PropertyName == "Items");
    }

    // Spec 2026-11-23: los regalos viajan en su propia lista; la misma variante puede ir pagada y de regalo.
    [Fact]
    public void RejectsTheSameVariantTwiceAsAGift()
    {
        var variantId = Guid.CreateVersion7();
        var result = new CreateOrderValidator().Validate(
            Valid() with { Gifts = [new OrderItemAddition(variantId, 1m), new OrderItemAddition(variantId, 2m)] });
        Assert.Contains(result.Errors, error => error.PropertyName == "Gifts");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsAGiftWithoutQuantity(int quantity)
    {
        var result = new CreateOrderValidator().Validate(
            Valid() with { Gifts = [new OrderItemAddition(Guid.CreateVersion7(), quantity)] });
        Assert.Contains(result.Errors, error => error.PropertyName == "Gifts[0].Quantity");
    }

    [Fact]
    public void AcceptsTheSameVariantPaidAndAsAGift()
    {
        var variantId = Guid.CreateVersion7();
        var result = new CreateOrderValidator().Validate(
            Valid() with
            {
                Items = [new OrderItemAddition(variantId, 10m)],
                Gifts = [new OrderItemAddition(variantId, 1m)],
            });
        Assert.True(result.IsValid);
    }

    [Fact]
    public void RequiresAPaidLineEvenWithGifts()
    {
        var result = new CreateOrderValidator().Validate(
            Valid() with { Items = [], Gifts = [new OrderItemAddition(Guid.CreateVersion7(), 1m)] });
        Assert.Contains(result.Errors, error => error.PropertyName == "Items");
    }

    [Fact]
    public void RequiresTheBillingAccount()
    {
        var result = new CreateOrderValidator().Validate(Valid() with { BillingAccount = null });
        Assert.Contains(result.Errors, error => error.PropertyName == "BillingAccount");
    }

    // El cálculo previo pinta el precio antes de que se elija la empresa que factura: sin cuenta, el backend
    // precia en la moneda por defecto del tenant. Guardar la sigue exigiendo (RequiresTheBillingAccount).
    [Fact]
    public void ThePreviewDoesNotRequireTheBillingAccount()
    {
        var command = Valid();
        var previewed = new PreviewOrderValidator().Validate(new PreviewOrderQuery(
            command.TenantId, command.ClientId, command.PaymentMethod, command.Notes, command.IsRetail,
            command.GlobalScaleFloor, command.Parties, BillingAccount: null, command.Items, command.PaymentProofs));

        Assert.True(previewed.IsValid);
    }

    // Una parte de facturación con nombre propio lleva documento al guardar; el preview no lo exige.
    [Fact]
    public void TheBillingPartyNeedsItsIdentificationOnlyWhenSaving()
    {
        var parties = new QuotationPartiesRequest(
            new QuotationPartyRequest("Otra S.A.S.", null, "1", "a@b.co", "x", Guid.CreateVersion7(), Guid.CreateVersion7()),
            null,
            BillingWithRetention: false,
            BillingVatSurplus: false);
        var command = Valid() with { Parties = parties };

        var saved = new CreateOrderValidator().Validate(command);
        var previewed = new PreviewOrderValidator().Validate(new PreviewOrderQuery(
            command.TenantId, command.ClientId, command.PaymentMethod, command.Notes, command.IsRetail,
            command.GlobalScaleFloor, command.Parties, command.BillingAccount, command.Items, command.PaymentProofs));

        Assert.Contains(saved.Errors, error => error.PropertyName == "Parties.Billing.IdentificationNumber");
        Assert.True(previewed.IsValid);
    }

    // Spec 2026-10-17 §5.4: el envío del pedido manual trae sus errores de forma con el campo, igual que la
    // cotización, en crear y en el cálculo previo.
    [Fact]
    public void TheShippingErrorsCarryTheField()
    {
        var shipping = new ShippingRequest(null, "Paid", 10.555m, false);
        var command = Valid() with { Shipping = shipping };

        var saved = new CreateOrderValidator().Validate(command);
        var previewed = new PreviewOrderValidator().Validate(new PreviewOrderQuery(
            command.TenantId, command.ClientId, command.PaymentMethod, command.Notes, command.IsRetail,
            command.GlobalScaleFloor, command.Parties, command.BillingAccount, command.Items, command.PaymentProofs,
            shipping));

        foreach (var result in new[] { saved, previewed })
        {
            Assert.Contains(result.Errors, error => error.PropertyName == "Shipping.FreightAmount");
            Assert.Contains(result.Errors, error => error.PropertyName == "Shipping.FreightChargeMode");
        }
    }

    // Spec 2026-11-27: el pedido manual exige el país de la facturación propia al guardar, como el documento; el
    // cálculo previo no lo exige.
    [Fact]
    public void TheBillingPartyNeedsItsCountryOnlyWhenSaving()
    {
        var parties = new QuotationPartiesRequest(
            new QuotationPartyRequest(
                "Otra S.A.S.", "900123456", "1", "a@b.co", "x", Guid.CreateVersion7(), Guid.CreateVersion7(),
                IdentificationType: "NIT"),
            null,
            BillingWithRetention: false,
            BillingVatSurplus: false);
        var command = Valid() with { Parties = parties };

        var saved = new CreateOrderValidator().Validate(command);
        var previewed = new PreviewOrderValidator().Validate(new PreviewOrderQuery(
            command.TenantId, command.ClientId, command.PaymentMethod, command.Notes, command.IsRetail,
            command.GlobalScaleFloor, command.Parties, command.BillingAccount, command.Items, command.PaymentProofs));

        Assert.Contains(saved.Errors, error => error.PropertyName == "Parties.Billing.Country");
        Assert.DoesNotContain(previewed.Errors, error => error.PropertyName == "Parties.Billing.Country");

        var withCountry = new CreateOrderValidator().Validate(command with
        {
            Parties = parties with { Billing = parties.Billing! with { Country = "CO" } }
        });
        Assert.DoesNotContain(withCountry.Errors, error => error.PropertyName == "Parties.Billing.Country");
    }
}
