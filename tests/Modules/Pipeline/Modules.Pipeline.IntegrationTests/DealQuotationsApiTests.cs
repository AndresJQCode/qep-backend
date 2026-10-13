using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 §4.4, D18 y P11 del plan: enlazar a mano una cotización que quedó suelta, elegir la principal y el
/// monto de sólo lectura. Las fotos se siembran con el mismo <see cref="QuotationEventApplier"/> que usa el worker.</summary>
public sealed class DealQuotationsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SentAsync(QepApiFactory factory, Guid tenantId, Guid clientId, Guid quotationId, decimal total)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<QuotationEventApplier>().ApplySentAsync(
            new QuotationFact(tenantId, quotationId, $"COT-{total}", clientId, Guid.CreateVersion7(), total, "COP", null), DateTimeOffset.UtcNow, Ct);
        await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static async Task<DealDetailDto> CreateDealAsync(HttpClient client, Guid tenantId, Guid customerId, string title)
    {
        var response = await client.PostAsJsonAsync($"{PipelineUrl(tenantId)}/deals", new CreateDealRequest(customerId, title, null, null, null, null), Ct);
        return (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid tenantId, Guid dealId, Guid quotationId, string action, long version) =>
        client.PostAsJsonAsync($"{PipelineUrl(tenantId)}/deals/{dealId}/quotations/{quotationId}/{action}", new DealVersionRequest(version), Ct);

    // Con dos negocios abiertos la cotización queda suelta (§4.4); el asesor la enlaza y el monto pasa a seguirla (D18).
    [Fact]
    public async Task LinkingALooseQuotationMakesItPrimaryAndTheAmountReadOnly()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var (customerId, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var first = await CreateDealAsync(client, tenant.TenantId, customerId, "Uno");
        await CreateDealAsync(client, tenant.TenantId, customerId, "Dos");
        var quotationId = Guid.CreateVersion7();
        await SentAsync(factory, tenant.TenantId, customerId, quotationId, 1_000m);

        var before = (await client.GetFromJsonAsync<DealDetailDto>($"{PipelineUrl(tenant.TenantId)}/deals/{first.Id}", Ct))!;
        var linked = await PostAsync(client, tenant.TenantId, first.Id, quotationId, "link", before.Version);
        var detail = (await linked.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
        var patch = await client.PatchAsJsonAsync($"{PipelineUrl(tenant.TenantId)}/deals/{first.Id}",
            new UpdateDealRequest("Uno", detail.OwnerMemberId, 5_000m, "COP", null, detail.Version), Ct);

        Assert.Equal(quotationId, Assert.Single(before.UnlinkedCustomerQuotations).QuotationId);
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Equal(((Guid?)quotationId, (decimal?)1_000m, false), (detail.PrimaryQuotationId, detail.Amount, detail.AmountIsEditable));
        Assert.True(Assert.Single(detail.Quotations).IsPrimary);
        Assert.Empty(detail.UnlinkedCustomerQuotations);
        Assert.Equal(PipelineErrorCodes.DealAmountFollowsQuotation, (await ProblemAsync(patch)).Code);
    }

    [Fact]
    public async Task ForeignUnknownAndUnlinkedQuotationsAre422()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var (customerId, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        var (otherCustomer, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Pedro");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var deal = await CreateDealAsync(client, tenant.TenantId, customerId, "Uno");
        await CreateDealAsync(client, tenant.TenantId, otherCustomer, "Otro A");
        await CreateDealAsync(client, tenant.TenantId, otherCustomer, "Otro B");
        var foreignQuotation = Guid.CreateVersion7();
        await SentAsync(factory, tenant.TenantId, otherCustomer, foreignQuotation, 700m);

        var mismatch = await PostAsync(client, tenant.TenantId, deal.Id, foreignQuotation, "link", deal.Version);
        var unknown = await PostAsync(client, tenant.TenantId, deal.Id, Guid.CreateVersion7(), "link", deal.Version);
        var notLinked = await PostAsync(client, tenant.TenantId, deal.Id, foreignQuotation, "primary", deal.Version);

        Assert.Equal(PipelineErrorCodes.DealQuotationCustomerMismatch, (await ProblemAsync(mismatch)).Code);
        Assert.Equal(PipelineErrorCodes.DealQuotationNotFound, (await ProblemAsync(unknown)).Code);
        Assert.Equal(PipelineErrorCodes.DealQuotationNotLinked, (await ProblemAsync(notLinked)).Code);
    }

    // P11: enlazar una cotización que estaba en otro negocio la mueve; ése pierde la principal y conserva el monto (D27).
    [Fact]
    public async Task LinkingMovesTheQuotationAndPrimaryChangesTheAmount()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var (customerId, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var first = await CreateDealAsync(client, tenant.TenantId, customerId, "Uno");
        var firstQuotation = Guid.CreateVersion7();
        await SentAsync(factory, tenant.TenantId, customerId, firstQuotation, 1_000m);
        var second = await CreateDealAsync(client, tenant.TenantId, customerId, "Dos");
        var secondQuotation = Guid.CreateVersion7();
        await SentAsync(factory, tenant.TenantId, customerId, secondQuotation, 2_000m);
        var dealsUrl = $"{PipelineUrl(tenant.TenantId)}/deals";
        second = (await client.GetFromJsonAsync<DealDetailDto>($"{dealsUrl}/{second.Id}", Ct))!;

        var moved = (await (await PostAsync(client, tenant.TenantId, second.Id, firstQuotation, "link", second.Version)).Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
        var firstAfter = (await client.GetFromJsonAsync<DealDetailDto>($"{dealsUrl}/{first.Id}", Ct))!;
        var linkedSecond = await PostAsync(client, tenant.TenantId, second.Id, secondQuotation, "link", moved.Version);
        var afterLink = (await linkedSecond.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
        var primary = (await (await PostAsync(client, tenant.TenantId, second.Id, secondQuotation, "primary", afterLink.Version)).Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;

        Assert.Equal(((Guid?)firstQuotation, (decimal?)1_000m), (moved.PrimaryQuotationId, moved.Amount));
        Assert.Equal(((Guid?)null, (decimal?)1_000m, true), (firstAfter.PrimaryQuotationId, firstAfter.Amount, firstAfter.AmountIsEditable));
        Assert.Equal(((Guid?)secondQuotation, (decimal?)2_000m), (primary.PrimaryQuotationId, primary.Amount));
        Assert.Equal(2, primary.Quotations.Count);
        // §6.3: el enlace manual audita como el aplicador. El negocio que perdió la cotización, una vez; el que la ganó, la
        // principal dos veces (el primer link, que la hizo principal, y el primary explícito) y dos enlaces.
        Assert.Equal(1L, await AuditCountAsync(connectionString, tenant.TenantId, PipelineAuditActions.QuotationUnlinked, first.Id));
        Assert.Equal(2L, await AuditCountAsync(connectionString, tenant.TenantId, PipelineAuditActions.PrimaryQuotationSet, second.Id));
        Assert.Equal(2L, await AuditCountAsync(connectionString, tenant.TenantId, PipelineAuditActions.QuotationLinked, second.Id));
    }

    private static Task<long> AuditCountAsync(string connectionString, Guid tenantId, string action, Guid dealId) =>
        CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = @a AND resource_id = @r",
            ("t", tenantId), ("a", action), ("r", dealId.ToString()));
}
