using Bootstrapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Revisión final, M-3: el motor de pedidos puede fallar por su configuración (una tasa de IVA, el redondeo) y el visitante sólo ve
/// «una línea no está disponible». Sin la excepción original ni un log, eso es indiagnosticable.
/// </summary>
public sealed class StoreOrdersAdapterTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AQuotationEngineRejectionKeepsTheOriginalAsTheInnerExceptionAndLogsItsCode()
    {
        var original = new QuotationsDomainException("quotation.tax.rate_missing", "No tax rate");
        var logger = new RecordingLogger();
        var adapter = Adapter(new ThrowingGateway(original), logger);

        var error = await Assert.ThrowsAsync<StoreDomainException>(
            () => adapter.QuoteAsync(TenantId, "COP", StorePricingMode.RetailOnly, [(Guid.NewGuid(), 2)], Ct));

        Assert.Equal(StoreErrorCodes.LineUnavailable, error.Code);
        Assert.Same(original, error.InnerException);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("quotation.tax.rate_missing", entry.Message, StringComparison.Ordinal);
        Assert.Contains(TenantId.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("No tax rate", entry.Message, StringComparison.Ordinal);
    }

    // El visitante anónimo tampoco ve quotation.* al crear el pedido: la tienda ya no lo toma un asesor.
    [Fact]
    public async Task AQuotationEngineRejectionOnCreateBecomesLineUnavailableAndLogsItsCode()
    {
        var original = new QuotationsDomainException("quotation.item.product_inactive", "Product is inactive");
        var logger = new RecordingLogger();
        var adapter = Adapter(new ThrowingGateway(original), logger);

        var error = await Assert.ThrowsAsync<StoreDomainException>(() => adapter.CreateAsync(Draft(), Ct));

        Assert.Equal(StoreErrorCodes.LineUnavailable, error.Code);
        Assert.Same(original, error.InnerException);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("quotation.item.product_inactive", entry.Message, StringComparison.Ordinal);
        Assert.Contains(TenantId.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Product is inactive", entry.Message, StringComparison.Ordinal);
    }

    private static Modules.Store.Application.StoreOrderDraft Draft()
    {
        var party = new StoreOrderPartyInput(
            "Ana Pérez", "+573001112233", "ana@example.com", "Calle 1 # 2-3", "CO", Guid.NewGuid(), Guid.NewGuid(), null,
            "CC", "1020304050");
        return new Modules.Store.Application.StoreOrderDraft(
            TenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            IsRetail: true,
            new StoreBillingAccount(Guid.NewGuid(), "Bancolombia", "7542", "COP"),
            party,
            party,
            BillingWithRetention: false,
            BillingVatSurplus: false,
            [new StoreOrderLineInput(Guid.NewGuid(), 2m)],
            Notes: null);
    }

    private static StoreOrdersAdapter Adapter(IStoreOrderGateway gateway, ILogger<StoreOrdersAdapter> logger)
    {
        var services = new ServiceCollection().AddScoped(_ => gateway).BuildServiceProvider();
        return new StoreOrdersAdapter(services.GetRequiredService<IServiceScopeFactory>(), logger);
    }

    private sealed class ThrowingGateway(Exception error) : IStoreOrderGateway
    {
        public Task<StoreQuote> QuoteAsync(StoreQuoteRequest request, CancellationToken cancellationToken) => throw error;

        public Task<StoreOrderResult> CreateAsync(Modules.Quotations.Application.StoreOrderDraft draft, CancellationToken cancellationToken) =>
            throw error;

        public Task<StoreOrderResult?> FindExistingAsync(Guid tenantId, Guid clientRequestId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> CountStoreOrdersSinceAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger<StoreOrdersAdapter>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
