using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El Excel del listado de cotizaciones: los mismos filtros que la pantalla, sin paginar, y un
/// rango de fechas obligatorio de a lo sumo un año como cota de volumen en lugar de un tope de
/// filas.
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

    [Fact]
    public async Task ExportForAnotherTenantIsForbiddenAndReadsNothing()
    {
        var repository = new StubQuotationListRepository(NewQuotation("QUO-2026-0001"));
        var handler = NewHandler(
            repository, executionContext: new StubExecutionContext(SubjectId, Guid.CreateVersion7()));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(0, repository.ExportCalls);
    }

    [Fact]
    public async Task ExportWithoutTheReadPermissionIsForbiddenAndReadsNothing()
    {
        var repository = new StubQuotationListRepository(NewQuotation("QUO-2026-0001"));
        var handler = NewHandler(
            repository, executionContext: new PermissionlessExecutionContext(SubjectId, TenantId));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(0, repository.ExportCalls);
    }

    // Autorizar antes de validar, mismo criterio que el resto del modulo: a quien no puede
    // exportar no se le contesta que su rango estaba mal.
    [Fact]
    public async Task ExportChecksThePermissionBeforeTheRange()
    {
        var handler = NewHandler(
            new StubQuotationListRepository(),
            executionContext: new PermissionlessExecutionContext(SubjectId, TenantId));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                NewQuery(createdFrom: null, createdTo: null),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExportWithoutCreatedFromIsRejectedOnThatField()
    {
        var repository = new StubQuotationListRepository(NewQuotation("QUO-2026-0001"));
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(
                NewQuery(createdFrom: null, To), TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "CreatedFrom");
        Assert.Equal(0, repository.ExportCalls);
    }

    [Fact]
    public async Task ExportWithoutCreatedToIsRejectedOnThatField()
    {
        var repository = new StubQuotationListRepository(NewQuotation("QUO-2026-0001"));
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(
                NewQuery(From, createdTo: null), TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "CreatedTo");
        Assert.Equal(0, repository.ExportCalls);
    }

    [Fact]
    public async Task ExportWithCreatedFromAfterCreatedToIsRejected()
    {
        var repository = new StubQuotationListRepository(NewQuotation("QUO-2026-0001"));
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(
                NewQuery(createdFrom: new DateOnly(2026, 9, 12), createdTo: new DateOnly(2026, 9, 11)),
                TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "CreatedTo");
        Assert.Equal(0, repository.ExportCalls);
    }

    [Fact]
    public async Task ExportWithARangeLongerThanOneYearIsRejected()
    {
        var repository = new StubQuotationListRepository(NewQuotation("QUO-2026-0001"));
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(
                NewQuery(createdFrom: new DateOnly(2025, 1, 1), createdTo: new DateOnly(2026, 1, 2)),
                TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "CreatedTo");
        Assert.Equal(0, repository.ExportCalls);
    }

    [Fact]
    public async Task ExportAcceptsARangeOfExactlyOneYear()
    {
        var builder = new RecordingQuotationExportWorkbookBuilder();
        var handler = NewHandler(
            new StubQuotationListRepository(NewQuotation("QUO-2026-0001")), builder);

        var file = await handler.HandleAsync(
            NewQuery(createdFrom: new DateOnly(2025, 1, 1), createdTo: new DateOnly(2026, 1, 1)),
            TestContext.Current.CancellationToken);

        Assert.Same(builder.Result, file);
    }

    // Un archivo con solo la cabecera es peor que decir que no habia nada: mismo criterio que
    // `reporting.export.empty` y `customers.export.empty`.
    [Fact]
    public async Task ExportWithNoMatchingRowsFailsAndBuildsNothing()
    {
        var builder = new RecordingQuotationExportWorkbookBuilder();
        var handler = NewHandler(new StubQuotationListRepository(), builder);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.export.empty", error.Code);
        Assert.Null(builder.Rows);
    }

    // El NIT no vive en Quotation: si no resuelve a ningun cliente, la busqueda recibe una
    // coleccion vacia -- "ninguna fila", no "sin filtro" -- y el Excel sale vacio.
    [Fact]
    public async Task ExportWithAClientNitThatMatchesNoCustomerFailsAsEmpty()
    {
        var repository = new StubQuotationListRepository(NewQuotation("QUO-2026-0001"));
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(
                NewQuery(From, To, clientNit: "no-existe-este-nit"),
                TestContext.Current.CancellationToken));

        Assert.Equal("quotation.export.empty", error.Code);
        var clientIds = repository.LastExportSearch?.ClientIds;
        Assert.NotNull(clientIds);
        Assert.Empty(clientIds);
    }

    [Fact]
    public async Task ExportFiltersWithTheSameCriteriaAsTheList()
    {
        var repository = new StubQuotationListRepository(NewQuotation("QUO-2026-0001"));
        var handler = NewHandler(repository);

        await handler.HandleAsync(
            new ExportQuotationsQuery(
                TenantId, ClientId, AdvisorId.Value, "sent", From, To, ClientNit: null, "0001"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            new RecordedExportSearch(
                ClientId, ClientIds: null, AdvisorId, QuotationStatus.Sent, From, To, "0001"),
            repository.LastExportSearch);
    }

    [Fact]
    public async Task ExportWithAnInvalidStatusFailsLikeTheList()
    {
        var repository = new StubQuotationListRepository(NewQuotation("QUO-2026-0001"));
        var handler = NewHandler(repository);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(
                NewQuery(From, To, status: "NotAStatus"), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.quotation.status_invalid", error.Code);
        Assert.Equal(0, repository.ExportCalls);
    }

    // Cada fila del Excel dice el cliente y la asesora por nombre y correo, igual que la tabla:
    // un id en una planilla no le sirve a nadie.
    [Fact]
    public async Task ExportHandsTheBuilderRowsWithTheCustomerNameAndTheAdvisorEmail()
    {
        var builder = new RecordingQuotationExportWorkbookBuilder();
        var handler = NewHandler(
            new StubQuotationListRepository(NewQuotation("QUO-2026-0001")),
            builder,
            advisors: new StubQuotationAdvisorLookup("asesora@example.com"));

        var file = await handler.HandleAsync(NewQuery(), TestContext.Current.CancellationToken);

        var row = Assert.Single(builder.Rows!);
        Assert.Equal("QUO-2026-0001", row.QuotationNumber);
        Assert.Equal("Ferretería El Tornillo", row.ClientName);
        Assert.Equal("asesora@example.com", row.AdvisorEmail);
        Assert.Equal(Now, builder.GeneratedAt);
        Assert.Same(builder.Result, file);
    }

    private static ExportQuotationsQuery NewQuery() => NewQuery(From, To);

    // Las fechas sin default a proposito: un `null` explicito es justo lo que ejercen las pruebas
    // del rango obligatorio, y un default lo confundiria con "no lo pasaron".
    private static ExportQuotationsQuery NewQuery(
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? status = null,
        string? clientNit = null) =>
        new(
            TenantId,
            ClientId: null,
            AdvisorId: null,
            status,
            createdFrom,
            createdTo,
            clientNit,
            QuotationNumber: null);

    private static StubQuotationCustomerLookup NewCustomerLookup() =>
        new(new QuotationCustomerRef(
            ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false));

    private static Quotation NewQuotation(string number) =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            number,
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
        RecordingQuotationExportWorkbookBuilder? builder = null,
        IExecutionContext? executionContext = null,
        StubQuotationAdvisorLookup? advisors = null) =>
        new(repository,
            NewCustomerLookup(),
            advisors ?? new StubQuotationAdvisorLookup(),
            builder ?? new RecordingQuotationExportWorkbookBuilder(),
            new ExportQuotationsValidator(),
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));
}
