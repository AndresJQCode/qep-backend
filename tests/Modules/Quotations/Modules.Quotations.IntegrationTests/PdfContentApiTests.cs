using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>El contenido configurable del PDF (spec 2026-11-16 §4.2): <c>GET</c> y <c>PUT …/quotations/settings/pdf-content</c>,
/// con los permisos de settings, el HTML saneado y las variables validadas al guardar.</summary>
public sealed class PdfContentApiTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    private const string SettingsRead = "tenancy.settings.read";
    private const string SettingsUpdate = "tenancy.settings.update";
    private const string AuditAction = "quotations.pdf_content.updated";

    private static readonly string[] VariableNames =
        ["empresa", "nit_empresa", "cliente", "numero", "fecha", "vigencia", "asesor", "total"];

    private static string Url(Guid tenantId) => $"/api/v1/tenants/{tenantId}/quotations/settings/pdf-content";

    private static readonly PdfSectionDto Disabled = new(false, true, true, string.Empty, string.Empty);

    [Fact]
    public async Task ATenantWithoutConfigurationGetsThreeDisabledSections()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead);
        using var _ = client;

        var content = await GetAsync(client, tenantId);

        Assert.Equal(Disabled, content.PaymentNotice);
        Assert.Equal(Disabled, content.Terms);
        Assert.Equal(Disabled, content.DeliveryProcess);
        Assert.Equal(VariableNames, content.Variables.Select(variable => variable.Name));
    }

    [Fact]
    public async Task TheVariablesTravelLast()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead);
        using var _ = client;

        using var body = JsonDocument.Parse(
            await client.GetStringAsync(Url(tenantId), TestContext.Current.CancellationToken));

        Assert.Equal(
            ["paymentNotice", "terms", "deliveryProcess", "variables"],
            body.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task PutReplacesTheThreeSectionsAndGetReturnsThem()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var paymentNotice = new PdfSectionDto(true, true, false, "Ten en cuenta:", "<p>Paga a {{empresa}}.</p>");
        var terms = new PdfSectionDto(false, false, true, "Términos", "<p>Vigente hasta {{ vigencia }}.</p>");
        var delivery = new PdfSectionDto(true, true, true, "Entrega", "<ul><li>Uno</li><li>Dos</li></ul>");

        var put = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice, terms, deliveryProcess = delivery }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await put.Content.ReadFromJsonAsync<PdfContentPayload>(TestContext.Current.CancellationToken);
        Assert.Equal((paymentNotice, terms, delivery), (saved!.PaymentNotice, saved.Terms, saved.DeliveryProcess));
        Assert.Equal(VariableNames, saved.Variables.Select(variable => variable.Name));

        var read = await GetAsync(client, tenantId);
        Assert.Equal((paymentNotice, terms, delivery), (read.PaymentNotice, read.Terms, read.DeliveryProcess));

        // Un segundo guardado reemplaza las filas que ya existen.
        var replaced = terms with { Enabled = true, ContentHtml = "<p>Otros términos.</p>" };
        var second = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice = Disabled, terms = replaced, deliveryProcess = Disabled },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var reread = await GetAsync(client, tenantId);
        Assert.Equal((Disabled, replaced, Disabled), (reread.PaymentNotice, reread.Terms, reread.DeliveryProcess));
    }

    [Fact]
    public async Task PutSanitizesTheHtml()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var terms = new PdfSectionDto(true, true, true, "Términos", "<p>x<script>alert(1)</script></p>");

        var put = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice = Disabled, terms, deliveryProcess = Disabled }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("<p>x</p>", (await GetAsync(client, tenantId)).Terms.ContentHtml);
    }

    [Fact]
    public async Task AnUnknownVariableIs422AndSavesNothing()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var before = await GetAsync(client, tenantId);
        var terms = new PdfSectionDto(true, true, true, "Términos", "<p>{{ciudad}}</p>");

        var put = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice = Disabled, terms, deliveryProcess = Disabled }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal("quotation.pdf_content.unknown_variable", await CodeOfAsync(put));
        var after = await GetAsync(client, tenantId);
        Assert.Equal(
            (before.PaymentNotice, before.Terms, before.DeliveryProcess),
            (after.PaymentNotice, after.Terms, after.DeliveryProcess));
    }

    [Fact]
    public async Task AnEnabledSectionWithoutTextIs422()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var terms = new PdfSectionDto(true, true, true, "Términos", "<p> </p>");

        var put = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice = Disabled, terms, deliveryProcess = Disabled }, TestContext.Current.CancellationToken);

        await AssertValidationErrorAsync(put, "Terms.ContentHtml");
    }

    [Fact]
    public async Task ContentLongerThanTheLimitAfterSanitizingIs422()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var terms = new PdfSectionDto(true, true, true, "Términos", "<p>" + new string('a', 20_001));

        var put = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice = Disabled, terms, deliveryProcess = Disabled }, TestContext.Current.CancellationToken);

        await AssertValidationErrorAsync(put, "Terms.ContentHtml");
    }

    [Fact]
    public async Task ATitleLongerThan120Is422()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var paymentNotice = new PdfSectionDto(false, true, true, new string('t', 121), string.Empty);

        var put = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice, terms = Disabled, deliveryProcess = Disabled }, TestContext.Current.CancellationToken);

        await AssertValidationErrorAsync(put, "PaymentNotice.Title");
    }

    [Fact]
    public async Task AMissingSectionIs422()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;

        var put = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice = Disabled, terms = Disabled }, TestContext.Current.CancellationToken);

        await AssertValidationErrorAsync(put, "DeliveryProcess");
    }

    [Fact]
    public async Task PutWithoutSettingsUpdateIs403()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead);
        using var _ = client;

        var put = await client.PutAsJsonAsync(
            Url(tenantId),
            new { paymentNotice = Disabled, terms = Disabled, deliveryProcess = Disabled },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task ThePutIsAuditedWithFieldNamesOnly()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        const string Secret = "Texto-que-no-se-audita";
        var terms = new PdfSectionDto(true, true, true, $"Título {Secret}", $"<p>{Secret}</p>");

        var put = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice = Disabled, terms, deliveryProcess = Disabled }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var audit = Assert.Single(
            await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"),
            message => ActionOf(message) == AuditAction);
        Assert.DoesNotContain(Secret, audit.PayloadJson, StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(audit.PayloadJson);
        Assert.Equal(tenantId.ToString(), payload.RootElement.GetProperty("resourceId").GetString());
        // Cambiaron el estado, el título y el contenido de Términos; las otras dos secciones quedaron como estaban.
        Assert.Contains("Terms.Enabled", audit.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("Terms.Title", audit.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("Terms.ContentHtml", audit.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("PaymentNotice", audit.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("DeliveryProcess", audit.PayloadJson, StringComparison.Ordinal);
    }

    private static async Task<PdfContentPayload> GetAsync(HttpClient client, Guid tenantId) =>
        (await client.GetFromJsonAsync<PdfContentPayload>(Url(tenantId), TestContext.Current.CancellationToken))!;

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.GetProperty("code").GetString();
    }

    private static async Task AssertValidationErrorAsync(HttpResponseMessage response, string key)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("validation.failed", body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty(key, out _), $"errors has no {key}");
    }

    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }

    private sealed record VariablePayload(string Name, string Label, string Example);

    private sealed record PdfContentPayload(
        PdfSectionDto PaymentNotice,
        PdfSectionDto Terms,
        PdfSectionDto DeliveryProcess,
        IReadOnlyList<VariablePayload> Variables);
}
