using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Las reglas del guardado de una vez de una cotización, ejercidas por los dos tipos que las
/// comparten: el comando (<c>PUT /quotations/{id}</c>) y la consulta del cálculo previo
/// (<c>POST /quotations/{id}/preview</c>). Van lado a lado a propósito — el cálculo previo tiene
/// que reportar el mismo 422 que el guardado, o la pantalla pasa una validación que después falla
/// al guardar.
/// </summary>
public sealed class SaveQuotationValidatorTests
{
    private static readonly Guid ProductId = Guid.CreateVersion7();

    private readonly SaveQuotationValidator _save = new();
    private readonly PreviewQuotationValidator _preview = new();

    private static SaveQuotationCommand Command(
        IReadOnlyList<QuotationItemAddition>? items = null,
        string? paymentMethod = null) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            3,
            new DateOnly(2026, 10, 31),
            paymentMethod,
            null,
            null,
            null,
            items ?? [new QuotationItemAddition(ProductId, 1m)]);

    private static PreviewQuotationQuery Query(
        IReadOnlyList<QuotationItemAddition>? items = null,
        string? paymentMethod = null) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new DateOnly(2026, 10, 31),
            paymentMethod,
            null,
            null,
            null,
            items ?? [new QuotationItemAddition(ProductId, 1m)]);

    private static void AssertFailsOn(FluentValidation.Results.ValidationResult result, string propertyName)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == propertyName);
    }

    [Fact]
    public void AcceptsACompleteEdit()
    {
        Assert.True(_save.Validate(Command(paymentMethod: "Transferencia")).IsValid);
        Assert.True(_preview.Validate(Query(paymentMethod: "Transferencia")).IsValid);
    }

    // A diferencia del pedido, un borrador sí puede quedarse sin líneas: nace vacío y borrar la
    // última es un gesto legítimo del editor.
    [Fact]
    public void AcceptsAnEmptyItemList()
    {
        Assert.True(_save.Validate(Command(items: [])).IsValid);
        Assert.True(_preview.Validate(Query(items: [])).IsValid);
    }

    [Fact]
    public void RejectsARepeatedProduct()
    {
        IReadOnlyList<QuotationItemAddition> items =
            [new QuotationItemAddition(ProductId, 1m), new QuotationItemAddition(ProductId, 2m)];

        AssertFailsOn(_save.Validate(Command(items: items)), "Items");
        AssertFailsOn(_preview.Validate(Query(items: items)), "Items");
    }

    [Fact]
    public void RejectsAnEmptyProductId()
    {
        IReadOnlyList<QuotationItemAddition> items = [new QuotationItemAddition(Guid.Empty, 1m)];

        AssertFailsOn(_save.Validate(Command(items: items)), "Items[0].ProductId");
        AssertFailsOn(_preview.Validate(Query(items: items)), "Items[0].ProductId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositiveQuantity(int quantity)
    {
        IReadOnlyList<QuotationItemAddition> items = [new QuotationItemAddition(ProductId, quantity)];

        AssertFailsOn(_save.Validate(Command(items: items)), "Items[0].Quantity");
        AssertFailsOn(_preview.Validate(Query(items: items)), "Items[0].Quantity");
    }

    // La regla del encabezado es la misma que ya aplicaba el PATCH (UpdateQuotationValidator): no
    // se reescribe, se comparte.
    [Fact]
    public void RejectsAPaymentMethodLongerThanTheLimit()
    {
        var tooLong = new string('a', Quotation.PaymentMethodMaxLength + 1);

        AssertFailsOn(_save.Validate(Command(paymentMethod: tooLong)), "PaymentMethod");
        AssertFailsOn(_preview.Validate(Query(paymentMethod: tooLong)), "PaymentMethod");
        AssertFailsOn(
            new UpdateQuotationValidator().Validate(
                new UpdateQuotationCommand(
                    Guid.CreateVersion7(), Guid.CreateVersion7(), null, tooLong, null, null, null)),
            "PaymentMethod");
    }
}
