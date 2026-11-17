using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>Plan N16: the validator catches every body the note's domain guard would reject, as <c>validation.failed</c> with the
/// field in <c>errors</c>; <c>support.note.text_invalid</c> is not reachable over HTTP.</summary>
public sealed class AddTicketNoteValidatorTests
{
    private static List<string> Errors(Guid? clientId, string? text) =>
        new AddTicketNoteValidator()
            .Validate(new AddTicketNoteCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), clientId, text, null))
            .Errors.Select(error => error.PropertyName)
            .Distinct()
            .ToList();

    [Fact]
    public void AValidNotePasses()
    {
        Assert.Empty(Errors(Guid.CreateVersion7(), "El cliente pidió que lo llamen en la tarde."));
        Assert.Empty(Errors(Guid.CreateVersion7(), "Línea uno\nLínea dos\tcon tabulador"));
        Assert.Empty(Errors(Guid.CreateVersion7(), "  " + new string('a', 4096) + "  "));
    }

    [Fact]
    public void TheClientIdIsRequired()
    {
        Assert.Equal(["ClientId"], Errors(null, "Nota"));
        Assert.Equal(["ClientId"], Errors(Guid.Empty, "Nota"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hola\u0007")]
    public void AnInvalidTextFailsOnText(string? text) =>
        Assert.Equal(["Text"], Errors(Guid.CreateVersion7(), text));

    [Fact]
    public void ATextLongerThan4096FailsOnText() =>
        Assert.Equal(["Text"], Errors(Guid.CreateVersion7(), new string('a', 4097)));
}
