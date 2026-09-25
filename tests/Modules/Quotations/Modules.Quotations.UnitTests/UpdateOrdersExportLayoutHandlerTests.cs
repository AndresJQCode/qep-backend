using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// PUT del layout (spec 2026-09-24, "Application y API"): sin fila y ExpectedVersion 1 crea la fila
/// en 2 (D9); cualquier otra versión es 412; el no-op no crea fila, no audita ni guarda; la
/// auditoría `quotations.orders_export_layout.updated` sale por outbox sólo si cambió; 403 con
/// tenant ajeno o sin SettingsUpdate.
///
/// Ronda de control, hallazgo B1: el handler autoriza antes de validar (a diferencia del brief
/// original, que corría el validador primero). Con la autorización delante, un llamador sin
/// permiso o de otro tenant recibe 403 aunque su cuerpo sea inválido — nunca 422, que
/// confirmaría que el cuerpo se leyó.
/// </summary>
public sealed class UpdateOrdersExportLayoutHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithoutARowAndIfMatchOneItCreatesTheRowAtVersionTwoAndAudits()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(repository, audit, unitOfWork);
        var columns = DefaultInputs();
        columns.Insert(0, Fixed("Tipo Doc", "FV"));
        columns[19] = columns[19] with { Header = "Correo" };

        var dto = await handler.HandleAsync(NewCommand(columns, expectedVersion: 1), TestContext.Current.CancellationToken);

        Assert.Equal(2, dto.Version);
        Assert.Equal(36, dto.Columns.Count);
        Assert.Equal("Fixed", dto.Columns[0].Kind);
        Assert.Equal("Correo", dto.Columns[19].Header);
        var stored = Assert.Single(repository.Layouts);
        Assert.Equal(2, stored.Version);
        Assert.Equal(Now, stored.UpdatedAt);
        Assert.Equal(1, unitOfWork.Saves);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(
            new RecordedAuditEntry(TenantId, SubjectId, "quotations.orders_export_layout.updated", TenantId.ToString(), "success"),
            entry);
    }

    // D9: sin fila, la única versión válida es la implícita 1.
    [Fact]
    public async Task WithoutARowAnyOtherExpectedVersionIsAConflict()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(repository, audit, unitOfWork);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            handler.HandleAsync(NewCommand(DefaultInputs(), expectedVersion: 2), TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Empty(repository.Layouts);
        Assert.Empty(audit.Entries);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task WithARowAStaleVersionIsAConflict()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        repository.Add(StoredLayout(Catalog("email", "Correo")));
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(repository, audit, unitOfWork);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            handler.HandleAsync(NewCommand(DefaultInputs(), expectedVersion: 1), TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal(2, repository.Layouts.Single().Version);
        Assert.Empty(audit.Entries);
        Assert.Equal(0, unitOfWork.Saves);
    }

    // Hallazgo 6: un primer PUT idéntico al catálogo no crea fila, responde la versión implícita y
    // no audita. Guardar sin tocar no es un cambio.
    [Fact]
    public async Task SavingTheDefaultsWithoutARowIsANoOpWithoutRowAuditOrSave()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(repository, audit, unitOfWork);

        var dto = await handler.HandleAsync(NewCommand(DefaultInputs(), expectedVersion: 1), TestContext.Current.CancellationToken);

        Assert.Equal(1, dto.Version);
        Assert.Empty(repository.Layouts);
        Assert.Empty(audit.Entries);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task SavingTheSameColumnsOnAStoredLayoutIsANoOp()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var stored = StoredLayout(Catalog("email", "Correo"));
        repository.Add(stored);
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(repository, audit, unitOfWork);
        var same = stored.Columns
            .Select(column => new OrdersExportColumnInput(column.Kind.ToString(), column.Key, column.Header, column.Value, column.Visible))
            .ToList();

        var dto = await handler.HandleAsync(NewCommand(same, expectedVersion: 2), TestContext.Current.CancellationToken);

        Assert.Equal(2, dto.Version);
        Assert.Equal(2, stored.Version);
        Assert.Empty(audit.Entries);
        Assert.Equal(0, unitOfWork.Saves);
    }

    // El dominio da el código; nada queda guardado ni auditado.
    [Fact]
    public async Task ABrokenDomainRuleIsTheDomainCodeAndNothingIsSaved()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var handler = NewHandler(repository, audit, unitOfWork);
        var hidden = DefaultInputs().Select(column => column with { Visible = false }).ToList();

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(hidden, expectedVersion: 1), TestContext.Current.CancellationToken));

        Assert.Equal("quotations.orders_export_layout.all_hidden", error.Code);
        Assert.Empty(repository.Layouts);
        Assert.Empty(audit.Entries);
        Assert.Equal(0, unitOfWork.Saves);
    }

    // El validador corre después de la autorización (B1) pero antes de leer la fila: un cuerpo
    // inválido de un llamador autorizado ni siquiera lee el repositorio.
    [Fact]
    public async Task AnInvalidBodyIsAValidationFailureBeforeReadingAnything()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var handler = NewHandler(repository, new RecordingExportAuditPublisher(), new CountingQuotationsUnitOfWork());
        var columns = DefaultInputs();
        columns[3] = columns[3] with { Header = "   " };

        await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(NewCommand(columns, expectedVersion: 1), TestContext.Current.CancellationToken));

        Assert.Equal(0, repository.FindCalls);
    }

    [Fact]
    public async Task ForAnotherTenantIsForbidden()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var handler = NewHandler(
            repository, new RecordingExportAuditPublisher(), new CountingQuotationsUnitOfWork(),
            new StubExecutionContext(SubjectId, Guid.CreateVersion7()));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(DefaultInputs(), expectedVersion: 1), TestContext.Current.CancellationToken));

        Assert.Equal(0, repository.FindCalls);
    }

    // D5: SettingsUpdate, no un permiso nuevo ni el de leer.
    [Fact]
    public async Task WithoutSettingsUpdateIsForbidden()
    {
        var handler = NewHandler(
            new InMemoryOrdersExportLayoutRepository(), new RecordingExportAuditPublisher(), new CountingQuotationsUnitOfWork(),
            new StubExecutionContext(SubjectId, TenantId, TenancyPermissions.SettingsUpdate));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(DefaultInputs(), expectedVersion: 1), TestContext.Current.CancellationToken));
    }

    // Ronda de control, hallazgo B1: tenant ajeno + cuerpo inválido debe dar 403, no 422 — la
    // autorización corre primero y el validador ni se alcanza, así que nada se lee.
    [Fact]
    public async Task ForAnotherTenantWithAnInvalidBodyIsForbiddenNotAValidationFailure()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var handler = NewHandler(
            repository, new RecordingExportAuditPublisher(), new CountingQuotationsUnitOfWork(),
            new StubExecutionContext(SubjectId, Guid.CreateVersion7()));
        var invalid = DefaultInputs();
        invalid[3] = invalid[3] with { Header = "   " };

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(NewCommand(invalid, expectedVersion: 1), TestContext.Current.CancellationToken));

        Assert.Equal(0, repository.FindCalls);
    }

    private static OrdersExportLayout StoredLayout(params OrdersExportColumnSetting[] columns)
    {
        var layout = OrdersExportLayout.CreateDefault(TenantId, Now.AddDays(-1));
        Assert.True(layout.Replace(columns, Now.AddDays(-1)));
        return layout;
    }

    private static OrdersExportColumnSetting Catalog(string key, string header, bool visible = true) =>
        OrdersExportColumnSetting.Catalog(key, header, visible);

    private static List<OrdersExportColumnInput> DefaultInputs() =>
        [.. OrdersExportLayout.Effective(stored: null)
            .Select(column => new OrdersExportColumnInput("Catalog", column.Key, column.Header, null, column.Visible))];

    private static OrdersExportColumnInput Fixed(string header, string? value) =>
        new("Fixed", null, header, value, Visible: true);

    private static UpdateOrdersExportLayoutCommand NewCommand(
        IReadOnlyList<OrdersExportColumnInput> columns, long expectedVersion) =>
        new(TenantId, columns, expectedVersion, "trace");

    private static UpdateOrdersExportLayoutHandler NewHandler(
        InMemoryOrdersExportLayoutRepository repository,
        RecordingExportAuditPublisher audit,
        CountingQuotationsUnitOfWork unitOfWork,
        IExecutionContext? executionContext = null) =>
        new(
            repository,
            unitOfWork,
            audit,
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now),
            new UpdateOrdersExportLayoutValidator());
}
