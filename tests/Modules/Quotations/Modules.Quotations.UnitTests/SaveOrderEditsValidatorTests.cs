using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-09-17, «Validación (<c>SaveOrderEditsValidator</c>)». El preview comparte
/// las reglas salvo las de archivos: todavía no se subieron.</summary>
public sealed class SaveOrderEditsValidatorTests
{
    private static readonly Guid ProductId = Guid.CreateVersion7();

    private readonly SaveOrderEditsValidator _save = new();
    private readonly PreviewOrderEditsValidator _preview = new();

    private static SaveOrderEditsCommand Command(
        IReadOnlyList<OrderItemAddition>? items = null,
        OrderEditProofs? proofs = null,
        string? notes = null) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            3,
            items ?? [new OrderItemAddition(ProductId, 1m)],
            proofs ?? OrderEditProofs.None,
            notes,
            null);

    private static PreviewOrderEditsQuery Query(OrderEditProofs proofs) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), [new OrderItemAddition(ProductId, 1m)], proofs, null, null);

    private static void AssertFailsOn(FluentValidation.Results.ValidationResult result, string propertyName)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == propertyName);
    }

    [Fact]
    public void AcceptsACompleteEdit()
    {
        var proofs = new OrderEditProofs(
            [new OrderEditProofAddition(Guid.CreateVersion7(), 150_000m)],
            [new OrderPaymentProofUpdateRequest(Guid.CreateVersion7(), 90_000m, Guid.CreateVersion7())],
            [Guid.CreateVersion7()]);

        var result = _save.Validate(Command(proofs: proofs, notes: "Entregar el lunes"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void RejectsAnEmptyItemList() =>
        AssertFailsOn(_save.Validate(Command(items: [])), "Items");

    [Fact]
    public void RejectsARepeatedProduct() =>
        AssertFailsOn(
            _save.Validate(Command(items: [new OrderItemAddition(ProductId, 1m), new OrderItemAddition(ProductId, 2m)])),
            "Items");

    [Fact]
    public void RejectsAnEmptyProductId() =>
        AssertFailsOn(_save.Validate(Command(items: [new OrderItemAddition(Guid.Empty, 1m)])), "Items[0].ProductId");

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositiveQuantity(int quantity) =>
        AssertFailsOn(_save.Validate(Command(items: [new OrderItemAddition(ProductId, quantity)])), "Items[0].Quantity");

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RejectsANewProofWithANonPositiveAmount(int amount) =>
        AssertFailsOn(
            _save.Validate(Command(proofs: new OrderEditProofs(
                [new OrderEditProofAddition(Guid.CreateVersion7(), amount)], [], []))),
            "Proofs.Add[0].Amount");

    // «add[].fileId no vacío (sólo en guardado)».
    [Fact]
    public void RequiresTheFileOfANewProofOnlyWhenSaving()
    {
        var proofs = new OrderEditProofs([new OrderEditProofAddition(null, 150_000m)], [], []);

        AssertFailsOn(_save.Validate(Command(proofs: proofs)), "Proofs.Add[0].FileId");
        Assert.True(_preview.Validate(Query(proofs)).IsValid);
    }

    [Fact]
    public void RejectsAnEmptyFileIdOfANewProofWhenSaving() =>
        AssertFailsOn(
            _save.Validate(Command(proofs: new OrderEditProofs(
                [new OrderEditProofAddition(Guid.Empty, 150_000m)], [], []))),
            "Proofs.Add[0].FileId");

    [Fact]
    public void RejectsUpdatingTheSameProofTwice()
    {
        var proofId = Guid.CreateVersion7();
        var proofs = new OrderEditProofs(
            [],
            [new OrderPaymentProofUpdateRequest(proofId, 1m), new OrderPaymentProofUpdateRequest(proofId, 2m)],
            []);

        AssertFailsOn(_save.Validate(Command(proofs: proofs)), "Proofs.Update");
    }

    [Fact]
    public void RejectsAnEmptyReplacementFile() =>
        AssertFailsOn(
            _save.Validate(Command(proofs: new OrderEditProofs(
                [], [new OrderPaymentProofUpdateRequest(Guid.CreateVersion7(), 1m, Guid.Empty)], []))),
            "Proofs.Update[0].NewFileId");

    [Fact]
    public void RejectsUpdatingAndRemovingTheSameProof()
    {
        var proofId = Guid.CreateVersion7();
        var proofs = new OrderEditProofs([], [new OrderPaymentProofUpdateRequest(proofId, 1m)], [proofId]);

        AssertFailsOn(_save.Validate(Command(proofs: proofs)), "Proofs");
        AssertFailsOn(_preview.Validate(Query(proofs)), "Proofs");
    }

    [Fact]
    public void RejectsTheSameFileAsANewProofAndAsAReplacement()
    {
        var fileId = Guid.CreateVersion7();
        var proofs = new OrderEditProofs(
            [new OrderEditProofAddition(fileId, 1m)],
            [new OrderPaymentProofUpdateRequest(Guid.CreateVersion7(), 1m, fileId)],
            []);

        AssertFailsOn(_save.Validate(Command(proofs: proofs)), "Proofs");
    }

    [Fact]
    public void RejectsNotesLongerThanTheLimit() =>
        AssertFailsOn(_save.Validate(Command(notes: new string('a', Order.NotesMaxLength + 1))), "Notes");
}
