using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-10-15 §5.2: el cuerpo del pedido manual. El preview comparte las reglas salvo el
/// documento de la parte de facturación, que el cálculo previo no exige.</summary>
public sealed class CreateOrderValidatorTests
{
    private static CreateOrderCommand Valid() => new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), "Transferencia", null, false, null, null,
        new QuotationBillingAccountRequest(Guid.CreateVersion7(), "Bancolombia", "7542", "COP"),
        [new OrderItemAddition(Guid.CreateVersion7(), 1m)], "PaymentPending", []);

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

    [Fact]
    public void RequiresTheBillingAccount()
    {
        var result = new CreateOrderValidator().Validate(Valid() with { BillingAccount = null! });
        Assert.Contains(result.Errors, error => error.PropertyName == "BillingAccount");
    }

    [Fact]
    public void RejectsAnUnknownPaymentStatus()
    {
        var result = new CreateOrderValidator().Validate(Valid() with { PaymentStatus = "Paid" });
        Assert.Contains(result.Errors, error => error.PropertyName == "PaymentStatus");
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
            command.GlobalScaleFloor, command.Parties, command.BillingAccount, command.Items, command.PaymentStatus,
            command.PaymentProofs));

        Assert.Contains(saved.Errors, error => error.PropertyName == "Parties.Billing.IdentificationNumber");
        Assert.True(previewed.IsValid);
    }
}
