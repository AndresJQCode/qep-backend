using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Storage.Application;
using Modules.Support.Application;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportPublicHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Los adjuntos del formulario público (plan fase 3, Task 10; F30 a F37). El visitante abre una sesión, sube por la URL firmada,
/// completa con el <c>uploadKey</c> y radica con los pares <c>(fileId, uploadKey)</c>; la consola los ve en el detalle y los baja con
/// una URL firmada. Host propio por clase: el bucket privado es un doble en memoria, además de los dobles de <see cref="SupportPublicHost"/>.
/// </summary>
public sealed class SupportPublicAttachmentsApiTests(SupportPublicAttachmentsHost host) : IClassFixture<SupportPublicAttachmentsHost>
{
    private static readonly byte[] PdfBytes = "%PDF-1.7\nacta de entrega"u8.ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFiledTicketKeepsItsAttachments()
    {
        var rig = await RigAsync();
        var first = await UploadAsync(rig, "acta.pdf");
        var second = await UploadAsync(rig, "foto.pdf");

        var response = await PostJsonAsync(
            rig.Client, rig.TicketsUrl, WithAttachments(NamedBody(rig.TypeId, rig.CityId, Email()), first, second));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await JsonAsync(await CreateReader(rig.Factory, rig.TenantId.ToString())
            .GetAsync($"{SupportUrl(rig.TenantId.ToString())}/tickets/{await TicketIdAsync(rig)}", Ct));
        var attachments = detail.GetProperty("attachments").EnumerateArray().ToList();
        Assert.Equal(["acta.pdf", "foto.pdf"], attachments.Select(item => item.GetProperty("fileName").GetString()).Order());
        Assert.All(attachments, item =>
        {
            Assert.Equal("application/pdf", item.GetProperty("mimeType").GetString());
            Assert.Equal((long)PdfBytes.Length, item.GetProperty("sizeBytes").GetInt64());
        });
    }

