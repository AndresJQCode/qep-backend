using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportPublicHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-15 §5.4 and plan Task 5 (N17 to N21): the files of internal notes. They use Storage's member-less upload with an owner
/// derived from ticket and member (<see cref="SupportNoteUploads.OwnerKey"/>), so only the member who opened the upload can put it in
/// a note of that ticket. Own host per class, like <see cref="SupportPublicAttachmentsApiTests"/>: the private bucket lives in memory
/// and the test "uploads" by writing the object there. Each test seeds its own published tenant (the public form is needed to prove
/// that its uploads and the notes' never mix).
/// </summary>
public sealed class SupportTicketNoteFilesApiTests(SupportPublicAttachmentsHost host) : IClassFixture<SupportPublicAttachmentsHost>
{
    private static readonly byte[] PdfBytes = "%PDF-1.7\nacta de entrega"u8.ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ANoteKeepsItsFilesInOrder()
    {
        var rig = await RigAsync();
        var a = await UploadAsync(rig, rig.Ana, rig.TicketId, "a.pdf");
        var b = await UploadAsync(rig, rig.Ana, rig.TicketId, "b.pdf");

        var response = await PostNoteAsync(rig.Ana, rig, rig.TicketId, [b, a]);

        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        var files = (await JsonAsync(response)).GetProperty("files").EnumerateArray().ToList();
        Assert.Equal([b, a], files.Select(file => file.GetProperty("fileId").GetGuid()));
        Assert.Equal(["b.pdf", "a.pdf"], files.Select(file => file.GetProperty("name").GetString()));
        Assert.All(files, file =>
        {
            Assert.Equal("application/pdf", file.GetProperty("mimeType").GetString());
            Assert.Equal((long)PdfBytes.Length, file.GetProperty("sizeBytes").GetInt64());
        });
        var listed = await JsonAsync(await rig.Ana.GetAsync($"{rig.Url}/tickets/{rig.TicketId}/notes", Ct));
        Assert.Equal(
            [b, a],
            listed.GetProperty("items")[0].GetProperty("files").EnumerateArray().Select(file => file.GetProperty("fileId").GetGuid()));
    }

    [Fact]
    public async Task SixFilesAreRejected()
    {
        var rig = await RigAsync();
        var files = new List<Guid>();
        for (var i = 0; i < SupportTicketNote.MaxFiles + 1; i++)
        {
            files.Add(await UploadAsync(rig, rig.Ana, rig.TicketId, $"archivo-{i}.pdf"));
        }

        var response = await PostNoteAsync(rig.Ana, rig, rig.TicketId, files);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(SupportErrorCodes.NoteTooManyFiles, await CodeOfAsync(response));
        Assert.Equal(HttpStatusCode.Created, (await PostNoteAsync(rig.Ana, rig, rig.TicketId, files.Take(5).ToList())).StatusCode);
    }

    [Fact]
    public async Task ARepeatedFileIsRejected()
    {
        var rig = await RigAsync();
        var file = await UploadAsync(rig, rig.Ana, rig.TicketId, "acta.pdf");

        var response = await PostNoteAsync(rig.Ana, rig, rig.TicketId, [file, file]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(SupportErrorCodes.NoteTooManyFiles, await CodeOfAsync(response));
    }

    // Plan N18: the owner of the upload is (ticket, member). Bruno's upload is not Ana's, and an upload for another ticket is not this one's.
    [Fact]
    public async Task AFileOfAnotherMemberOrTicketIsInvalid()
    {
        var rig = await RigAsync();
        var otherTicket = await CreateTicketAsync(rig);
        var bruno = await UploadAsync(rig, rig.Bruno, rig.TicketId, "de-bruno.pdf");
        var elsewhere = await UploadAsync(rig, rig.Ana, otherTicket, "de-otro-ticket.pdf");

        var byBruno = await PostNoteAsync(rig.Ana, rig, rig.TicketId, [bruno]);
        var fromAnotherTicket = await PostNoteAsync(rig.Ana, rig, rig.TicketId, [elsewhere]);

        foreach (var response in new[] { byBruno, fromAnotherTicket })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(SupportErrorCodes.NoteFileInvalid, await CodeOfAsync(response));
        }

        Assert.Equal(HttpStatusCode.Created, (await PostNoteAsync(rig.Bruno, rig, rig.TicketId, [bruno])).StatusCode);
    }

