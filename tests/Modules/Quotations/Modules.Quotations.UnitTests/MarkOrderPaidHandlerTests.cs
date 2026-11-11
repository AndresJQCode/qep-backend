using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Marcar como pagado tiene permiso propio (<c>quotations.order.approve</c>) y no el de gestión: quien
/// registra el pedido —la asesora, con <c>quotations.order.manage</c>— no es quien verifica la plata.
/// Con el ajuste <c>OnOrderApproval</c> del tenant (spec 2026-10-26) exige el tipo de persona de a quien
/// se factura; desde spec 2026-11-10 §3.2 esa exigencia corre al marcar pagado y al facturar.
/// </summary>
public sealed class MarkOrderPaidHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly MemberId ApproverId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MarkingPaidWithOnlyManageIsForbidden()
    {
        var row = NewRow();
        var before = row.PaymentStatus;
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.read", "quotations.order.manage"));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => MarkPaidAsync(handler, row));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(before, row.PaymentStatus);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task MarkingPaidWithApproveRecordsWhoAndWhen()
    {
        var row = NewRow();
        var audit = new RecordingQuotationAuditPublisher();
        var repository = new StubOrderListRepository(row);

        var paid = await MarkPaidAsync(NewHandler(row, audit, repository: repository), row);

        Assert.Equal("FullPaymentReceived", paid.PaymentStatus);
        Assert.Equal(ApproverId, row.PaidMarkedBy);
        Assert.Equal(Now, row.PaidMarkedAt);
        Assert.Equal(["quotation.order.paid_marked"], audit.Actions);
        var entry = Assert.Single(repository.History);
        Assert.Equal(
            (OrderHistoryEventType.PaidMarked, "Marcado como pagado.", (MemberId?)ApproverId),
            (entry.EventType, entry.Details, entry.MemberId));
    }

    [Fact]
    public async Task OnOrderApprovalRejectsACustomerWithoutPersonTypeWhenMarkingPaid()
    {
        var row = NewRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewHandler(
            row, audit,
            directory: Directory(PersonTypeRequirement.OnOrderApproval),
            customer: Customer(personType: null));

        var error = await Assert.ThrowsAsync<BilledPersonTypeRequiredException>(() => MarkPaidAsync(handler, row));

        Assert.Equal("order.order.billed_person_type_required", error.Code);
        Assert.Equal("Customer", error.ProblemExtensions["missingOn"]);
        Assert.NotEqual(OrderPaymentStatus.FullPaymentReceived, row.PaymentStatus);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task MarkingPaidRejectsACustomerWithoutDocument()
    {
        var row = NewRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewHandler(
            row, audit,
            customer: new StubQuotationCustomerLookup(new QuotationCustomerRef(
                Guid.CreateVersion7(), TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false,
                PersonType: "Natural", Country: "CO")));

        var error = await Assert.ThrowsAsync<BilledCustomerFieldRequiredException>(() => MarkPaidAsync(handler, row));

        Assert.Equal("order.order.billed_document_required", error.Code);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task WithABillingRowTheCustomersPersonTypeDoesNotCount()
    {
        var directory = Directory(PersonTypeRequirement.OnOrderApproval);

        var withoutType = NewRow(BillingParties(personType: null));
        var error = await Assert.ThrowsAsync<BilledPersonTypeRequiredException>(() =>
            MarkPaidAsync(
                NewHandler(withoutType, directory: directory, customer: Customer(personType: "Juridica")),
                withoutType));
        Assert.Equal("BillingParty", error.ProblemExtensions["missingOn"]);

        var withType = NewRow(BillingParties(personType: PartyPersonType.Juridica));
        var paid = await MarkPaidAsync(
            NewHandler(withType, directory: directory, customer: Customer(personType: null)),
            withType);
        Assert.Equal("FullPaymentReceived", paid.PaymentStatus);
    }

    [Theory]
    [InlineData(PersonTypeRequirement.Never)]
    [InlineData(PersonTypeRequirement.OnCustomerSave)]
    public async Task NeverAndOnCustomerSaveDoNotCheck(PersonTypeRequirement requirement)
    {
        var row = NewRow();

        var paid = await MarkPaidAsync(
            NewHandler(row, directory: Directory(requirement), customer: Customer(personType: null)), row);

        Assert.Equal("FullPaymentReceived", paid.PaymentStatus);
    }

    [Fact]
    public async Task TheTenantCanRequireAProofToMarkPaid()
    {
        var row = OrderBuilders.Pending(TenantId, AdvisorId, Now, proofs: []);
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewHandler(
            row, audit, settings: new FixedQuotationSettingsStore { OrderSettings = new(true, true, false) });

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => MarkPaidAsync(handler, row));

        Assert.Equal("order.order.payment_proof_required", error.Code);
        Assert.Equal(OrderPaymentStatus.PaymentPending, row.PaymentStatus);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task WithoutTheSettingAnOrderWithoutProofsCanBeMarkedPaid()
    {
        var row = OrderBuilders.Pending(TenantId, AdvisorId, Now, proofs: []);

        var paid = await MarkPaidAsync(NewHandler(row), row);

        Assert.Equal("FullPaymentReceived", paid.PaymentStatus);
    }

    [Fact]
    public async Task AlreadyPaidAnswersBeforeTheBillingChecks()
    {
        var row = NewRow();
        row.MarkPaid(AdvisorId, requirePaymentProof: false, Now);

        // El cliente perdió el tipo después de pagar: un pedido pagado no pregunta por él.
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            MarkPaidAsync(
                NewHandler(
                    row,
                    directory: Directory(PersonTypeRequirement.OnOrderApproval),
                    customer: Customer(personType: null)),
                row));

        Assert.Equal("order.order.already_paid", error.Code);
    }

    [Fact]
    public async Task MarkingPaidAPreparedOrderArchivesItWhenTheTenantSaysSo()
    {
        var row = NewRow();
        row.Prepare(AdvisorId, Now);
        var audit = new RecordingQuotationAuditPublisher();
        var repository = new StubOrderListRepository(row);

        var paid = await MarkPaidAsync(
            NewHandler(
                row, audit, repository: repository,
                settings: new FixedQuotationSettingsStore { OrderSettings = new(false, true, true) }),
            row);

        Assert.Equal("Archived", paid.Status);
        Assert.Equal(ApproverId, row.ArchivedBy);
        Assert.Equal(["quotation.order.paid_marked", "quotation.order.archived"], audit.Actions);
        Assert.Contains(
            repository.History,
            entry => entry.EventType == OrderHistoryEventType.Archived
                && entry.Details == "Archivado automáticamente: quedó pagado y preparado."
                && entry.MemberId == ApproverId);
    }

    [Fact]
    public async Task MarkingPaidAnUnpreparedOrderDoesNotArchiveIt()
    {
        var row = NewRow();
        var audit = new RecordingQuotationAuditPublisher();

        var paid = await MarkPaidAsync(
            NewHandler(row, audit, settings: new FixedQuotationSettingsStore { OrderSettings = new(false, true, true) }),
            row);

        Assert.Equal("Open", paid.Status);
        Assert.Equal(["quotation.order.paid_marked"], audit.Actions);
    }

    [Fact]
    public async Task UnmarkingPaidNeedsApproveAndReturnsToTheCalculatedStatus()
    {
        var row = NewRow();
        row.MarkPaid(AdvisorId, requirePaymentProof: false, Now);
        var audit = new RecordingQuotationAuditPublisher();
        var repository = new StubOrderListRepository(row);

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            new UnmarkOrderPaidHandler(
                repository, new NoOpQuotationsUnitOfWork(), audit, new StubMembershipDirectory(ApproverId.Value),
                new GrantedPermissionsExecutionContext(SubjectId, TenantId, "quotations.order.manage"),
                new FixedClock(Now))
            .HandleAsync(new UnmarkOrderPaidCommand(TenantId, row.Id.Value), TestContext.Current.CancellationToken));
        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, row.PaymentStatus);

        var unmarked = await new UnmarkOrderPaidHandler(
                repository, new NoOpQuotationsUnitOfWork(), audit, new StubMembershipDirectory(ApproverId.Value),
                new GrantedPermissionsExecutionContext(SubjectId, TenantId, "quotations.order.approve"),
                new FixedClock(Now))
            .HandleAsync(new UnmarkOrderPaidCommand(TenantId, row.Id.Value), TestContext.Current.CancellationToken);

        // 100.000 consignados sobre un neto de 119.000: lo calculado es pago parcial.
        Assert.Equal("PartialPaymentReceived", unmarked.PaymentStatus);
        Assert.Null(row.PaidMarkedAt);
        Assert.Equal(["quotation.order.paid_unmarked"], audit.Actions);
        Assert.Equal(OrderHistoryEventType.PaidUnmarked, Assert.Single(repository.History).EventType);
    }

    private static Task<OrderDto> MarkPaidAsync(MarkOrderPaidHandler handler, Order row) =>
        handler.HandleAsync(
            new MarkOrderPaidCommand(TenantId, row.Id.Value),
            TestContext.Current.CancellationToken);

    // Por defecto Never, un cliente Natural/CO con documento y los ajustes del owner.
    private static MarkOrderPaidHandler NewHandler(
        Order row,
        RecordingQuotationAuditPublisher? audit = null,
        IExecutionContext? executionContext = null,
        StubTenantDirectory? directory = null,
        StubQuotationCustomerLookup? customer = null,
        FixedQuotationSettingsStore? settings = null,
        StubOrderListRepository? repository = null) =>
        new(
            repository ?? new StubOrderListRepository(row),
            new NoOpQuotationsUnitOfWork(),
            audit ?? new RecordingQuotationAuditPublisher(),
            new StubMembershipDirectory(ApproverId.Value),
            executionContext ?? new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.approve"),
            new FixedClock(Now),
            directory ?? Directory(PersonTypeRequirement.Never),
            customer ?? Customer(personType: "Natural"),
            settings ?? new FixedQuotationSettingsStore());

    private static StubTenantDirectory Directory(
        PersonTypeRequirement requirement, string tenantCountry = "CO") =>
        new()
        {
            Regional = new(
                "America/Bogota", "dd/MM/yyyy", "COP", TenantNumberFormats.CommaDecimal,
                tenantCountry, requirement)
        };

    private static StubQuotationCustomerLookup Customer(string? personType, string? country = "CO") =>
        new(new QuotationCustomerRef(
            Guid.CreateVersion7(), TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false,
            PersonType: personType, Country: country,
            IdentificationNumber: "900123456", IdentificationType: "NIT"));

    private static QuotationParties BillingParties(PartyPersonType? personType) =>
        QuotationParties.Empty with
        {
            Billing = new QuotationPartyDetails
            {
                Name = "Otra S.A.S.",
                IdentificationNumber = "900123456",
                IdentificationType = PartyIdentificationType.Nit,
                PersonType = personType,
                Phone = "3001234567",
                Email = "otra@example.com",
                Address = "Calle 1 # 2-3",
                DepartmentId = Guid.CreateVersion7(),
                CityId = Guid.CreateVersion7(),
            },
            BillingWithRetention = false,
            BillingVatSurplus = false,
        };

    private static Order NewRow(QuotationParties? parties = null) =>
        OrderBuilders.Pending(
            TenantId, AdvisorId, Now,
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)],
            parties: parties);

    /// <summary>Al revés de <see cref="StubExecutionContext"/>: concede sólo lo que se le nombra.
    /// Lo que estas pruebas miran es que un permiso de más (gestionar) no alcanza, y eso no se
    /// puede decir negando uno.</summary>
    private sealed class GrantedPermissionsExecutionContext(
        Guid subjectId, Guid tenantId, params string[] grantedPermissions) : IExecutionContext
    {
        public Guid SubjectId { get; } = subjectId;

        public TenantId TenantId { get; } = new(tenantId);

        public bool HasPermission(string permission) => grantedPermissions.Contains(permission);
    }
}