    [Fact]
    public async Task AnAnonymousTicketCanAttach()
    {
        var rig = await RigAsync();
        var file = await UploadAsync(rig, "denuncia.pdf");

        var response = await PostJsonAsync(rig.Client, rig.TicketsUrl, WithAttachments(AnonymousBody(rig.TypeId), file));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1L, await ScalarAsync(
            rig.Factory,
            "SELECT count(*) FROM support.ticket_attachments a JOIN support.tickets t ON t.id = a.ticket_id WHERE t.tenant_id = @tenant AND t.is_anonymous",
            ("tenant", rig.TenantId)));
    }

    // Plan F32: el uploadKey es el único vínculo entre la subida anónima y quien la abrió. Uno ajeno responde igual que un id que no
    // existe, al completar y al radicar.
    [Fact]
    public async Task AnAttachmentWithAnotherUploadKeyIsRejectedLikeAnUnknownOne()
    {
        var rig = await RigAsync();
        var file = await UploadAsync(rig, "acta.pdf");
        var stolen = file with { UploadKey = Guid.NewGuid() };
        var unknown = new Attachment(Guid.CreateVersion7(), file.UploadKey);

        var complete = await PostJsonAsync(
            rig.Client, PublicUrl(rig.Slug, $"/attachments/{file.FileId}/complete"), new { uploadKey = stolen.UploadKey });
        var withStolen = await PostJsonAsync(rig.Client, rig.TicketsUrl, WithAttachments(AnonymousBody(rig.TypeId), stolen));
        var withUnknown = await PostJsonAsync(rig.Client, rig.TicketsUrl, WithAttachments(AnonymousBody(rig.TypeId), unknown));

        foreach (var response in new[] { complete, withStolen, withUnknown })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("support.public.attachment_invalid", await CodeOfAsync(response));
        }

        Assert.Equal(
            WithoutTraceId(await JsonAsync(withStolen)), WithoutTraceId(await JsonAsync(withUnknown)));
        Assert.Equal(0L, await TicketCountAsync(rig));
    }

    // Plan F35: el índice único sobre file_id es lo que impide que dos radicaciones reclamen el mismo archivo, y se traduce por
    // nombre al mismo 422. La segunda no gasta radicado.
    [Fact]
    public async Task TheSameFileCannotBeClaimedByTwoTickets()
    {
        var rig = await RigAsync();
        var file = await UploadAsync(rig, "acta.pdf");

        var first = await PostJsonAsync(rig.Client, rig.TicketsUrl, WithAttachments(AnonymousBody(rig.TypeId), file));
        var second = await PostJsonAsync(rig.Client, rig.TicketsUrl, WithAttachments(AnonymousBody(rig.TypeId), file));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("support.public.attachment_invalid", await CodeOfAsync(second));
        Assert.Equal(1L, await TicketCountAsync(rig));
        Assert.Equal("SOP-000002", (await JsonAsync(await PostJsonAsync(rig.Client, rig.TicketsUrl, AnonymousBody(rig.TypeId))))
            .GetProperty("number").GetString());
    }

    [Fact]
    public async Task APendingOrQuarantinedFileIsRejected()
    {
        var rig = await RigAsync();
        var pending = await OpenAsync(rig, "acta.pdf", PdfBytes.Length);
        var quarantined = await OpenAsync(rig, "falso.pdf", 4);
        host.Storage.Put(await StagingKeyAsync(rig, quarantined.FileId), "MZ\0\0"u8.ToArray());

        var completed = await PostJsonAsync(
            rig.Client, PublicUrl(rig.Slug, $"/attachments/{quarantined.FileId}/complete"), new { uploadKey = quarantined.UploadKey });
        var notUploaded = await PostJsonAsync(
            rig.Client, PublicUrl(rig.Slug, $"/attachments/{pending.FileId}/complete"), new { uploadKey = pending.UploadKey });
        var withPending = await PostJsonAsync(rig.Client, rig.TicketsUrl, WithAttachments(AnonymousBody(rig.TypeId), pending));
        var withQuarantined = await PostJsonAsync(rig.Client, rig.TicketsUrl, WithAttachments(AnonymousBody(rig.TypeId), quarantined));

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal("rejected", (await JsonAsync(completed)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.PreconditionRequired, notUploaded.StatusCode);
        foreach (var response in new[] { withPending, withQuarantined })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("support.public.attachment_invalid", await CodeOfAsync(response));
        }

        Assert.Equal(0L, await TicketCountAsync(rig));
    }

    [Fact]
    public async Task SixFilesAreRejected()
    {
        var rig = await RigAsync();
        var files = Enumerable.Range(0, 6).Select(_ => new Attachment(Guid.CreateVersion7(), Guid.NewGuid())).ToArray();

        var response = await PostJsonAsync(rig.Client, rig.TicketsUrl, WithAttachments(AnonymousBody(rig.TypeId), files));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        Assert.Contains("Attachments", await ErrorKeysAsync(response));
    }

    [Fact]
    public async Task AFileOverTenMegabytesIsRejectedAtOpen()
    {
        var rig = await RigAsync();

        var tooLarge = await PostJsonAsync(
            rig.Client,
            PublicUrl(rig.Slug, "/attachments"),
            new { name = "video.pdf", mimeType = "application/pdf", sizeBytes = (10 * 1024 * 1024) + 1 });
        var wrongType = await PostJsonAsync(
            rig.Client, PublicUrl(rig.Slug, "/attachments"), new { name = "virus.exe", mimeType = "application/pdf", sizeBytes = 10 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLarge.StatusCode);
        Assert.Equal(["SizeBytes"], await ErrorKeysAsync(tooLarge));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongType.StatusCode);
        Assert.Contains("Name", await ErrorKeysAsync(wrongType));
        Assert.Equal(0L, await ScalarAsync(
            rig.Factory, "SELECT count(*) FROM storage.file_resources WHERE tenant_id = @tenant", ("tenant", rig.TenantId)));
    }

    // Plan F37: el asesor de Soporte no necesariamente tiene storage.file.read. La descarga pide support.ticket.read o manage
    // (revalidado en el handler) y un miembro de otro tenant recibe 403, nunca 404.
    [Fact]
    public async Task TheDownloadNeedsTicketViewAndAnotherTenantGets403()
    {
        var rig = await RigAsync();
        var file = await UploadAsync(rig, "acta.pdf");
        await PostJsonAsync(rig.Client, rig.TicketsUrl, WithAttachments(AnonymousBody(rig.TypeId), file));
        var tenant = rig.TenantId.ToString();
        var ticketId = await TicketIdAsync(rig);
        var detail = await JsonAsync(await CreateReader(rig.Factory, tenant).GetAsync($"{SupportUrl(tenant)}/tickets/{ticketId}", Ct));
        var attachmentId = detail.GetProperty("attachments")[0].GetProperty("id").GetGuid();
        var url = $"{SupportUrl(tenant)}/tickets/{ticketId}/attachments/{attachmentId}/download";

        var reader = await CreateReader(rig.Factory, tenant).GetAsync(url, Ct);
        var manager = await CreateClient(rig.Factory, SubjectId, tenant, SupportPermissions.Manage).GetAsync(url, Ct);
        var storageOnly = await CreateClient(rig.Factory, SubjectId, tenant, StoragePermissions.FileRead).GetAsync(url, Ct);
        var otherTenant = await CreateClient(rig.Factory, SubjectId, NewTenantId(), SupportPermissions.Read).GetAsync(url, Ct);
        var unknown = await CreateReader(rig.Factory, tenant)
            .GetAsync($"{SupportUrl(tenant)}/tickets/{ticketId}/attachments/{Guid.CreateVersion7()}/download", Ct);

        Assert.Equal(HttpStatusCode.OK, reader.StatusCode);
        Assert.StartsWith("https://r2.test/files/", (await JsonAsync(reader)).GetProperty("url").GetString(), StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(5), host.Storage.LastDownloadExpiry);
        Assert.Equal(HttpStatusCode.OK, manager.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, storageOnly.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("support.attachment.not_found", await CodeOfAsync(unknown));
    }

    // Plan F36: la sonda de Soporte retiene el archivo reclamado para que el barrido de Storage no lo borre; el que nadie reclamó, no.
    [Fact]
    public async Task TheReferenceProbeKeepsAClaimedFile()
    {
        var rig = await RigAsync();
        var claimed = await UploadAsync(rig, "acta.pdf");
        var unclaimed = await UploadAsync(rig, "suelto.pdf");
        await PostJsonAsync(rig.Client, rig.TicketsUrl, WithAttachments(AnonymousBody(rig.TypeId), claimed));

        await using var scope = rig.Factory.Services.CreateAsyncScope();
        var probe = Assert.Single(
            scope.ServiceProvider.GetServices<IFileReferenceProbe>(), candidate => candidate.Source == "support");

        Assert.True(await probe.HasReferencesAsync(claimed.FileId, Ct));
        Assert.False(await probe.HasReferencesAsync(unclaimed.FileId, Ct));
    }

    [Fact]
    public async Task TheUploadRoutesAreNoStoreAndRateLimited()
    {
        var rig = await RigAsync();
        var body = new { name = "acta.pdf", mimeType = "application/pdf", sizeBytes = PdfBytes.Length };

        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 21; i++)
        {
            responses.Add(await PostJsonAsync(rig.Client, PublicUrl(rig.Slug, "/attachments"), body));
        }

        Assert.All(responses.Take(20), response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, responses[^1].StatusCode);
        Assert.All(responses, response => Assert.Equal("no-store", response.Headers.CacheControl?.ToString()));
        // El limitador es propio: subir no gasta la cubeta de radicar.
        Assert.Equal(HttpStatusCode.Created, (await PostJsonAsync(rig.Client, rig.TicketsUrl, AnonymousBody(rig.TypeId))).StatusCode);
    }

    private async Task<Rig> RigAsync()
    {
        var factory = await host.StartTestAsync();
        var tenantId = NewTenantId();
        var slug = await SeedPublishedTenantAsync(factory, tenantId);
        return new Rig(
            factory,
            Guid.Parse(tenantId),
            slug,
            VisitorClient(factory),
            await FirstTypeIdAsync(factory, tenantId),
            await AnyCityIdAsync(factory));
    }

    private static async Task<Attachment> OpenAsync(Rig rig, string name, long sizeBytes)
    {
        var response = await PostJsonAsync(
            rig.Client, PublicUrl(rig.Slug, "/attachments"), new { name, mimeType = "application/pdf", sizeBytes });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = await JsonAsync(response);
        Assert.Equal("application/pdf", session.GetProperty("headers").GetProperty("Content-Type").GetString());
        Assert.StartsWith("https://r2.test/", session.GetProperty("uploadUrl").GetString(), StringComparison.Ordinal);
        return new Attachment(session.GetProperty("fileId").GetGuid(), session.GetProperty("uploadKey").GetGuid());
    }

    /// <summary>Abre, «sube» al bucket en memoria (lo que haría el PUT del navegador) y completa: el archivo queda disponible.</summary>
    private async Task<Attachment> UploadAsync(Rig rig, string name)
    {
        var attachment = await OpenAsync(rig, name, PdfBytes.Length);
        host.Storage.Put(await StagingKeyAsync(rig, attachment.FileId), PdfBytes);
        var completed = await PostJsonAsync(
            rig.Client, PublicUrl(rig.Slug, $"/attachments/{attachment.FileId}/complete"), new { uploadKey = attachment.UploadKey });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal("available", (await JsonAsync(completed)).GetProperty("status").GetString());
        return attachment;
    }

    private static async Task<string> StagingKeyAsync(Rig rig, Guid fileId) =>
        (await TextAsync(rig.Factory, "SELECT storage_key FROM storage.file_resources WHERE id = @id", ("id", fileId)))!;

    private static async Task<Guid> TicketIdAsync(Rig rig) =>
        Guid.Parse((await TextAsync(
            rig.Factory, "SELECT id::text FROM support.tickets WHERE tenant_id = @tenant ORDER BY number LIMIT 1", ("tenant", rig.TenantId)))!);

    private static Task<long> TicketCountAsync(Rig rig) =>
        ScalarAsync(rig.Factory, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant", ("tenant", rig.TenantId));

    /// <summary>El mismo cuerpo de radicado con <c>attachments</c>: se pasa por JSON para no tocar los cuerpos del harness.</summary>
    private static Dictionary<string, object?> WithAttachments(object body, params Attachment[] attachments)
    {
        var copy = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(body))!;
        copy["attachments"] = attachments.Select(item => new { fileId = item.FileId, uploadKey = item.UploadKey }).ToArray();
        return copy;
    }

    private static string WithoutTraceId(JsonElement problem) =>
        JsonSerializer.Serialize(problem.EnumerateObject()
            .Where(property => property.Name != "traceId")
            .ToDictionary(property => property.Name, property => property.Value.ToString()));

    private static string Email() => $"adjuntos-{Guid.NewGuid():N}@example.com";

    private sealed record Rig(
        SupportApiHarness.QepApiFactory Factory, Guid TenantId, string Slug, HttpClient Client, Guid TypeId, Guid CityId)
    {
        public string TicketsUrl => PublicUrl(Slug, "/tickets");
    }

    private sealed record Attachment(Guid FileId, Guid UploadKey);
}

