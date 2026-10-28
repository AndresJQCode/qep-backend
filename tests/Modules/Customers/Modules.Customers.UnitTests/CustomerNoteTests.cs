using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-11 §4.1: la nota es su propio agregado, con su propia versión; el cuerpo mide 1–2000.</summary>
public sealed class CustomerNoteTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly CustomerId CustomerId = CustomerId.New();
    private static readonly Guid Author = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static CustomerNote NoteWith(string body) =>
        CustomerNote.Create(CustomerNoteId.New(), TenantId, CustomerId, body, Author, Now);

    [Fact]
    public void CreateStartsAtVersionOneWithItsAuthor()
    {
        var note = NoteWith("Prefiere que le escriban por la tarde");

        Assert.Equal(TenantId, note.TenantId);
        Assert.Equal(CustomerId, note.CustomerId);
        Assert.Equal(Author, note.AuthorUserId);
        Assert.Equal(1, note.Version);
        Assert.Equal(Now, note.CreatedAt);
        Assert.Equal(Now, note.UpdatedAt);
    }

    [Fact]
    public void CreateTrimsTheBody()
    {
        Assert.Equal("Hola", NoteWith("  Hola \n").Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsABlankBody(string body)
    {
        var error = Assert.Throws<CustomersDomainException>(() => NoteWith(body));

        Assert.Equal("customers.note.body_required", error.Code);
    }

    [Fact]
    public void CreateAcceptsExactlyTheMaximumLength()
    {
        Assert.Equal(CustomerNote.BodyMaxLength, NoteWith(new string('a', CustomerNote.BodyMaxLength)).Body.Length);
    }

    [Fact]
    public void CreateRejectsABodyOverTheMaximum()
    {
        var error = Assert.Throws<CustomersDomainException>(() => NoteWith(new string('a', CustomerNote.BodyMaxLength + 1)));

        Assert.Equal("customers.note.body_too_long", error.Code);
    }

    [Fact]
    public void EditChangesTheBodyAndBumpsTheVersion()
    {
        var note = NoteWith("Uno");
        var later = Now.AddHours(1);

        note.Edit("Dos", later);

        Assert.Equal("Dos", note.Body);
        Assert.Equal(2, note.Version);
        Assert.Equal(Now, note.CreatedAt);
        Assert.Equal(later, note.UpdatedAt);
    }

    [Fact]
    public void AFailedEditLeavesTheNoteAsItWas()
    {
        var note = NoteWith("Uno");

        Assert.Throws<CustomersDomainException>(() => note.Edit("  ", Now.AddHours(1)));

        Assert.Equal("Uno", note.Body);
        Assert.Equal(1, note.Version);
    }

    [Fact]
    public void OnlyTheAuthorIsTheAuthor()
    {
        var note = NoteWith("Uno");

        Assert.True(note.IsAuthoredBy(Author));
        Assert.False(note.IsAuthoredBy(Guid.CreateVersion7()));
    }
}
