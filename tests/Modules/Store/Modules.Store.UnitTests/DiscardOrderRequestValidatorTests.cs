using Modules.Store.Application;

namespace Modules.Store.UnitTests;

/// <summary>«Otro» sin nota se rechaza en el validador, para que el 422 traiga <c>errors.note</c> y el formulario marque el campo.</summary>
public sealed class DiscardOrderRequestValidatorTests
{
    private static readonly DiscardOrderRequestValidator Validator = new();

    private static List<string> Errors(string? reason, string? note) =>
        Validator.Validate(new DiscardOrderRequestCommand(Guid.NewGuid(), Guid.NewGuid(), reason, note))
            .Errors.Select(error => error.PropertyName)
            .ToList();

    [Theory]
    [InlineData("Other", null)]
    [InlineData("other", "   ")]
    public void OtherWithoutANoteFailsOnTheNote(string reason, string? note) =>
        Assert.Equal(["Note"], Errors(reason, note));

    [Fact]
    public void OtherWithANotePasses() => Assert.Empty(Errors("Other", "Pidió por teléfono"));

    [Theory]
    [InlineData("Spam")]
    [InlineData("Duplicate")]
    public void TheOtherReasonsNeedNoNote(string reason) => Assert.Empty(Errors(reason, null));
}
