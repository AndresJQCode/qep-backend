using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>Spec 2026-11-15 §5.4: an internal note keeps the advisor's text rule and takes up to five different files.</summary>
public sealed class SupportTicketNoteTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 15, 0, 0, TimeSpan.Zero);

    private static SupportTicketNote Create(string text, IReadOnlyList<SupportNoteFileSnapshot>? files = null) =>
        SupportTicketNote.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), SupportTicketId.New(), Guid.CreateVersion7(), text, files ?? [], Now);

    private static SupportNoteFileSnapshot File(string name = "acta.pdf") =>
        new(Guid.CreateVersion7(), name, "application/pdf", 1024);

    [Fact]
    public void CreateTrimsTheText()
    {
        var note = Create("  El cliente llamó otra vez.  ");

        Assert.Equal("El cliente llamó otra vez.", note.Text);
        Assert.Equal(Now, note.CreatedAt);
        Assert.Empty(note.Files);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\u0007")]
    public void CreateRejectsEmptyTooLongOrControlCharacters(string text)
    {
        var error = Assert.Throws<SupportDomainException>(() => Create(text));

        Assert.Equal(SupportErrorCodes.NoteTextInvalid, error.Code);
    }

    [Fact]
    public void CreateRejectsATextLongerThan4096()
    {
        var error = Assert.Throws<SupportDomainException>(() => Create(new string('a', 4097)));

        Assert.Equal(SupportErrorCodes.NoteTextInvalid, error.Code);
        Assert.Equal(4096, Create(new string('a', 4096)).Text.Length);
    }

    [Fact]
    public void LineBreaksAndTabsPass()
    {
        Assert.Equal("a\nb\tc", Create("a\nb\tc").Text);
    }

    [Fact]
    public void CreateRejectsSixFilesOrARepeatedOne()
    {
        var six = Enumerable.Range(0, 6).Select(index => File($"archivo-{index}.pdf")).ToList();
        var repeated = File();

        var tooMany = Assert.Throws<SupportDomainException>(() => Create("Nota", six));
        var twice = Assert.Throws<SupportDomainException>(() => Create("Nota", [repeated, repeated]));

        Assert.Equal(SupportErrorCodes.NoteTooManyFiles, tooMany.Code);
        Assert.Equal(SupportErrorCodes.NoteTooManyFiles, twice.Code);
        Assert.Equal(SupportTicketNote.MaxFiles, Create("Nota", six.Take(5).ToList()).Files.Count);
    }

    [Fact]
    public void FilesKeepTheirOrder()
    {
        var second = File("b.png");
        var first = File("a.pdf");

        var note = Create("Nota", [second, first]);

        Assert.Equal([second.FileId, first.FileId], note.Files.Select(file => file.FileId));
        Assert.Equal([0, 1], note.Files.Select(file => file.Position));
        Assert.All(note.Files, file => Assert.Equal(note.Id, file.NoteId));
        Assert.Equal("b.png", note.Files[0].FileName);
        Assert.Equal("application/pdf", note.Files[0].MimeType);
        Assert.Equal(1024, note.Files[0].SizeBytes);
    }
}
