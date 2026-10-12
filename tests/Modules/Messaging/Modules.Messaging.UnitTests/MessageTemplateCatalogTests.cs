using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Meta;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §5.1: el catálogo normalizado se guarda 5 minutos por <c>(tenant, conexión)</c>; <c>refresh</c> se lo salta; una
/// conexión que no es del tenant o no está Active no lo lee (ni de la caché); una lista que Meta no contesta no se guarda.</summary>
public sealed class MessageTemplateCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();

    private static JsonElement Template(string name, string status = "APPROVED") =>
        JsonDocument.Parse($$$"""{"name":"{{{name}}}","language":"es","status":"{{{status}}}","category":"UTILITY","components":[{"type":"BODY","text":"Hola {{1}}"}]}""").RootElement.Clone();

    private sealed record Rig(MessageTemplateCatalog Catalog, FakeWhatsAppClient Meta, FakeConnectionDirectory Connections);

    private static Rig NewRig(params JsonElement[] templates)
    {
        var meta = new FakeWhatsAppClient { NextTemplates = new MessagingGraphResult<IReadOnlyList<JsonElement>>(templates, null) };
        var connections = new FakeConnectionDirectory();
        return new Rig(new MessageTemplateCatalog(connections, meta, new MemoryCache(new MemoryCacheOptions())), meta, connections);
    }

    [Fact]
    public async Task OnlyApprovedTemplatesComeOutNormalized()
    {
        var rig = NewRig(Template("b"), Template("a"), Template("c", "PAUSED"));

        var catalog = await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);

        Assert.Equal(["a", "b"], catalog.Select(template => template.Name));
        Assert.Equal(["1"], catalog[0].Parameters);
    }

    [Fact]
    public async Task TheSecondReadWithinTheLifetimeComesFromTheCache()
    {
        var rig = NewRig(Template("a"));

        await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);
        await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);

        Assert.Equal(1, rig.Meta.TemplateListCalls);
    }

    [Fact]
    public async Task RefreshSkipsTheCacheAndReplacesIt()
    {
        var rig = NewRig(Template("a"));
        await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);
        rig.Meta.NextTemplates = new MessagingGraphResult<IReadOnlyList<JsonElement>>([Template("a"), Template("z")], null);

        var refreshed = await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: true, Ct);
        var afterwards = await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);

        Assert.Equal(2, rig.Meta.TemplateListCalls);
        Assert.Equal(["a", "z"], refreshed.Select(template => template.Name));
        Assert.Equal(["a", "z"], afterwards.Select(template => template.Name));
    }

    [Fact]
    public async Task EachTenantAndConnectionHasItsOwnEntry()
    {
        var rig = NewRig(Template("a"));

        await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);
        await rig.Catalog.ListAsync(TenantId, Guid.CreateVersion7(), refresh: false, Ct);
        await rig.Catalog.ListAsync(Guid.CreateVersion7(), ConnectionId, refresh: false, Ct);

        Assert.Equal(3, rig.Meta.TemplateListCalls);
    }

    [Fact]
    public void TheLifetimeIsFiveMinutes() => Assert.Equal(TimeSpan.FromMinutes(5), MessageTemplateCatalog.Lifetime);

    [Fact]
    public async Task AConnectionThatIsNotActiveIsUnavailableEvenIfCached()
    {
        var rig = NewRig(Template("a"));
        await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);
        rig.Connections.TemplateSource = null;

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, error.Code);
        Assert.Equal(1, rig.Meta.TemplateListCalls);
    }

    [Fact]
    public async Task AListMetaDoesNotAnswerIsUnavailableAndIsNotCached()
    {
        var rig = NewRig();
        rig.Meta.NextTemplates = new MessagingGraphResult<IReadOnlyList<JsonElement>>(null, new MessagingGraphFailure(0, null, "timeout"));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct));
        rig.Meta.NextTemplates = new MessagingGraphResult<IReadOnlyList<JsonElement>>([Template("a")], null);
        var retried = await rig.Catalog.ListAsync(TenantId, ConnectionId, refresh: false, Ct);

        Assert.Equal(MessagingErrorCodes.TemplatesUnavailable, error.Code);
        Assert.Equal("a", Assert.Single(retried).Name);
        Assert.Equal(2, rig.Meta.TemplateListCalls);
    }
}
