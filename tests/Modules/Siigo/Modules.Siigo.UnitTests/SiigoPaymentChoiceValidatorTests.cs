using Modules.Siigo.Application.Invoicing;

namespace Modules.Siigo.UnitTests;

/// <summary>Un <c>payments: [null]</c> falla en el campo de ese elemento, en el comando de emisión y en la tanda de uno: sin esto sería un
/// 500.</summary>
public sealed class SiigoPaymentChoiceValidatorTests
{
    private static readonly SiigoPaymentChoice[] WithANullElement = [new("Order", Guid.CreateVersion7(), "5101"), null!];

    [Fact]
    public void ANullPaymentOfTheOrderPostFailsOnItsElement()
    {
        var command = new IssueSiigoInvoiceCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), WithANullElement);

        var result = new IssueSiigoInvoiceValidator().Validate(command);

        Assert.Equal("Payments[1]", Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public void ANullPaymentOfABatchFailsOnItsElement()
    {
        var command = new CreateSiigoInvoiceBatchCommand(Guid.CreateVersion7(), [Guid.CreateVersion7()], WithANullElement);

        var result = new CreateSiigoInvoiceBatchValidator().Validate(command);

        Assert.Equal("Payments[1]", Assert.Single(result.Errors).PropertyName);
    }
}
