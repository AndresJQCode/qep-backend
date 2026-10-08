using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure;
using Modules.Quotations.Infrastructure.Whatsapp;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Spec 2026-10-07, «Reglas de resolución»: sin fila y <c>Shared</c> usan el sender global, <c>Own</c>
/// arma un Zenvia con las credenciales del tenant y <c>Disabled</c> no tiene sender. Una key que no
/// descifra es <c>settings_unreadable</c>, no un 500.
/// </summary>
public sealed class WhatsAppChannelResolverTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private const string TenantToken = "token-del-tenant";
    private const string TenantFrom = "573005556677";
    private const string TenantTemplate = "11111111-2222-3333-4444-555555555555";

    private static readonly WhatsAppQuotationMessage Message = new(
        ToPhone: "3001234567", FullName: "Juan Pérez", OrderNumber: "COT-1",
        Total: 1000m, ValidUntil: new DateOnly(2026, 10, 30), DocumentUrl: "https://assets/x.pdf");

    private sealed record Harness(
        WhatsAppChannelResolver Resolver,
        InMemoryTenantWhatsAppSettingsRepository Repository,
        FakeWhatsAppSecretProtector Protector,
        RecordingWhatsAppSender Shared,
        Capture Zenvia);

    private sealed class Capture
    {
        public string? Token { get; set; }

        public string Json { get; set; } = "{}";
    }

    private sealed class CapturingHandler(Capture capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture.Token = request.Headers.TryGetValues("X-API-TOKEN", out var values) ? values.First() : null;
            capture.Json = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"z1"}""") };
        }
    }

    private static Harness NewResolver()
    {
        var repository = new InMemoryTenantWhatsAppSettingsRepository();
        var protector = new FakeWhatsAppSecretProtector();
        var shared = new RecordingWhatsAppSender();
        var capture = new Capture();
        var resolver = new WhatsAppChannelResolver(
            repository,
            protector,
            shared,
            new ZenviaHttpClient(new HttpClient(new CapturingHandler(capture))),
            Options.Create(new QuotationsOptions()),
            NullLogger<ZenviaWhatsAppSender>.Instance);
        return new Harness(resolver, repository, protector, shared, capture);
    }

    private static TenantWhatsAppSettings Stored(WhatsAppMode mode, ProtectedSecret token)
    {
        var settings = TenantWhatsAppSettings.CreateEmpty(TenantId, Now);
        settings.Configure(WhatsAppMode.Own, WhatsAppProvider.Zenvia, token, null, TenantFrom, TenantTemplate, Now);
        if (mode != WhatsAppMode.Own)
        {
            settings.Configure(mode, null, null, null, null, null, Now);
        }

        return settings;
    }

    [Fact]
    public async Task WithoutARowItIsTheSharedSender()
    {
        var harness = NewResolver();

        var channel = await harness.Resolver.ResolveAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.Equal(WhatsAppMode.Shared, channel.Mode);
        Assert.Same(harness.Shared, channel.Sender);
    }

    // Las credenciales propias guardadas se ignoran en Shared.
    [Fact]
    public async Task SharedIsTheSharedSenderEvenWithAnOwnAccountStored()
    {
        var harness = NewResolver();
        harness.Repository.Add(Stored(WhatsAppMode.Shared, harness.Protector.Seed("k2", TenantToken)));

        var channel = await harness.Resolver.ResolveAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.Equal(WhatsAppMode.Shared, channel.Mode);
        Assert.Same(harness.Shared, channel.Sender);
    }

    [Fact]
    public async Task OwnBuildsAZenviaSenderWithTheTenantCredentials()
    {
        var harness = NewResolver();
        harness.Repository.Add(Stored(WhatsAppMode.Own, harness.Protector.Seed("k1", TenantToken)));

        var channel = await harness.Resolver.ResolveAsync(TenantId, TestContext.Current.CancellationToken);
        Assert.Equal(WhatsAppMode.Own, channel.Mode);
        Assert.NotNull(channel.Sender);
        await channel.Sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken);

        Assert.Equal(TenantToken, harness.Zenvia.Token);
        var body = JsonDocument.Parse(harness.Zenvia.Json).RootElement;
        Assert.Equal(TenantFrom, body.GetProperty("from").GetString());
        Assert.Equal(TenantTemplate, body.GetProperty("contents")[0].GetProperty("templateId").GetString());
        Assert.Null(harness.Shared.Sent);
    }

    [Fact]
    public async Task DisabledHasNoSender()
    {
        var harness = NewResolver();
        harness.Repository.Add(Stored(WhatsAppMode.Disabled, harness.Protector.Seed("k2", TenantToken)));

        var channel = await harness.Resolver.ResolveAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.Equal(WhatsAppMode.Disabled, channel.Mode);
        Assert.Null(channel.Sender);
    }

    [Fact]
    public async Task AnOwnKeyThatCannotBeDecryptedIsSettingsUnreadable()
    {
        var harness = NewResolver();
        harness.Repository.Add(Stored(WhatsAppMode.Own, FakeWhatsAppSecretProtector.Garbage("k1")));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Resolver.ResolveAsync(TenantId, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.whatsapp.settings_unreadable", error.Code);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public async Task AnOwnKeyStoredWithARetiredKeyIsSettingsUnreadable()
    {
        var harness = NewResolver();
        harness.Repository.Add(Stored(WhatsAppMode.Own, harness.Protector.Seed("k1", TenantToken)));
        harness.Protector.KnownKeys.Remove("k1");

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Resolver.ResolveAsync(TenantId, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.whatsapp.settings_unreadable", error.Code);
    }
}
