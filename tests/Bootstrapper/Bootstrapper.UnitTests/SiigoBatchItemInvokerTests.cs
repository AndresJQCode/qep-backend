using BuildingBlocks.Application;
using Bootstrapper.Copilot;
using Microsoft.Extensions.DependencyInjection;
using Modules.Authorization.Application;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>Un reenvío que no se puede hacer (conexión, permiso) no deja <c>Failed</c> un pedido cuya factura puede existir: queda por
/// confirmar, enlazado a su fila. Con una fila que nunca salió, o sin fila, sigue fallido.</summary>
public sealed class SiigoBatchItemInvokerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OrderId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALostPermissionKeepsAnInvoiceThatMayExistToConfirm(bool unknown)
    {
        var row = unknown ? Unknown() : AwaitingConfirmation();

        var outcome = await InvokeAsync(permitted: false, conflict: null, row);

        Assert.Equal(new SiigoBatchItemOutcome.ToConfirm(row.Id, "siigo.invoice_batch.forbidden"), outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnavailableConnectionKeepsAnInvoiceThatMayExistToConfirm(bool unknown)
    {
        var row = unknown ? Unknown() : AwaitingConfirmation();

        var outcome = await InvokeAsync(permitted: true, SiigoErrorCodes.ConnectionUnavailable, row);

        Assert.Equal(new SiigoBatchItemOutcome.ToConfirm(row.Id, SiigoErrorCodes.ConnectionUnavailable), outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARowThatNeverLeftStaysFailed(bool permitted)
    {
        var row = Pending(Now.AddMinutes(-20));
        row.BeginSend(Now.AddMinutes(-20));
        row.MarkTransient(SiigoErrorCodes.InvoiceUnavailable, Now.AddMinutes(-20));

        var outcome = await InvokeAsync(permitted, SiigoErrorCodes.ConnectionUnavailable, row);

        Assert.Equal(
            new SiigoBatchItemOutcome.Failed(permitted ? SiigoErrorCodes.ConnectionUnavailable : SiigoErrorCodes.InvoiceBatchForbidden),
            outcome);
    }

    [Fact]
    public async Task AnOrderWithoutALiveInvoiceStaysFailed()
    {
        var rejected = Pending(Now.AddMinutes(-20));
        rejected.MarkRejected("siigo.invoice.rejected", "invalid_reference", null, Now.AddMinutes(-20));

        var outcome = await InvokeAsync(permitted: false, conflict: null, rejected);

        Assert.Equal(new SiigoBatchItemOutcome.Failed(SiigoErrorCodes.InvoiceBatchForbidden), outcome);
    }

    private static SiigoInvoice Pending(DateTimeOffset createdAt) =>
        SiigoInvoice.PendingFor(
            Guid.CreateVersion7(), TenantId, Guid.CreateVersion7(), OrderId, SiigoInvoiceKind.Order, """{"a":1}""", 7,
            Guid.CreateVersion7(), createdAt);

    private static SiigoInvoice AwaitingConfirmation()
    {
        var row = Pending(Now.AddMinutes(-20));
        row.BeginSend(Now.AddMinutes(-20));
        row.MarkUnanswered(SiigoErrorCodes.InvoiceTimeout, null, Now.AddMinutes(-20));
        return row;
    }

    private static SiigoInvoice Unknown()
    {
        var row = Pending(Now.AddHours(-25));
        row.ExpireIfStale(Now);
        return row;
    }

    private static async Task<SiigoBatchItemOutcome> InvokeAsync(bool permitted, string? conflict, params SiigoInvoice[] rows)
    {
        var services = new ServiceCollection();
        services.AddScoped<DelegatedExecutionScope>();
        services.AddSingleton<IRequestDispatcher>(new ConflictingDispatcher(conflict ?? "unused"));
        services.AddSingleton<ISiigoInvoiceRepository>(new Invoices(rows));
        services.AddSingleton<IClock>(new FixedClock());
        await using var provider = services.BuildServiceProvider();
        var invoker = new SiigoBatchItemInvoker(new Permissions(permitted), provider.GetRequiredService<IServiceScopeFactory>());

        return await invoker.InvokeAsync(TenantId, Guid.CreateVersion7(), OrderId, null, TestContext.Current.CancellationToken);
    }

    private sealed class Permissions(bool permitted) : IAuthorizationService
    {
        public Task<AuthorizationDecision> AuthorizeAsync(Guid subjectId, Guid tenantId, string permission, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>?> ResolvePermissionsAsync(Guid subjectId, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<string>?>(permitted ? [SiigoPermissions.InvoiceIssue] : []);
    }

    private sealed class ConflictingDispatcher(string code) : IRequestDispatcher
    {
        public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) =>
            Task.FromException<TResponse>(new ResourceConflictException(code, "The connection cannot be used."));

        public Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Invoices(SiigoInvoice[] rows) : ISiigoInvoiceRepository
    {
        public Task<IReadOnlyList<SiigoInvoice>> ListByOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SiigoInvoice>>([.. rows.Where(row => row.TenantId == tenantId && row.OrderId == orderId)]);

        public Task<IReadOnlyList<SiigoInvoice>> ListByIdsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(SiigoInvoice invoice) => throw new NotSupportedException();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
