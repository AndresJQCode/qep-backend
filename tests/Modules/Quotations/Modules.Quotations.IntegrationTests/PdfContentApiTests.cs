using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Infrastructure.RichText;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    // Lo que lee un tenant sin fila en quotations.pdf_payment_account (spec 2026-11-16 pdf-payment-account, decisión 1).
    private static readonly PdfPaymentAccountDto AccountOff = new(false, true, true);

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
            ["paymentNotice", "terms", "deliveryProcess", "paymentAccount", "variables"],
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
            Url(tenantId), new { paymentNotice, terms, deliveryProcess = delivery, paymentAccount = AccountOff }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await put.Content.ReadFromJsonAsync<PdfContentPayload>(TestContext.Current.CancellationToken);
        Assert.Equal((paymentNotice, terms, delivery), (saved!.PaymentNotice, saved.Terms, saved.DeliveryProcess));
        Assert.Equal(VariableNames, saved.Variables.Select(variable => variable.Name));

        var read = await GetAsync(client, tenantId);
        Assert.Equal((paymentNotice, terms, delivery), (read.PaymentNotice, read.Terms, read.DeliveryProcess));

        // Un segundo guardado reemplaza las filas que ya existen.
        var replaced = terms with { Enabled = true, ContentHtml = "<p>Otros términos.</p>" };
        var second = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice = Disabled, terms = replaced, deliveryProcess = Disabled, paymentAccount = AccountOff },
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
            Url(tenantId), new { paymentNotice = Disabled, terms, deliveryProcess = Disabled, paymentAccount = AccountOff }, TestContext.Current.CancellationToken);

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
            Url(tenantId), new { paymentNotice = Disabled, terms, deliveryProcess = Disabled, paymentAccount = AccountOff }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal("quotation.pdf_content.unknown_variable", await CodeOfAsync(put));
        // El error de dominio marca el campo con la clave del validador, para que la consola pinte el input.
        using (var body = JsonDocument.Parse(await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("Terms.ContentHtml", out var _), body.RootElement.ToString());
        }

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
            Url(tenantId), new { paymentNotice = Disabled, terms, deliveryProcess = Disabled, paymentAccount = AccountOff }, TestContext.Current.CancellationToken);

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
            Url(tenantId), new { paymentNotice = Disabled, terms, deliveryProcess = Disabled, paymentAccount = AccountOff }, TestContext.Current.CancellationToken);

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
            Url(tenantId), new { paymentNotice, terms = Disabled, deliveryProcess = Disabled, paymentAccount = AccountOff }, TestContext.Current.CancellationToken);

        await AssertValidationErrorAsync(put, "PaymentNotice.Title");
    }

    [Fact]
    public async Task AMissingSectionIs422()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;

        var put = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice = Disabled, terms = Disabled, paymentAccount = AccountOff }, TestContext.Current.CancellationToken);

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
            new { paymentNotice = Disabled, terms = Disabled, deliveryProcess = Disabled, paymentAccount = AccountOff },
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
            Url(tenantId), new { paymentNotice = Disabled, terms, deliveryProcess = Disabled, paymentAccount = AccountOff }, TestContext.Current.CancellationToken);

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

    // El rechazo del saneador (demasiadas etiquetas) no esconde el error del validador en otra sección: el formulario marca los dos.
    [Fact]
    public async Task ASanitizerRejectionDoesNotHideOtherErrors()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var paymentNotice = new PdfSectionDto(false, true, true, new string('t', 121), string.Empty);
        var terms = new PdfSectionDto(
            true, true, true, "Términos", string.Concat(Enumerable.Repeat("<i></i>", RichTextHtmlSanitizer.MaxElements + 1)));

        var put = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice, terms, deliveryProcess = Disabled, paymentAccount = AccountOff }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        using var body = JsonDocument.Parse(await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("validation.failed", body.RootElement.GetProperty("code").GetString());
        var errors = body.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("PaymentNotice.Title", out var _), errors.ToString());
        Assert.True(errors.TryGetProperty("Terms.ContentHtml", out var _), errors.ToString());
    }

    [Fact]
    public async Task AnUnchangedPutIsNotAudited()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var terms = new PdfSectionDto(true, true, true, "Términos", "<p>Sin cambios.</p>");
        var body = new { paymentNotice = Disabled, terms, deliveryProcess = Disabled, paymentAccount = AccountOff };

        var first = await client.PutAsJsonAsync(Url(tenantId), body, TestContext.Current.CancellationToken);
        var second = await client.PutAsJsonAsync(Url(tenantId), body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        // El segundo guardado responde igual, pero no cambió nada: no deja una entrada de auditoría vacía.
        Assert.Single(
            await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"),
            message => ActionOf(message) == AuditAction && ResourceOf(message) == tenantId.ToString());
    }

    [Fact]
    public async Task GetReturnsThePaymentAccountOffForATenantWithoutRow()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead);
        using var _ = client;

        using var body = JsonDocument.Parse(
            await client.GetStringAsync(Url(tenantId), TestContext.Current.CancellationToken));

        var account = body.RootElement.GetProperty("paymentAccount");
        Assert.False(account.GetProperty("enabled").GetBoolean());
        Assert.True(account.GetProperty("inQuotation").GetBoolean());
        Assert.True(account.GetProperty("inOrder").GetBoolean());
    }

    // Sin paymentAccount el cuerpo es un 422, no una cuenta apagada en silencio: así un cliente viejo no la apaga sin saberlo.
    [Fact]
    public async Task PutWithoutPaymentAccountIs422()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;

        var put = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice = Disabled, terms = Disabled, deliveryProcess = Disabled },
            TestContext.Current.CancellationToken);

        await AssertValidationErrorAsync(put, "PaymentAccount");
    }

    [Fact]
    public async Task PutSavesAndReturnsThePaymentAccount()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var account = new PdfPaymentAccountDto(true, false, true);

        var put = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice = Disabled, terms = Disabled, deliveryProcess = Disabled, paymentAccount = account },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(account, (await put.Content.ReadFromJsonAsync<PdfContentPayload>(TestContext.Current.CancellationToken))!.PaymentAccount);
        Assert.Equal(account, (await GetAsync(client, tenantId)).PaymentAccount);

        // Un segundo guardado reemplaza la fila que ya existe.
        var replaced = new PdfPaymentAccountDto(false, true, false);
        var second = await client.PutAsJsonAsync(
            Url(tenantId), new { paymentNotice = Disabled, terms = Disabled, deliveryProcess = Disabled, paymentAccount = replaced },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(replaced, (await GetAsync(client, tenantId)).PaymentAccount);
    }

    [Fact]
    public async Task APutThatOnlyFlipsTheAccountAuditsItsFields()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;

        var put = await client.PutAsJsonAsync(
            Url(tenantId),
            new { paymentNotice = Disabled, terms = Disabled, deliveryProcess = Disabled, paymentAccount = AccountOff with { Enabled = true } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var audit = Assert.Single(
            await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"),
            message => ActionOf(message) == AuditAction && ResourceOf(message) == tenantId.ToString());
        Assert.Contains("PaymentAccount.Enabled", audit.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("PaymentAccount.InQuotation", audit.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("PaymentAccount.InOrder", audit.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("PaymentNotice", audit.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Terms", audit.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("DeliveryProcess", audit.PayloadJson, StringComparison.Ordinal);
    }

    // Spec 2026-11-16 pdf-payment-account, decisión 5: la huella del PDF no incluye la cuenta, así que cambiar si la cotización la
    // imprime vence a mano los PDF cacheados de ese tenant (content_fingerprint en null), en la misma transacción del PUT.
    [Fact]
    public async Task PutChangingQuotationAccountVisibilityInvalidatesCachedPdfs()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, client, _) = await TenantWithACachedQuotationPdfAsync(factory);
        using var _ = client;
        var (otherTenantId, otherClient, _) = await TenantWithACachedQuotationPdfAsync(factory);
        using var __ = otherClient;
        var otherFingerprint = await CachedFingerprintAsync(factory, otherTenantId);
        Assert.NotNull(otherFingerprint);

        var put = await PutAccountAsync(client, tenantId, AccountOff with { Enabled = true });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Null(await CachedFingerprintAsync(factory, tenantId));
        Assert.Equal(otherFingerprint, await CachedFingerprintAsync(factory, otherTenantId));
    }

    // Los pedidos no tienen PDF cacheado: cambiar sólo InOrder no vence nada.
    [Fact]
    public async Task PutChangingOnlyInOrderKeepsCachedPdfs()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, client, quotationId) = await TenantWithACachedQuotationPdfAsync(factory);
        using var _ = client;
        Assert.Equal(HttpStatusCode.OK, (await PutAccountAsync(client, tenantId, new PdfPaymentAccountDto(true, true, true))).StatusCode);
        // El primer PUT prendió la cuenta y venció el PDF: se regenera para tener otra vez una huella que cuidar.
        await ExportQuotationPdfAsync(client, tenantId, quotationId);
        var fingerprint = await CachedFingerprintAsync(factory, tenantId);
        Assert.NotNull(fingerprint);

        var put = await PutAccountAsync(client, tenantId, new PdfPaymentAccountDto(true, true, false));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(fingerprint, await CachedFingerprintAsync(factory, tenantId));
    }

    [Fact]
    public async Task PutWithoutChangesKeepsCachedPdfs()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, client, _) = await TenantWithACachedQuotationPdfAsync(factory);
        using var _ = client;
        var fingerprint = await CachedFingerprintAsync(factory, tenantId);
        Assert.NotNull(fingerprint);

        var put = await PutAccountAsync(client, tenantId, AccountOff);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(fingerprint, await CachedFingerprintAsync(factory, tenantId));
    }

    private static async Task<(Guid TenantId, HttpClient Client, Guid QuotationId)> TenantWithACachedQuotationPdfAsync(QepApiFactory factory)
    {
        var (tenantId, _, client) = await RegisterTenantAsync(factory, [.. ManagerPermissions, SettingsRead, SettingsUpdate]);
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, customerId);
        await ExportQuotationPdfAsync(client, tenantId, quotation.Id);
        return (tenantId, client, quotation.Id);
    }

    private static async Task ExportQuotationPdfAsync(HttpClient client, Guid tenantId, Guid quotationId)
    {
        var export = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/pdf", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
    }

    private static async Task<string?> CachedFingerprintAsync(QepApiFactory factory, Guid tenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        return await dbContext.QuotationPdfs
            .AsNoTracking()
            .Where(pdf => pdf.TenantId == tenantId)
            .Select(pdf => pdf.ContentFingerprint)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> PutAccountAsync(HttpClient client, Guid tenantId, PdfPaymentAccountDto account) =>
        client.PutAsJsonAsync(
            Url(tenantId),
            new { paymentNotice = Disabled, terms = Disabled, deliveryProcess = Disabled, paymentAccount = account },
            TestContext.Current.CancellationToken);

    private static string? ResourceOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("resourceId").GetString();
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
        PdfPaymentAccountDto PaymentAccount,
        IReadOnlyList<VariablePayload> Variables);
}
