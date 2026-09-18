using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El pedido de exportación de cotizaciones (spec 2026-09-12, D4): valida en orden —permiso,
/// rango, que haya filas, límite de pendientes— y recién entonces encola. Nada pesado pasa en el
/// request: el Excel lo arma el worker.
/// </summary>
public sealed class ExportQuotationsHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 9, 12);

    // El mismo rango cortado en el día de Bogotá (spec 2026-09-17, punto 3): 00:00 del 1 de enero y
    // 00:00 del día siguiente al "hasta".
    private static readonly DateTimeOffset FromUtc = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeUtc = new(2026, 9, 13, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExportForAnotherTenantIsForbiddenAndEnqueuesNothing()
    {
        var repository = new StubQuotationListRepository(NewQuotation());
        var queue = new InMemoryExportJobQueue();
        var handler = NewHandler(
            repository, queue, executionContext: new StubExecutionContext(SubjectId, Guid.CreateVersion7()));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(0, repository.AnyCalls);
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task ExportWithoutTheReadPermissionIsForbiddenAndEnqueuesNothing()
    {
        var repository = new StubQuotationListRepository(NewQuotation());
        var queue = new InMemoryExportJobQueue();
        var handler = NewHandler(
            repository, queue, executionContext: new PermissionlessExecutionContext(SubjectId, TenantId));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal(0, repository.AnyCalls);
        Assert.Empty(queue.Jobs);
    }

    // D4, paso 1 antes que el 2: a quien no puede exportar no se le contesta que su rango estaba mal.
    [Fact]
    public async Task ExportChecksThePermissionBeforeTheRange()
    {
        var handler = NewHandler(
            new StubQuotationListRepository(),
            executionContext: new PermissionlessExecutionContext(SubjectId, TenantId));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(createdFrom: null, createdTo: null), TestContext.Current.CancellationToken));
    }

    // D4, paso 2 antes que el 3: un rango inválido no llega a consultar la base.
    [Fact]
    public async Task ExportWithARangeLongerThanOneYearIsRejectedBeforeReading()
    {
        var repository = new StubQuotationListRepository(NewQuotation());
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(
                NewCommand(createdFrom: new DateOnly(2025, 1, 1), createdTo: new DateOnly(2026, 1, 2)),
                TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "CreatedTo");
        Assert.Equal(0, repository.AnyCalls);
    }

    [Fact]
    public async Task ExportWithAnInvalidStatusFailsLikeTheList()
    {
        var repository = new StubQuotationListRepository(NewQuotation());
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(From, To, status: "NotAStatus"), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.quotation.status_invalid", error.Code);
        Assert.Equal(0, repository.AnyCalls);
    }

    // D4, paso 3: enterarse de que no había nada después de esperar un correo es peor.
    [Fact]
    public async Task ExportWithNoMatchingRowsIsRejectedAndEnqueuesNothing()
    {
        var queue = new InMemoryExportJobQueue();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(new StubQuotationListRepository(), queue, unitOfWork);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.export.empty", error.Code);
        Assert.Empty(queue.Jobs);
        Assert.Equal(0, unitOfWork.Saves);
    }

    // El NIT no vive en Quotation: si no resuelve a ningún cliente, la pregunta recibe una
    // colección vacía —"ninguna fila", no "sin filtro"— y el pedido sale como vacío.
    [Fact]
    public async Task ExportWithAClientNitThatMatchesNoCustomerIsRejectedAsEmpty()
    {
        var repository = new StubQuotationListRepository(NewQuotation());
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(From, To, clientNit: "no-existe-este-nit"), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.export.empty", error.Code);
        Assert.NotNull(repository.LastExportSearch?.ClientIds);
        Assert.Empty(repository.LastExportSearch.ClientIds);
    }

    // D4, paso 3 antes que el 4: sin filas se dice eso, aunque además esté en el límite.
    [Fact]
    public async Task ExportChecksForRowsBeforeThePendingLimit()
    {
        var queue = QueueWithPendingJobsOf(SubjectId, 3);
        var handler = NewHandler(new StubQuotationListRepository(), queue);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.export.empty", error.Code);
    }

    // D4, paso 4: tres pendientes de la misma persona, contando los dos tipos.
    [Fact]
    public async Task ExportBeyondThePendingLimitIsRejectedAndEnqueuesNothing()
    {
        var queue = QueueWithPendingJobsOf(SubjectId, 3);
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(new StubQuotationListRepository(NewQuotation()), queue, unitOfWork);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.export.pending_limit", error.Code);
        Assert.Equal(3, queue.Jobs.Count);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task PendingExportsOfSomeoneElseDoNotCount()
    {
        var queue = QueueWithPendingJobsOf(Guid.CreateVersion7(), 3);
        var handler = NewHandler(new StubQuotationListRepository(NewQuotation()), queue);

        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(4, queue.Jobs.Count);
    }

    [Fact]
    public async Task ExportEnqueuesAPendingJobWithTheValidatedFilters()
    {
        var queue = new InMemoryExportJobQueue();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var repository = new StubQuotationListRepository(NewQuotation());
        var customers = NewCustomerLookup();
        customers.IdsByIdentification.Add(ClientId);
        var handler = NewHandler(repository, queue, unitOfWork, customerLookup: customers);

        var accepted = await handler.HandleAsync(
            new ExportQuotationsCommand(TenantId, ClientId, AdvisorId.Value, "sent", From, To, "900", "0001"),
            TestContext.Current.CancellationToken);

        // El NIT resolvió al cliente sembrado: la pregunta por filas va con sus ids, no sin filtro.
        Assert.Equal([ClientId], repository.LastExportSearch!.ClientIds!);

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(new ExportJobAccepted(job.Id, Now), accepted);
        Assert.Equal(ExportJobKind.Quotations, job.Kind);
        Assert.Equal(ExportJobStatus.Pending, job.Status);
        Assert.Equal(TenantId, job.TenantId);
        Assert.Equal(SubjectId, job.RequestedBy);
        Assert.Equal(
            new QuotationsExportFilters(ClientId, AdvisorId.Value, "sent", From, To, "900", "0001"),
            JsonSerializer.Deserialize<QuotationsExportFilters>(job.Filters));
        Assert.Equal(1, unitOfWork.Saves);
    }

    // Un año exacto vale (D3).
    [Fact]
    public async Task ExportAcceptsARangeOfExactlyOneYear()
    {
        var queue = new InMemoryExportJobQueue();
        var handler = NewHandler(new StubQuotationListRepository(NewQuotation()), queue);

        await handler.HandleAsync(
            NewCommand(createdFrom: new DateOnly(2025, 1, 1), createdTo: new DateOnly(2026, 1, 1)),
            TestContext.Current.CancellationToken);

        Assert.Single(queue.Jobs);
    }

    [Fact]
    public async Task ExportAsksForRowsWithTheSameCriteriaAsTheList()
    {
        var repository = new StubQuotationListRepository(NewQuotation());
        var handler = NewHandler(repository);

        await handler.HandleAsync(
            new ExportQuotationsCommand(TenantId, ClientId, AdvisorId.Value, "sent", From, To, ClientNit: null, "0001"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            new RecordedExportSearch(ClientId, ClientIds: null, AdvisorId, QuotationStatus.Sent, FromUtc, BeforeUtc, "0001"),
            repository.LastExportSearch);
    }

    private static ExportQuotationsCommand NewCommand() => NewCommand(From, To);

    // Las fechas sin default a propósito: un `null` explícito es justo lo que ejercen las pruebas
    // del rango obligatorio.
    private static ExportQuotationsCommand NewCommand(
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? status = null,
        string? clientNit = null) =>
        new(TenantId, ClientId: null, AdvisorId: null, status, createdFrom, createdTo, clientNit, QuotationNumber: null);

    private static InMemoryExportJobQueue QueueWithPendingJobsOf(Guid requestedBy, int count)
    {
        var queue = new InMemoryExportJobQueue();
        for (var index = 0; index < count; index++)
        {
            // Alternados a propósito: el límite cuenta cotizaciones y pedidos juntos.
            var kind = index % 2 == 0 ? ExportJobKind.Quotations : ExportJobKind.Orders;
            queue.Add(ExportJob.Enqueue(Guid.CreateVersion7(), TenantId, requestedBy, kind, "{}", Now));
        }

        return queue;
    }

    private static StubQuotationCustomerLookup NewCustomerLookup() =>
        new(new QuotationCustomerRef(
            ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false));

    private static Quotation NewQuotation() =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            ClientId,
            AdvisorId,
            validUntil: null,
            paymentMethod: null,
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

    private static ExportQuotationsHandler NewHandler(
        StubQuotationListRepository repository,
        InMemoryExportJobQueue? queue = null,
        CountingQuotationsUnitOfWork? unitOfWork = null,
        IExecutionContext? executionContext = null,
        StubQuotationCustomerLookup? customerLookup = null) =>
        new(repository,
            customerLookup ?? NewCustomerLookup(),
            queue ?? new InMemoryExportJobQueue(),
            unitOfWork ?? new CountingQuotationsUnitOfWork(),
            new ExportQuotationsValidator(),
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedTenantClock(Now));
}