/// <summary><see cref="SupportPublicHost"/> con el bucket privado de Storage en memoria: la URL firmada no sale a R2 y la prueba
/// «sube» el objeto escribiéndolo ahí.</summary>
public sealed class SupportPublicAttachmentsHost : SupportPublicHost
{
    internal InMemorySupportObjectStorage Storage { get; } = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.RemoveAll<IObjectStorage>();
        services.AddSingleton<IObjectStorage>(Storage);
    }
}

/// <summary>El bucket privado en memoria, con la vida de la última descarga firmada.</summary>
internal sealed class InMemorySupportObjectStorage : IObjectStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    public TimeSpan? LastDownloadExpiry { get; private set; }

    public void Put(string key, byte[] content) => _objects[key] = content.ToArray();

    public Task<Uri> CreatePresignedUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://r2.test/{key}"));

    public Task<Uri> CreatePresignedDownloadUrlAsync(string key, string? downloadFileName, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://r2.test/{key}"));

    public Task<Uri> CreatePresignedDownloadUrlAsync(
        string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken)
    {
        LastDownloadExpiry = expiry;
        return Task.FromResult(new Uri($"https://r2.test/{key}"));
    }

    public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<StoredObject?>(_objects.TryGetValue(key, out var content)
            ? new StoredObject(content.LongLength, Convert.ToHexStringLower(SHA256.HashData(content)))
            : null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        _objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task PromoteAsync(string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken)
    {
        _objects[destinationKey] = _objects[sourceKey].ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) => Task.FromResult(_objects[key].ToArray());

    public Task UploadAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        _objects[key] = content.ToArray();
        return Task.CompletedTask;
    }

    public Task UploadAsync(
        string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
