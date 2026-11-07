using Bootstrapper.ApiKeys;
using Microsoft.AspNetCore.Http;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-11-01 parte 1: la nota de una llamada externa. Sin una clave identificada no hay fila, porque no hay de qué tenant es.</summary>
public sealed class ExternalCallNoteTests
{
    private const string Path = "/api/v1/external/whatsapp/messages";

    private static readonly Guid KeyId = Guid.CreateVersion7();
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid MessageId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ACallWithoutAKnownKeyProducesNoRow() =>
        Assert.Null(ExternalCallNote.Attach(new DefaultHttpContext()).ToRequest("POST", Path, 401, "t", 1, Now));

    [Fact]
    public void AnIdentifiedCallKeepsWhatWasNoted()
    {
        var note = ExternalCallNote.Attach(new DefaultHttpContext());
        note.Identify(KeyId, "ERP", TenantId);
        note.NoteMessage(MessageId);
        note.NoteError("external.send_unconfirmed");

        var row = note.ToRequest("POST", Path, 502, "t", 12, Now)!;

        Assert.Equal((TenantId, KeyId, "ERP", MessageId, "external.send_unconfirmed"), (row.TenantId, row.ApiKeyId, row.ApiKeyName, row.MessageId, row.ErrorCode));
    }

    [Fact]
    public void FindReturnsNothingOutsideTheExternalApi() =>
        Assert.Null(ExternalCallNote.Find(new DefaultHttpContext()));

    [Fact]
    public void FindReturnsTheNoteOfThisCall()
    {
        var context = new DefaultHttpContext();

        var note = ExternalCallNote.Attach(context);

        Assert.Same(note, ExternalCallNote.Find(context));
    }
}
