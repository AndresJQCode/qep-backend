using Microsoft.Extensions.Logging;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using static Modules.Siigo.UnitTests.InvoiceTestData;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-26: la vista previa y la emisión anotan en la bandeja del operador la ciudad del exterior que no se pudo
/// vincular. Anotar escribe y puede fallar, y eso nunca tumba la facturación.</summary>
public sealed class SiigoForeignCityAnnotationTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static SiigoInvoiceInput Input(SiigoUnlinkedCity? city) =>
        Order() with { BilledParty = Company() with { CityCode = null, StateCode = null, UnlinkedCity = city } };

    [Fact]
    public async Task AReportableCityIsAnnotatedOnce()
    {
        var inbox = new RecordingInbox();
        var input = Input(new SiigoUnlinkedCity("US", "Miamy", Reportable: true));

        await SiigoForeignCityAnnotation.AnnotateAsync(
            inbox, new RecordingLogger(), TenantId, input, TestContext.Current.CancellationToken);

        var call = Assert.Single(inbox.Calls);
        Assert.Equal((TenantId, input.OrderId, "US", "Miamy"), (call.TenantId, call.OrderId, call.City.CountryCode, call.City.CityText));
    }

    [Fact]
    public async Task ANonReportableOrLinkedCityIsNotAnnotated()
    {
        var inbox = new RecordingInbox();
        var logger = new RecordingLogger();

        await SiigoForeignCityAnnotation.AnnotateAsync(
            inbox, logger, TenantId, Input(new SiigoUnlinkedCity("GD", "St. George's", Reportable: false)), TestContext.Current.CancellationToken);
        await SiigoForeignCityAnnotation.AnnotateAsync(
            inbox, logger, TenantId, Input(null), TestContext.Current.CancellationToken);

        Assert.Empty(inbox.Calls);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task AFailingInboxLogsAWarningWithTheExceptionTypeAndDoesNotThrow()
    {
        var inbox = new RecordingInbox { Failure = new InvalidOperationException("Miamy") };
        var logger = new RecordingLogger();

        await SiigoForeignCityAnnotation.AnnotateAsync(
            inbox, logger, TenantId, Input(new SiigoUnlinkedCity("US", "Miamy", Reportable: true)), TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("InvalidOperationException", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Miamy", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledCallerStillSeesTheCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var inbox = new RecordingInbox { Failure = new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => SiigoForeignCityAnnotation.AnnotateAsync(
                inbox,
                new RecordingLogger(),
                TenantId,
                Input(new SiigoUnlinkedCity("US", "Miamy", Reportable: true)),
                cancellation.Token));
    }

    private sealed class RecordingInbox : ISiigoForeignCityInbox
    {
        public List<(Guid TenantId, Guid OrderId, SiigoUnlinkedCity City)> Calls { get; } = [];

        public Exception? Failure { get; init; }

        public Task ReportAsync(Guid tenantId, Guid orderId, SiigoUnlinkedCity city, CancellationToken cancellationToken)
        {
            Calls.Add((tenantId, orderId, city));
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class RecordingLogger : ILogger
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
