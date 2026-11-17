using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-10-29 §8.3, P-4 y D-A16: el borrador de cotización que crea un agente IA sin sesión. Nace en borrador con la asesora y la
/// cuenta del agente, precia contra el catálogo, rechaza al cliente que no se puede cotizar con el código de la pantalla y es idempotente
/// por clave externa.
/// </summary>
public sealed class ExternalQuotationServiceTests
{
    private static string QuotationsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/quotations";

    [Fact]
    public async Task ADraftIsCreatedWithTheGivenAdvisorAndAccountAndAuditedByTheIntegration()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await ExternalOrderServiceTests.OwnerMembershipIdAsync(factory, tenantId, ownerUserId);

        var result = await CreateAsync(factory, Draft(tenantId, customerId, advisorId, billing, variantId, "ai:job-1:call-1"));

        Assert.False(result.AlreadyExisted);
        Assert.Equal(advisorId, result.AdvisorMembershipId);
        var quotation = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{result.QuotationId}", TestContext.Current.CancellationToken);
        Assert.NotNull(quotation);
        Assert.Equal(
            (result.QuotationNumber, "Draft", customerId, advisorId, "Pedido por WhatsApp."),
            (quotation.QuotationNumber, quotation.Status, quotation.ClientId, quotation.AdvisorId, quotation.Notes));
        Assert.Equal(variantId, Assert.Single(quotation.Items).VariantId);
        var audit = Assert.Single(
            await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"),
            message => message.PayloadJson.Contains("quotation.quotation.created", StringComparison.Ordinal));
        using var payload = JsonDocument.Parse(audit.PayloadJson);
        Assert.Equal("Integration", payload.RootElement.GetProperty("actorType").GetString());
    }

    // D-A16: un trabajo que se repite con la misma clave devuelve la cotización que ya creó, sin gastar otro número.
    [Fact]
    public async Task TheSameExternalKeyReturnsTheExistingQuotation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await ExternalOrderServiceTests.OwnerMembershipIdAsync(factory, tenantId, ownerUserId);
        var draft = Draft(tenantId, customerId, advisorId, billing, variantId, "ai:job-2:call-1");

        var first = await CreateAsync(factory, draft);
        var second = await CreateAsync(factory, draft);
        var both = await Task.WhenAll(
            CreateAsync(factory, draft with { ExternalKey = "ai:job-3:call-1" }),
            CreateAsync(factory, draft with { ExternalKey = "ai:job-3:call-1" }));

        Assert.Equal((first.QuotationId, first.QuotationNumber, true), (second.QuotationId, second.QuotationNumber, second.AlreadyExisted));
        Assert.Equal(both[0].QuotationId, both[1].QuotationId);
        var page = await client.GetFromJsonAsync<QuotationsPageResponse>(QuotationsUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.Equal(2, page.Total);
    }

    // P-4 y el orden de CLAUDE.md: el incompleto se rechaza con client_incomplete, antes que client_cuc_missing, y no queda nada.
    [Fact]
    public async Task AnIncompleteCustomerIsRejectedWithClientIncompleteAndNothingIsWritten()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        await IncompleteCustomerQuotationApiTests.MarkIncompleteAsync(database.GetConnectionString(), customerId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await ExternalOrderServiceTests.OwnerMembershipIdAsync(factory, tenantId, ownerUserId);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(
            () => CreateAsync(factory, Draft(tenantId, customerId, advisorId, billing, variantId, "ai:job-4:call-1")));

        Assert.Equal("quotation.quotation.client_incomplete", error.Code);
        var page = await client.GetFromJsonAsync<QuotationsPageResponse>(QuotationsUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.Equal(0, page.Total);
    }

    private static ExternalQuotationDraft Draft(
        Guid tenantId,
        Guid customerId,
        Guid advisorId,
        (Guid CompanyId, string BankName, string AccountNumber, string Currency) billing,
        Guid variantId,
        string key) =>
        new(
            tenantId, customerId, advisorId,
            new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
            [new QuotationItemAddition(variantId, 3m)],
            "Pedido por WhatsApp.",
            key);

    private static async Task<ExternalQuotationResult> CreateAsync(QepApiFactory factory, ExternalQuotationDraft draft)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IExternalQuotationWriter>()
            .CreateDraftAsync(draft, TestContext.Current.CancellationToken);
    }
}
