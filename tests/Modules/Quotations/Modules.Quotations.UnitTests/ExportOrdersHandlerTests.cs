using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>El pedido de exportación de pedidos: mismo orden de D4 que cotizaciones, con el permiso
/// de pedidos y los códigos `sale.export.*`. El límite de pendientes cuenta los dos tipos.</summary>
public sealed class ExportOrdersHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    [Fact]
    public async Task ExportForAnotherTenantIsForbiddenAndReadsNothing()
    {
        var repository = new StubOrderListRepository(NewRow());
        var handler = NewHandler(
            repository, executionContext: new StubExecutionContext(SubjectId, Guid.CreateVersion7()));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal(0, repository.AnyCalls);
    }

    // Poder ver cotizaciones no es poder ver pedidos: sin SaleRead no se exporta.
    [Fact]
    public async Task ExportWithoutTheOrderReadPermissionIsForbidden()
    {
        var repository = new StubOrderListRepository(NewRow());
        var handler = NewHandler(
            repository,
            executionContext: new StubExecutionContext(SubjectId, TenantId, OrdersPermissions.SaleRead));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal(0, repository.AnyCalls);
    }

    // El CUC que sí resuelve: los ids de esos clientes —y no "sin filtro"— son los que llegan a la
    // pregunta por filas, igual que en el listado.
    [Fact]
    public async Task ExportWithAClientCucAsksForRowsOfTheClientsItResolvesTo()
    {
        var repository = new StubOrderListRepository(NewRow());
        var customers = NewCustomerLookup();
        customers.IdsByCuc.Add(ClientId);
        var handler = NewHandler(repository, customerLookup: customers);

        await handler.HandleAsync(NewCommand(From, To, clientCuc: "CUC-001"), TestContext.Current.CancellationToken);

        Assert.Equal("CUC-001", customers.LastCucTerm);
        Assert.Equal(1, repository.AnyCalls);
        Assert.Equal([ClientId], repository.LastExportSearch!.ClientIds!);
    }

    [Fact]
    public async Task ExportWithARangeLongerThanOneYearIsRejectedBeforeReading()
    {
        var repository = new StubOrderListRepository(NewRow());
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(
                NewCommand(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 2)),
                TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "ConvertedTo");
        Assert.Equal(0, repository.AnyCalls);
    }

    [Theory]
    [InlineData("NotAStatus", null, "order.order.status_invalid")]
    [InlineData(null, "NotAPaymentStatus", "order.order.payment_status_invalid")]
    public async Task ExportWithAnInvalidStatusFailsLikeTheList(
        string? status, string? paymentStatus, string expectedCode)
    {
        var handler = NewHandler(new StubOrderListRepository(NewRow()));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(From, To, status, paymentStatus), TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public async Task ExportWithNoMatchingOrdersIsRejectedAndEnqueuesNothing()
    {
        var queue = new InMemoryExportJobQueue();
        var handler = NewHandler(new StubOrderListRepository(), queue);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("order.export.empty", error.Code);
        Assert.Empty(queue.Jobs);
    }

    // El CUC no vive en el pedido: si no resuelve a ningún cliente, "ninguna fila".
    [Fact]
    public async Task ExportWithAClientCucThatMatchesNoCustomerIsRejectedAsEmpty()
    {
        var repository = new StubOrderListRepository(NewRow());
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(From, To, clientCuc: "CUC-NO-EXISTE"), TestContext.Current.CancellationToken));

        Assert.Equal("order.export.empty", error.Code);
        Assert.Empty(repository.LastExportSearch!.ClientIds!);
    }

    // Tres exportaciones de cotizaciones pendientes también llenan el cupo de pedidos (D4).
    [Fact]
    public async Task PendingQuotationExportsCountTowardsTheLimit()
    {
        var queue = new InMemoryExportJobQueue();
        for (var index = 0; index < 3; index++)
        {
            queue.Add(ExportJob.Enqueue(Guid.CreateVersion7(), TenantId, SubjectId, ExportJobKind.Quotations, "{}", Now));
        }

        var handler = NewHandler(new StubOrderListRepository(NewRow()), queue);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("order.export.pending_limit", error.Code);
        Assert.Equal(3, queue.Jobs.Count);
    }

    [Fact]
    public async Task ExportEnqueuesAOrdersJobWithTheValidatedFilters()
    {
        var queue = new InMemoryExportJobQueue();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var repository = new StubOrderListRepository(NewRow());
        var handler = NewHandler(repository, queue, unitOfWork);

        var accepted = await handler.HandleAsync(
            new ExportOrdersCommand(
                TenantId, ClientId, AdvisorId.Value, "pending", "paymentpending", From, To, null, "VEN-2026"),
            TestContext.Current.CancellationToken);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(new ExportJobAccepted(job.Id, Now), accepted);
        Assert.Equal(ExportJobKind.Sales, job.Kind);
        Assert.Equal(SubjectId, job.RequestedBy);
        Assert.Equal(
            new OrdersExportFilters(ClientId, AdvisorId.Value, "pending", "paymentpending", From, To, null, "VEN-2026"),
            JsonSerializer.Deserialize<OrdersExportFilters>(job.Filters));
        Assert.Equal(
            new RecordedOrderExportSearch(
                ClientId, null, AdvisorId, OrderStatus.Pending, OrderPaymentStatus.PaymentPending, From, To, "VEN-2026"),
            repository.LastExportSearch);
        Assert.Equal(1, unitOfWork.Saves);
    }

    private static ExportOrdersCommand NewCommand() => NewCommand(From, To);

    private static ExportOrdersCommand NewCommand(
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? status = null,
        string? paymentStatus = null,
        string? clientCuc = null) =>
        new(TenantId, null, null, status, paymentStatus, convertedFrom, convertedTo, clientCuc, null);

    private static OrderWithQuotation NewRow()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 10, 30),
            paymentMethod: null, notes: null, QuotationParties.Empty, billingAccount: null,
            customerWithRetention: false, customerVatSurplus: false, AdvisorId, Now);
        var order = Order.Create(
            OrderId.New(), TenantId, "VEN-2026-0001", quotation.Id, OrderPaymentStatus.PaymentPending,
            notes: null, AdvisorId, [], Now);
        return new OrderWithQuotation(order, quotation);
    }

    private static StubQuotationCustomerLookup NewCustomerLookup() =>
        new(new QuotationCustomerRef(
            ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false));

    private static ExportOrdersHandler NewHandler(
        StubOrderListRepository repository,
        InMemoryExportJobQueue? queue = null,
        CountingQuotationsUnitOfWork? unitOfWork = null,
        IExecutionContext? executionContext = null,
        StubQuotationCustomerLookup? customerLookup = null) =>
        new(repository,
            customerLookup ?? NewCustomerLookup(),
            queue ?? new InMemoryExportJobQueue(),
            unitOfWork ?? new CountingQuotationsUnitOfWork(),
            new ExportOrdersValidator(),
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));
}
