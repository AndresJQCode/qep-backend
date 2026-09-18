using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Expiration;
using Modules.Quotations.Infrastructure.Persistence;
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
        var yesterday = TodayInBogota().AddDays(-1);
        await client.PatchAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}",
            new UpdateQuotationRequest(yesterday, null, null, null, null),
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
        var tomorrow = TodayInBogota().AddDays(1);
        await client.PatchAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}",
            new UpdateQuotationRequest(tomorrow, null, null, null, null),
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
        var yesterday = TodayInBogota().AddDays(-1);
        await client.PatchAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}",
            new UpdateQuotationRequest(yesterday, null, null, null, null),
            TestContext.Current.CancellationToken);

        await RunExpirationSweepAsync(factory);

        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal("Draft", fetched.Status);
    }

    // Una convertida no vence: el pedido ya salió de ahí. Mientras se quedaba en Sent después de
    // convertirse, el barrido la movía a Expired en cuanto pasaba su vigencia, con el pedido
    // vivo. La vigencia se corre al pasado directo en la base, después de convertir: es el paso
    // del tiempo lo que se simula, y por la API una convertida ya no se puede editar.
    [Fact]
    public async Task SweepDoesNotTouchAConvertedQuotationPastItsValidUntil()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        (await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        await SetValidUntilAsync(
            factory,
            new QuotationId(quotation.Id),
            TodayInBogota().AddDays(-1));

        await RunExpirationSweepAsync(factory);

        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal("Converted", fetched.Status);
    }

    // Spec 2026-09-17, punto 1: el 31 de diciembre a las 23:00 en Bogotá ya es 2027 en UTC, y una
    // cotización que vence ese día sigue vigente. Un tenant en UTC+14 ya vive el 1 de enero y la suya
    // sí vence: el corte es por tenant, dentro del mismo barrido.
    [Fact]
    public async Task SweepCutsTheDayInEachTenantsTimeZone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var dueTodayInBogota = await SentQuotationValidUntilAsync(
            factory, "America/Bogota", new DateOnly(2026, 12, 31));
        var dueYesterdayInKiritimati = await SentQuotationValidUntilAsync(
            factory, "Pacific/Kiritimati", new DateOnly(2026, 12, 31));
        var dueYesterdayInBogota = await SentQuotationValidUntilAsync(
            factory, "America/Bogota", new DateOnly(2026, 12, 30));

        var expiredCount = await RunExpirationSweepAsync(factory);

        Assert.Equal(2, expiredCount);
        Assert.Equal(QuotationStatus.Sent, await StatusOfAsync(factory, dueTodayInBogota));
        Assert.Equal(QuotationStatus.Expired, await StatusOfAsync(factory, dueYesterdayInKiritimati));
        Assert.Equal(QuotationStatus.Expired, await StatusOfAsync(factory, dueYesterdayInBogota));
    }

    // Decisión del owner (2026-09-17): un tenant cuyo huso no se puede resolver se salta y se
    // registra, sin caer en UTC y sin frenar el barrido de los demás.
    [Fact]
    public async Task SweepSkipsATenantWhoseTimeZoneCannotBeResolvedAndExpiresTheRest()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), utcNow: NewYearsEveInBogota);
        var orphan = await SentQuotationValidUntilAsync(
            factory, "America/Bogota", new DateOnly(2026, 12, 1));
        var dueYesterdayInBogota = await SentQuotationValidUntilAsync(
            factory, "America/Bogota", new DateOnly(2026, 12, 30));
        await MoveToUnknownTenantAsync(factory, orphan);

        var expiredCount = await RunExpirationSweepAsync(factory);

        Assert.Equal(1, expiredCount);
        Assert.Equal(QuotationStatus.Sent, await StatusOfAsync(factory, orphan));
        Assert.Equal(QuotationStatus.Expired, await StatusOfAsync(factory, dueYesterdayInBogota));
    }

    // Sin fila en tenancy.tenants: el mismo estado que deja un tenant borrado a mano.
    private static async Task MoveToUnknownTenantAsync(QepApiFactory factory, QuotationId quotationId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var unknownTenantId = Guid.CreateVersion7();
        await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE quotations.quotations SET tenant_id = {unknownTenantId} WHERE id = {quotationId.Value}",
            TestContext.Current.CancellationToken);
    }

    // Enviada por la API y con la vigencia corrida en la base: el paso del tiempo es lo que se simula.
    private static async Task<QuotationId> SentQuotationValidUntilAsync(
        QepApiFactory factory, string timeZone, DateOnly validUntil)
    {
        var (tenantId, _, client) = await RegisterTenantInTimeZoneAsync(factory, timeZone, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var quotationId = new QuotationId(quotation.Id);
        await SetValidUntilAsync(factory, quotationId, validUntil);
        return quotationId;
    }

    private static async Task<QuotationStatus> StatusOfAsync(QepApiFactory factory, QuotationId quotationId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        return await dbContext.Quotations
            .AsNoTracking()
            .Where(quotation => quotation.Id == quotationId)
            .Select(quotation => quotation.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> RunExpirationSweepAsync(QepApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IQuotationExpirationProcessor>();
        return await processor.ExpirePastDueQuotationsAsync(TestContext.Current.CancellationToken);
    }

    // Directo en la base, mismo criterio que SetQuotationStatusAsync en QuotationExportApiTests.
    private static async Task SetValidUntilAsync(
        QepApiFactory factory, QuotationId quotationId, DateOnly validUntil)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var updated = await dbContext.Quotations
            .Where(quotation => quotation.Id == quotationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(quotation => quotation.ValidUntil, validUntil),
                TestContext.Current.CancellationToken);
        Assert.Equal(1, updated);
    }
}
