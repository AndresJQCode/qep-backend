using FluentValidation.Results;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.UnitTests;

public sealed class PurchaseOrderWriteRulesTests
{
    private static readonly Guid TenantId = PurchaseOrderTestData.TenantId;

    [Fact]
    public void AValidCommandPasses()
    {
        var result = Validate(Create([Line(), Line()]));

        Assert.True(result.IsValid, string.Join(", ", result.Errors.Select(error => error.PropertyName)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ZeroOrNegativeQuantityFailsOnTheRow(decimal quantity)
    {
        var result = Validate(Create([Line(quantity: quantity)]));

        Assert.Contains(result.Errors, error => error.PropertyName == "Lines[0].Quantity");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ZeroOrNegativeUnitCostFailsOnTheRow(decimal unitCost)
    {
        var result = Validate(Create([Line(unitCost: unitCost)]));

        Assert.Contains(result.Errors, error => error.PropertyName == "Lines[0].UnitCost");
    }

    // Las columnas son numeric(10,2) y numeric(14,2): un valor que no cabe es un 500 de Postgres (22003), y uno con más de dos
    // decimales se redondea al guardar, así el total de la vista previa y el de la siguiente lectura difieren (D-19).
    public static TheoryData<string, decimal, decimal, string> OutOfColumnLines => new()
    {
        { "create", 1.234m, 10m, "Lines[0].Quantity" },
        { "create", 100_000_000m, 10m, "Lines[0].Quantity" },
        { "create", 3m, 20.255m, "Lines[0].UnitCost" },
        { "create", 1m, 1_000_000_000_000m, "Lines[0].UnitCost" },
        { "update", 1.234m, 10m, "Lines[0].Quantity" },
        { "update", 100_000_000m, 10m, "Lines[0].Quantity" },
        { "update", 3m, 20.255m, "Lines[0].UnitCost" },
        { "update", 1m, 1_000_000_000_000m, "Lines[0].UnitCost" },
        { "preview", 1.234m, 10m, "Lines[0].Quantity" },
        { "preview", 100_000_000m, 10m, "Lines[0].Quantity" },
        { "preview", 3m, 20.255m, "Lines[0].UnitCost" },
        { "preview", 1m, 1_000_000_000_000m, "Lines[0].UnitCost" },
    };

    [Theory]
    [MemberData(nameof(OutOfColumnLines))]
    public void AValueThatDoesNotFitItsColumnFailsOnTheRow(string command, decimal quantity, decimal unitCost, string property)
    {
        IReadOnlyList<PurchaseOrderLineInput> lines = [Line(quantity: quantity, unitCost: unitCost)];
        var result = command switch
        {
            "create" => Validate(Create(lines)),
            "update" => new UpdatePurchaseOrderValidator().Validate(
                new UpdatePurchaseOrderCommand(TenantId, Guid.CreateVersion7(), 1, Guid.CreateVersion7(), "COP", null, lines)),
            _ => new PreviewPurchaseOrderValidator().Validate(
                new PreviewPurchaseOrderCommand(TenantId, Guid.CreateVersion7(), "COP", null, lines)),
        };

        Assert.Contains(result.Errors, error => error.PropertyName == property);
    }

    [Fact]
    public void TheLargestValuesThatFitTheColumnsPass()
    {
        Assert.True(Validate(Create([Line(quantity: 99_999_999.99m, unitCost: 999_999_999_999.99m)])).IsValid);
        Assert.True(Validate(Create([Line(quantity: 1.50m, unitCost: 20.25m)])).IsValid);
    }

    public static TheoryData<decimal, decimal, string> OutOfColumnConfirmations => new()
    {
        { 1.234m, 10m, "Lines[0].ConfirmedQuantity" },
        { 100_000_000m, 10m, "Lines[0].ConfirmedQuantity" },
        { 3m, 20.255m, "Lines[0].ConfirmedUnitCost" },
        { 1m, 1_000_000_000_000m, "Lines[0].ConfirmedUnitCost" },
    };

    [Theory]
    [MemberData(nameof(OutOfColumnConfirmations))]
    public void AConfirmationThatDoesNotFitItsColumnFailsOnTheRow(decimal confirmedQuantity, decimal confirmedUnitCost, string property)
    {
        var result = new ConfirmPurchaseOrderValidator().Validate(Confirm(confirmedQuantity, confirmedUnitCost));

        Assert.Contains(result.Errors, error => error.PropertyName == property);
    }

    [Fact]
    public void AnEmptyVariantIdFailsOnTheRow()
    {
        var result = Validate(Create([Line(variantId: Guid.Empty)]));

        Assert.Contains(result.Errors, error => error.PropertyName == "Lines[0].VariantId");
    }

    [Fact]
    public void ARepeatedVariantFailsOnTheSecondRow()
    {
        var variantId = Guid.CreateVersion7();

        var result = Validate(Create([Line(variantId: variantId), Line(variantId: variantId)]));

        Assert.Contains(result.Errors, error => error.PropertyName == "Lines[1].VariantId");
        Assert.DoesNotContain(result.Errors, error => error.PropertyName == "Lines[0].VariantId");
    }

    [Fact]
    public void MoreThan200LinesFail()
    {
        var lines = Enumerable.Range(0, PurchaseOrder.MaxLines + 1).Select(_ => Line()).ToList();

        var result = Validate(Create(lines));

        Assert.Contains(result.Errors, error => error.PropertyName == "Lines");
    }

    [Fact]
    public void ExactlyMaxLinesPass()
    {
        var lines = Enumerable.Range(0, PurchaseOrder.MaxLines).Select(_ => Line()).ToList();

        Assert.True(Validate(Create(lines)).IsValid);
    }

    [Fact]
    public void NotesOver2000Fail()
    {
        var result = Validate(Create([Line()], notes: new string('a', PurchaseOrder.NotesMaxLength + 1)));

        Assert.Contains(result.Errors, error => error.PropertyName == "Notes");
    }

    [Fact]
    public void AnEmptyLineListIsNotAValidationError()
    {
        Assert.True(Validate(Create([])).IsValid);
    }

    [Fact]
    public void UpdateAndPreviewRunTheSameRules()
    {
        IReadOnlyList<PurchaseOrderLineInput> lines = [Line(quantity: 0)];
        var update = new UpdatePurchaseOrderValidator().Validate(
            new UpdatePurchaseOrderCommand(TenantId, Guid.CreateVersion7(), 1, Guid.CreateVersion7(), "COP", null, lines));
        var preview = new PreviewPurchaseOrderValidator().Validate(
            new PreviewPurchaseOrderCommand(TenantId, Guid.CreateVersion7(), "COP", null, lines));

        Assert.Contains(update.Errors, error => error.PropertyName == "Lines[0].Quantity");
        Assert.Contains(preview.Errors, error => error.PropertyName == "Lines[0].Quantity");
    }

    [Fact]
    public void ConfirmAcceptsZeroQuantityAndRejectsNegative()
    {
        var validator = new ConfirmPurchaseOrderValidator();

        var zero = validator.Validate(Confirm(confirmedQuantity: 0m, confirmedUnitCost: 10m));
        var negative = validator.Validate(Confirm(confirmedQuantity: -1m, confirmedUnitCost: 10m));

        Assert.True(zero.IsValid);
        Assert.Contains(negative.Errors, error => error.PropertyName == "Lines[0].ConfirmedQuantity");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConfirmRejectsZeroCost(decimal confirmedUnitCost)
    {
        var result = new ConfirmPurchaseOrderValidator().Validate(Confirm(confirmedQuantity: 1m, confirmedUnitCost: confirmedUnitCost));

        Assert.Contains(result.Errors, error => error.PropertyName == "Lines[0].ConfirmedUnitCost");
    }

    [Fact]
    public void ListRejectsAnUnknownStatusAndAcceptsAnyCase()
    {
        var validator = new ListPurchaseOrdersValidator();

        var unknown = validator.Validate(List("paused"));
        var numeric = validator.Validate(List("1"));

        Assert.Contains(unknown.Errors, error => error.PropertyName == "Status");
        Assert.Contains(numeric.Errors, error => error.PropertyName == "Status");
        Assert.True(validator.Validate(List("sent")).IsValid);
        Assert.True(validator.Validate(List("Cancelled")).IsValid);
        Assert.True(validator.Validate(List(null)).IsValid);
        Assert.True(validator.Validate(List("")).IsValid);
    }

    private static ValidationResult Validate(CreatePurchaseOrderCommand command) =>
        new CreatePurchaseOrderValidator().Validate(command);

    private static CreatePurchaseOrderCommand Create(IReadOnlyList<PurchaseOrderLineInput> lines, string? notes = null) =>
        new(TenantId, Guid.CreateVersion7(), "COP", notes, lines);

    private static PurchaseOrderLineInput Line(decimal quantity = 2m, decimal unitCost = 10m, Guid? variantId = null) =>
        new(variantId ?? Guid.CreateVersion7(), quantity, unitCost);

    private static ConfirmPurchaseOrderCommand Confirm(decimal confirmedQuantity, decimal confirmedUnitCost) =>
        new(TenantId, Guid.CreateVersion7(), 2, [new PurchaseOrderLineConfirmationInput(Guid.CreateVersion7(), confirmedQuantity, confirmedUnitCost)]);

    private static ListPurchaseOrdersQuery List(string? status) => new(TenantId, status, null, null, 1, 50);
}
