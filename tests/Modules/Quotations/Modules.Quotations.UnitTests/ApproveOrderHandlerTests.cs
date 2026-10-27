using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Aprobar tiene permiso propio (<c>quotations.order.approve</c>) y no el de gestión: quien
/// registra el pedido —la asesora, con <c>quotations.order.manage</c>— no es quien lo revisa.
/// Estas pruebas fijan la mitad del handler; la de la política del endpoint la cubre
/// <c>OrderApiTests</c>. Y, con el ajuste <c>OnOrderApproval</c> del tenant (spec 2026-10-26),
/// aprobar exige el tipo de persona de a quien se factura.
/// </summary>
public sealed class ApproveOrderHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ApproveWithOnlyTheManagePermissionIsForbiddenAndLeavesTheOrderPending()
    {
        var row = NewRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.read", "quotations.order.manage"));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new ApproveOrderCommand(TenantId, row.Id.Value),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(OrderStatus.Pending, row.Status);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task ApproveWithTheApprovePermissionApprovesWithoutNeedingTheManagePermission()
    {
        var row = NewRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewHandler(
            row, audit, new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.approve"));

        var approved = await handler.HandleAsync(
            new ApproveOrderCommand(TenantId, row.Id.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal("Approved", approved.Status);
        Assert.Equal(["quotation.order.approved"], audit.Actions);
    }

    [Fact]
    public async Task OnOrderApprovalRejectsACustomerWithoutPersonType()
    {
        var row = NewRow();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = NewHandler(
            row, audit,
            directory: Directory(PersonTypeRequirement.OnOrderApproval),
            customer: Customer(personType: null));

        var error = await Assert.ThrowsAsync<BilledPersonTypeRequiredException>(() => ApproveAsync(handler, row));

        Assert.Equal("order.order.billed_person_type_required", error.Code);
        Assert.Equal("Customer", error.ProblemExtensions["missingOn"]);
        Assert.Equal(OrderStatus.Pending, row.Status);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task WithoutABillingRowTheLiveCustomerIsRead()
    {
        var row = NewRow();
        var handler = NewHandler(
            row,
            directory: Directory(PersonTypeRequirement.OnOrderApproval),
            customer: Customer(personType: "Natural"));

        var approved = await ApproveAsync(handler, row);

        Assert.Equal("Approved", approved.Status);
    }

    [Fact]
    public async Task WithABillingRowTheCustomersPersonTypeDoesNotCount()
    {
        var directory = Directory(PersonTypeRequirement.OnOrderApproval);

        var withoutType = NewRow(BillingParties(personType: null));
        var error = await Assert.ThrowsAsync<BilledPersonTypeRequiredException>(() =>
            ApproveAsync(
                NewHandler(withoutType, directory: directory, customer: Customer(personType: "Juridica")),
                withoutType));
        Assert.Equal("BillingParty", error.ProblemExtensions["missingOn"]);

        var withType = NewRow(BillingParties(personType: PartyPersonType.Juridica));
        var approved = await ApproveAsync(
            NewHandler(withType, directory: directory, customer: Customer(personType: null)),
            withType);
        Assert.Equal("Approved", approved.Status);
    }

    [Fact]
    public async Task AFinalConsumerOrderApprovesWithoutPersonType()
    {
        var row = NewRow(QuotationParties.Empty with { BillsToFinalConsumer = true });
        var handler = NewHandler(
            row,
            directory: Directory(PersonTypeRequirement.OnOrderApproval),
            customer: Customer(personType: null));

        var approved = await ApproveAsync(handler, row);

        Assert.Equal("Approved", approved.Status);
    }

    [Fact]
    public async Task AForeignCustomerApprovesWithoutPersonType()
    {
        var row = NewRow();
        var handler = NewHandler(
            row,
            directory: Directory(PersonTypeRequirement.OnOrderApproval),
            customer: Customer(personType: null, country: "ES"));

        var approved = await ApproveAsync(handler, row);

        Assert.Equal("Approved", approved.Status);
    }

    [Fact]
    public async Task ACustomerWithoutCountryUsesTheTenantCountry()
    {
        var row = NewRow();
        var handler = NewHandler(
            row,
            directory: Directory(PersonTypeRequirement.OnOrderApproval),
            customer: Customer(personType: null, country: null));

        var error = await Assert.ThrowsAsync<BilledPersonTypeRequiredException>(() => ApproveAsync(handler, row));

        Assert.Equal("Customer", error.ProblemExtensions["missingOn"]);
    }

    [Theory]
    [InlineData(PersonTypeRequirement.Never)]
    [InlineData(PersonTypeRequirement.OnCustomerSave)]
    public async Task NeverAndOnCustomerSaveDoNotCheck(PersonTypeRequirement requirement)
    {
        var row = NewRow();
        var handler = NewHandler(
            row, directory: Directory(requirement), customer: Customer(personType: null));

        var approved = await ApproveAsync(handler, row);

        Assert.Equal("Approved", approved.Status);
    }

    [Fact]
    public async Task ANonColombianTenantWithOnOrderApprovalApprovesWithoutPersonType()
    {
        // El requisito guardado sólo cuenta si el tenant es colombiano: aquí el efectivo es Never.
        var row = NewRow();
        var handler = NewHandler(
            row,
            directory: Directory(PersonTypeRequirement.OnOrderApproval, tenantCountry: "ES"),
            customer: Customer(personType: null, country: null));

        var approved = await ApproveAsync(handler, row);

        Assert.Equal("Approved", approved.Status);
    }

    [Fact]
    public async Task AnApprovedOrderStillAnswersNotPending()
    {
        var row = NewRow();
        var directory = Directory(PersonTypeRequirement.OnOrderApproval);
        await ApproveAsync(NewHandler(row, directory: directory, customer: Customer(personType: "Natural")), row);

        // El cliente perdió el tipo después de aprobar: un pedido aprobado no pregunta por él.
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ApproveAsync(NewHandler(row, directory: directory, customer: Customer(personType: null)), row));

        Assert.Equal("order.order.not_pending", error.Code);
    }

    private static Task<OrderDto> ApproveAsync(ApproveOrderHandler handler, Order row) =>
        handler.HandleAsync(
            new ApproveOrderCommand(TenantId, row.Id.Value),
            TestContext.Current.CancellationToken);

    // Por defecto Never y un cliente Natural/CO: las dos pruebas de permisos no cambian.
    private static ApproveOrderHandler NewHandler(
        Order row,
        RecordingQuotationAuditPublisher? audit = null,
        IExecutionContext? executionContext = null,
        StubTenantDirectory? directory = null,
        StubQuotationCustomerLookup? customer = null) =>
        new(
            new StubOrderListRepository(row),
            new NoOpQuotationsUnitOfWork(),
            audit ?? new RecordingQuotationAuditPublisher(),
            new StubMembershipDirectory(AdvisorId.Value),
            executionContext ?? new GrantedPermissionsExecutionContext(
                SubjectId, TenantId, "quotations.order.approve"),
            new FixedClock(Now),
            directory ?? Directory(PersonTypeRequirement.Never),
            customer ?? Customer(personType: "Natural"));

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
            PersonType: personType, Country: country));

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
            paymentStatus: OrderPaymentStatus.FullPaymentReceived,
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
