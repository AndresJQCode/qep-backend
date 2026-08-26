using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Expiration;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// US-19: vencimiento automático. Invoca <see cref="IQuotationExpirationProcessor"/> directo
/// (resuelto del contenedor de <c>QepApiFactory</c>) en vez de esperar al temporizador de
/// <c>QuotationExpirationWorker</c> -- el intervalo real es de una hora por defecto, y esperarlo
/// de verdad haría la prueba lenta o forzaría una configuración de prueba aparte para el
/// temporizador. Lo que importa verificar es la consulta y la transición, no el reloj.
/// </summary>
public sealed class QuotationExpirationApiTests
{
    [Fact]
    public async Task SweepExpiresASentQuotationPastItsValidUntil()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var pdfFileId = await CreateAvailablePdfFileAsync(client, factory, tenantId);
        await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/send",
            new SendQuotationRequest(pdfFileId),
            TestContext.Current.CancellationToken);
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        await client.PatchAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}",
            new UpdateQuotationRequest(yesterday, null, null, null),
            TestContext.Current.CancellationToken);

        var expiredCount = await RunExpirationSweepAsync(factory);

        Assert.True(expiredCount >= 1);
        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal("Expired", fetched.Status);
    }

    [Fact]
    public async Task SweepDoesNotTouchASentQuotationWhoseValidUntilHasNotPassed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var pdfFileId = await CreateAvailablePdfFileAsync(client, factory, tenantId);
        await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/send",
            new SendQuotationRequest(pdfFileId),
            TestContext.Current.CancellationToken);
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        await client.PatchAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}",
            new UpdateQuotationRequest(tomorrow, null, null, null),
            TestContext.Current.CancellationToken);

        await RunExpirationSweepAsync(factory);

        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal("Sent", fetched.Status);
    }

    // Sólo Sent vence: un borrador con valid_until pasado se queda como está hasta que la
    // asesora decida que hacer con él.
    [Fact]
    public async Task SweepDoesNotTouchADraftQuotation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        await client.PatchAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}",
            new UpdateQuotationRequest(yesterday, null, null, null),
            TestContext.Current.CancellationToken);

        await RunExpirationSweepAsync(factory);

        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal("Draft", fetched.Status);
    }

    private static async Task<int> RunExpirationSweepAsync(QepApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IQuotationExpirationProcessor>();
        return await processor.ExpirePastDueQuotationsAsync(TestContext.Current.CancellationToken);
    }
}