    [Fact]
    public async Task APendingOrRejectedFileIsInvalid()
    {
        var rig = await RigAsync();
        var pending = await OpenAsync(rig, rig.Ana, rig.TicketId, "acta.pdf", PdfBytes.Length);
        var rejected = await OpenAsync(rig, rig.Ana, rig.TicketId, "falso.pdf", 4);
        host.Storage.Put(await StagingKeyAsync(rig, rejected), "MZ\0\0"u8.ToArray());
        var completed = await CompleteAsync(rig.Ana, rig, rig.TicketId, rejected);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal("rejected", (await JsonAsync(completed)).GetProperty("status").GetString());

        var withPending = await PostNoteAsync(rig.Ana, rig, rig.TicketId, [pending]);
        var withRejected = await PostNoteAsync(rig.Ana, rig, rig.TicketId, [rejected]);
        var unknown = await PostNoteAsync(rig.Ana, rig, rig.TicketId, [Guid.CreateVersion7()]);

        foreach (var response in new[] { withPending, withRejected, unknown })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(SupportErrorCodes.NoteFileInvalid, await CodeOfAsync(response));
        }

        Assert.Equal(0L, await NoteCountAsync(rig));
    }

    [Fact]
    public async Task TheSameFileCannotBeInTwoNotes()
    {
        var rig = await RigAsync();
        var file = await UploadAsync(rig, rig.Ana, rig.TicketId, "acta.pdf");
        Assert.Equal(HttpStatusCode.Created, (await PostNoteAsync(rig.Ana, rig, rig.TicketId, [file])).StatusCode);

        var again = await PostNoteAsync(rig.Ana, rig, rig.TicketId, [file]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal(SupportErrorCodes.NoteFileInvalid, await CodeOfAsync(again));
        Assert.Equal(1L, await NoteCountAsync(rig));
    }

    // A visitor's upload carries a random v4 key; a note's, the key of (ticket, member). Neither is the other's. The visitor never holds a
    // note upload's key, so filing with one of its own is what an attempt looks like.
    [Fact]
    public async Task APublicUploadCannotBeUsedInANoteAndViceVersa()
    {
        var rig = await RigAsync();
        var publicUpload = await UploadPublicAsync(rig, "del-visitante.pdf");
        var noteUpload = await UploadAsync(rig, rig.Ana, rig.TicketId, "de-la-nota.pdf");

        var inANote = await PostNoteAsync(rig.Ana, rig, rig.TicketId, [publicUpload.FileId]);
        using var visitor = VisitorClient(rig.Factory, NextIp());
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(AnonymousBody(rig.TypeId)))!;
        body["attachments"] = new[] { new { fileId = noteUpload, uploadKey = Guid.NewGuid() } };
        var inAFiling = await PostJsonAsync(visitor, PublicUrl(rig.Slug, "/tickets"), body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, inANote.StatusCode);
        Assert.Equal(SupportErrorCodes.NoteFileInvalid, await CodeOfAsync(inANote));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inAFiling.StatusCode);
        Assert.Equal(SupportErrorCodes.PublicAttachmentInvalid, await CodeOfAsync(inAFiling));
    }

    [Fact]
    public async Task OpeningOnAClosedTicketIs422()
    {
        var rig = await RigAsync();
        var version = 1L;
        foreach (var status in new[] { "Open", "Resolved", "Closed" })
        {
            var moved = await SupportApiHarness.PutAsync(rig.Ana, $"{rig.Url}/tickets/{rig.TicketId}/status", new { status }, version);
            Assert.True(moved.StatusCode == HttpStatusCode.OK, await BodyAsync(moved));
            version = (await JsonAsync(moved)).GetProperty("version").GetInt64();
        }

        var response = await OpenRawAsync(rig.Ana, rig, rig.TicketId, "acta.pdf", "application/pdf", PdfBytes.Length);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(SupportErrorCodes.NoteTicketClosed, await CodeOfAsync(response));
    }

    [Fact]
    public async Task OpeningAnExeIs422ValidationFailed()
    {
        var rig = await RigAsync();

        var exe = await OpenRawAsync(rig.Ana, rig, rig.TicketId, "virus.exe", "application/octet-stream", 10);
        var tooLarge = await OpenRawAsync(
            rig.Ana, rig, rig.TicketId, "acta.pdf", "application/pdf", SupportPublicAttachmentRules.MaxSizeBytes + 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, exe.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(exe));
        Assert.Contains("Name", await ErrorKeysAsync(exe));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLarge.StatusCode);
        Assert.Equal(["SizeBytes"], await ErrorKeysAsync(tooLarge));
    }

    [Fact]
    public async Task CompletingBeforeThePutIs428()
    {
        var rig = await RigAsync();
        var file = await OpenAsync(rig, rig.Ana, rig.TicketId, "acta.pdf", PdfBytes.Length);

        var response = await CompleteAsync(rig.Ana, rig, rig.TicketId, file);
        var byBruno = await CompleteAsync(rig.Bruno, rig, rig.TicketId, file);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Equal(SupportErrorCodes.PublicAttachmentNotUploaded, await CodeOfAsync(response));
        // Another member's key does not reach the file: the same answer as one that does not exist.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, byBruno.StatusCode);
        Assert.Equal(SupportErrorCodes.NoteFileInvalid, await CodeOfAsync(byBruno));
    }

    [Fact]
    public async Task TheDownloadNeedsTicketViewAndChecksTheNote()
    {
        var rig = await RigAsync();
        var otherTicket = await CreateTicketAsync(rig);
        var file = await UploadAsync(rig, rig.Ana, rig.TicketId, "acta.pdf");
        var otherFile = await UploadAsync(rig, rig.Ana, otherTicket, "otra.pdf");
        var note = await NoteIdAsync(await PostNoteAsync(rig.Ana, rig, rig.TicketId, [file]));
        var otherNote = await NoteIdAsync(await PostNoteAsync(rig.Ana, rig, otherTicket, [otherFile]));
        using var reader = CreateClient(rig.Factory, Guid.CreateVersion7().ToString(), rig.Tenant, SupportPermissions.Read);
        var foreign = NewTenantId();
        await SeedTenantAsync(rig.Factory, foreign);

        var read = await reader.GetAsync(DownloadUrl(rig, rig.TicketId, note, file), Ct);
        var noteOfAnotherTicket = await rig.Ana.GetAsync(DownloadUrl(rig, rig.TicketId, otherNote, otherFile), Ct);
        var fileOfAnotherNote = await rig.Ana.GetAsync(DownloadUrl(rig, rig.TicketId, note, otherFile), Ct);
        var otherTenant = await rig.Ana.GetAsync(
            $"{SupportUrl(foreign)}/tickets/{rig.TicketId}/notes/{note}/files/{file}/download", Ct);

        Assert.True(read.StatusCode == HttpStatusCode.OK, await BodyAsync(read));
        Assert.StartsWith("https://r2.test/files/", (await JsonAsync(read)).GetProperty("url").GetString(), StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(5), host.Storage.LastDownloadExpiry);
        foreach (var response in new[] { noteOfAnotherTicket, fileOfAnotherNote })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(SupportErrorCodes.AttachmentNotFound, await CodeOfAsync(response));
        }

        Assert.Equal(HttpStatusCode.Forbidden, otherTenant.StatusCode);
        Assert.Equal(1, await CountAuditEntriesAsync(rig.Factory, SupportAuditActions.TicketAttachmentDownloaded, rig.TicketId.ToString()));
    }

    // Plan N20 and Review Focus 4: the public form's attachments_busy cap counts uploads no ticket claimed; a note's file is claimed.
    [Fact]
    public async Task NoteFilesDoNotCountAsPendingPublicUploads()
    {
        var rig = await RigAsync();
        var first = await UploadAsync(rig, rig.Ana, rig.TicketId, "a.pdf");
        var second = await UploadAsync(rig, rig.Ana, rig.TicketId, "b.pdf");
        Assert.Equal(2, await CountPendingAsync(rig));

        Assert.Equal(HttpStatusCode.Created, (await PostNoteAsync(rig.Ana, rig, rig.TicketId, [first, second])).StatusCode);

        Assert.Equal(0, await CountPendingAsync(rig));
    }

    // Review Focus 4: Storage's staging sweep keeps a file a note claimed, and only that one.
    [Fact]
    public async Task TheReferenceProbeKeepsANoteFile()
    {
        var rig = await RigAsync();
        var claimed = await UploadAsync(rig, rig.Ana, rig.TicketId, "acta.pdf");
        var unclaimed = await UploadAsync(rig, rig.Ana, rig.TicketId, "suelto.pdf");
        Assert.Equal(HttpStatusCode.Created, (await PostNoteAsync(rig.Ana, rig, rig.TicketId, [claimed])).StatusCode);

        await using var scope = rig.Factory.Services.CreateAsyncScope();
        var probe = Assert.Single(scope.ServiceProvider.GetServices<IFileReferenceProbe>(), candidate => candidate.Source == "support");

        Assert.True(await probe.HasReferencesAsync(claimed, Ct));
        Assert.False(await probe.HasReferencesAsync(unclaimed, Ct));
    }

    private async Task<Rig> RigAsync()
    {
        var factory = await host.StartTestAsync();
        var tenant = NewTenantId();
        var slug = await SeedPublishedTenantAsync(factory, tenant);
        var ana = await MemberClientAsync(factory, tenant, "Ana Asesora");
        var bruno = await MemberClientAsync(factory, tenant, "Bruno Asesor");
        var typeId = await FirstTypeIdAsync(factory, tenant);
        var rig = new Rig(factory, tenant, slug, typeId, ana, bruno, Guid.Empty);
        return rig with { TicketId = await CreateTicketAsync(rig) };
    }

    private static async Task<HttpClient> MemberClientAsync(QepApiFactory factory, string tenant, string name)
    {
        var subject = Guid.CreateVersion7().ToString();
        await SeedCallerMembershipAsync(factory, name, "admin", tenant, subject);
        return CreateClient(factory, subject, tenant, SupportPermissions.Read, SupportPermissions.Manage);
    }

    private static async Task<Guid> CreateTicketAsync(Rig rig)
    {
        var response = await rig.Ana.PostAsJsonAsync($"{rig.Url}/tickets", SupportTicketScenario.Body(rig.TypeId), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> OpenRawAsync(
        HttpClient client, Rig rig, Guid ticketId, string name, string mimeType, long sizeBytes) =>
        client.PostAsJsonAsync($"{rig.Url}/tickets/{ticketId}/notes/attachments", new { name, mimeType, sizeBytes }, Ct);

    private static async Task<Guid> OpenAsync(Rig rig, HttpClient client, Guid ticketId, string name, long sizeBytes)
    {
        var response = await OpenRawAsync(client, rig, ticketId, name, "application/pdf", sizeBytes);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        var session = await JsonAsync(response);
        Assert.Equal("application/pdf", session.GetProperty("headers").GetProperty("Content-Type").GetString());
        Assert.StartsWith("https://r2.test/", session.GetProperty("uploadUrl").GetString(), StringComparison.Ordinal);
        // The note's upload never hands out a key: the owner is derived on the server from ticket and member.
        Assert.False(session.TryGetProperty("uploadKey", out _));
        return session.GetProperty("fileId").GetGuid();
    }

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, Rig rig, Guid ticketId, Guid fileId) =>
        client.PostAsync($"{rig.Url}/tickets/{ticketId}/notes/attachments/{fileId}/complete", null, Ct);

    /// <summary>Opens, "uploads" to the in-memory bucket (what the browser's PUT does) and completes: the file is available.</summary>
    private async Task<Guid> UploadAsync(Rig rig, HttpClient client, Guid ticketId, string name)
    {
        var fileId = await OpenAsync(rig, client, ticketId, name, PdfBytes.Length);
        host.Storage.Put(await StagingKeyAsync(rig, fileId), PdfBytes);
        var completed = await CompleteAsync(client, rig, ticketId, fileId);
        Assert.True(completed.StatusCode == HttpStatusCode.OK, await BodyAsync(completed));
        Assert.Equal("available", (await JsonAsync(completed)).GetProperty("status").GetString());
        return fileId;
    }

    private async Task<(Guid FileId, Guid UploadKey)> UploadPublicAsync(Rig rig, string name)
    {
        using var visitor = VisitorClient(rig.Factory, NextIp());
        var opened = await PostJsonAsync(
            visitor, PublicUrl(rig.Slug, "/attachments"), new { name, mimeType = "application/pdf", sizeBytes = PdfBytes.Length });
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var session = await JsonAsync(opened);
        var fileId = session.GetProperty("fileId").GetGuid();
        var uploadKey = session.GetProperty("uploadKey").GetGuid();
        host.Storage.Put(await StagingKeyAsync(rig, fileId), PdfBytes);
        var completed = await PostJsonAsync(visitor, PublicUrl(rig.Slug, $"/attachments/{fileId}/complete"), new { uploadKey });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        return (fileId, uploadKey);
    }

    private static Task<HttpResponseMessage> PostNoteAsync(HttpClient client, Rig rig, Guid ticketId, IReadOnlyList<Guid> fileIds) =>
        client.PostAsJsonAsync(
            $"{rig.Url}/tickets/{ticketId}/notes", new { clientId = Guid.CreateVersion7(), text = "Con archivos.", fileIds }, Ct);

    private static async Task<Guid> NoteIdAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static string DownloadUrl(Rig rig, Guid ticketId, Guid noteId, Guid fileId) =>
        $"{rig.Url}/tickets/{ticketId}/notes/{noteId}/files/{fileId}/download";

    private static async Task<string> StagingKeyAsync(Rig rig, Guid fileId) =>
        (await TextAsync(rig.Factory, "SELECT storage_key FROM storage.file_resources WHERE id = @id", ("id", fileId)))!;

    private static Task<long> NoteCountAsync(Rig rig) =>
        ScalarAsync(rig.Factory, "SELECT count(*) FROM support.ticket_notes WHERE tenant_id = @tenant", ("tenant", Guid.Parse(rig.Tenant)));

    private static async Task<int> CountPendingAsync(Rig rig)
    {
        await using var scope = rig.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISupportAttachmentStorage>().CountPendingAsync(
            Guid.Parse(rig.Tenant), SupportPublicAttachmentRules.PendingWindow, Ct);
    }

    private sealed record Rig(
        QepApiFactory Factory, string Tenant, string Slug, Guid TypeId, HttpClient Ana, HttpClient Bruno, Guid TicketId)
    {
        public string Url => SupportUrl(Tenant);
    }
}
