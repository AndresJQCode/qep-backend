using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.RichText;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Marcar como enviada por fuera de QEP (spec 2026-11-17): el mismo paso a <c>Sent</c> que el envío por WhatsApp,
/// sin canal. Lo que se afirma es lo que deja: estado, historial, auditoría, evento para Pipeline y el PDF al día.
/// </summary>
public sealed class MarkQuotationSentHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly Guid ProductId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly ValidUntil = new(2026, 11, 30);

    [Fact]
    public async Task MarkSentMovesADraftToSentAndPublishesTheSentEvent()
    {
        var harness = NewHandler();

        var dto = await harness.Handler.HandleAsync(harness.Command, TestContext.Current.CancellationToken);

        var quotation = harness.Repository.Quotation;
        Assert.Equal(QuotationStatus.Sent, quotation.Status);
        Assert.Equal(Now, quotation.SentAt);
        Assert.Equal(quotation.Id.Value, dto.Id);

        var entry = Assert.Single(harness.Repository.HistoryEntries);
        Assert.Equal(QuotationHistoryEventType.MarkedSent, entry.EventType);
        Assert.Equal("Marcada como enviada por fuera de QEP.", entry.Details);
        Assert.Equal(AdvisorId, entry.MemberId);

        Assert.Equal(MarkQuotationSentHandler.AuditAction, Assert.Single(harness.Audit.Actions));
        Assert.Equal("quotation.quotation.marked_sent", MarkQuotationSentHandler.AuditAction);
        Assert.Equal("sent", Assert.Single(harness.Lifecycle.Published).Event);
        Assert.Equal(1, harness.Renderer.Calls);
        Assert.Equal(1, harness.UnitOfWork.Saves);
    }

    [Theory]
    [InlineData(QuotationStatus.Voided)]
    [InlineData(QuotationStatus.Expired)]
    [InlineData(QuotationStatus.Converted)]
    public async Task MarkSentRejectsAClosedQuotation(QuotationStatus status)
    {
        var harness = NewHandler(closedAs: status);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(harness.Command, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.quotation.not_draft", error.Code);
        Assert.Equal(status, harness.Repository.Quotation.Status);
        Assert.Empty(harness.Repository.HistoryEntries);
        Assert.Empty(harness.Lifecycle.Published);
        Assert.Equal(0, harness.Renderer.Calls);
        Assert.Equal(0, harness.UnitOfWork.Saves);
    }

    [Fact]
    public async Task MarkSentRejectsAnInactiveCustomer()
    {
        var harness = NewHandler(customerActive: false);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(harness.Command, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.quotation.client_inactive", error.Code);
        Assert.Equal(QuotationStatus.Draft, harness.Repository.Quotation.Status);
        Assert.Empty(harness.Lifecycle.Published);
        Assert.Empty(harness.Repository.HistoryEntries);
    }

    // Spec 2026-10-26: con OnCustomerSave, la fila de facturación con nombre propio exige su tipo de persona también
    // al marcarla enviada, igual que en el envío. Se rechaza antes de pagar el PDF.
    [Fact]
    public async Task MarkSentRequiresTheBillingPersonTypeWhenTheTenantAsksForIt()
    {
        var harness = NewHandler(billingWithoutPersonType: true);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(harness.Command, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.billing.person_type_required", error.Code);
        Assert.Equal(["Parties.Billing.PersonType"], error.FieldErrors.Keys);
        Assert.Equal(QuotationStatus.Draft, harness.Repository.Quotation.Status);
        Assert.Empty(harness.Repository.HistoryEntries);
        Assert.Empty(harness.Audit.Actions);
        Assert.Empty(harness.Lifecycle.Published);
        Assert.Equal(0, harness.Renderer.Calls);
        Assert.Equal(0, harness.UnitOfWork.Saves);
    }

    [Fact]
    public async Task MarkSentWithoutManagePermissionIsForbidden()
    {
        var harness = NewHandler(deniedPermission: QuotationsPermissions.QuotationManage);

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            harness.Handler.HandleAsync(harness.Command, TestContext.Current.CancellationToken));

        Assert.Equal(QuotationStatus.Draft, harness.Repository.Quotation.Status);
        Assert.Empty(harness.Lifecycle.Published);
        Assert.Equal(0, harness.Renderer.Calls);
    }

    private sealed record Harness(
        MarkQuotationSentHandler Handler,
        MarkQuotationSentCommand Command,
        StubQuotationRepository Repository,
        RecordingQuotationAuditPublisher Audit,
        RecordingQuotationLifecycleEventPublisher Lifecycle,
        CountingPdfRenderer Renderer,
        CountingQuotationsUnitOfWork UnitOfWork);

    private static Harness NewHandler(
        QuotationStatus? closedAs = null,
        bool customerActive = true,
        string? deniedPermission = null,
        bool billingWithoutPersonType = false)
    {
        var tenant = new StubTenantDirectory();
        var parties = QuotationParties.Empty;
        if (billingWithoutPersonType)
        {
            tenant.Regional = new(
                "America/Bogota", "dd/MM/yyyy", "COP", TenantNumberFormats.CommaDecimal, "CO",
                PersonTypeRequirement.OnCustomerSave);
            parties = parties with
            {
                Billing = new QuotationPartyDetails
                {
                    Name = "Otra S.A.S.",
                    IdentificationNumber = "900123456",
                    IdentificationType = PartyIdentificationType.Nit,
                    PersonType = null,
                },
            };
        }

        var quotation = Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            ClientId,
            AdvisorId,
            ValidUntil,
            paymentMethod: "Transferencia bancaria",
            notes: null,
            parties,
            OrderBuilders.BillingAccount,
            defaultCurrency: "COP",
            customerVatSurplus: false,
            customerWithholdings: [],
            MunicipalContext.None,
            AdvisorId,
            Now);
        quotation.AddItem(
            QuotationItemId.New(), ProductId, ProductId, quantity: 1m, unitPrice: 1_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        // El estado cerrado se arma con las transiciones del agregado, no con reflexión: así la prueba
        // no depende de cómo guarda el estado.
        switch (closedAs)
        {
            case QuotationStatus.Voided:
                quotation.Void(AdvisorId, Now.AddHours(-1));
                break;
            case QuotationStatus.Expired:
                quotation.Send(AdvisorId, Now.AddHours(-2), QuotationProductSnapshot.None);
                quotation.Expire(Now.AddHours(-1));
                break;
            case QuotationStatus.Converted:
                quotation.ConvertToOrder(AdvisorId, Now.AddHours(-1), QuotationProductSnapshot.None);
                break;
        }

        var repository = new StubQuotationRepository(quotation);
        var audit = new RecordingQuotationAuditPublisher();
        var lifecycle = new RecordingQuotationLifecycleEventPublisher();
        var renderer = new CountingPdfRenderer();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        var customer = new QuotationCustomerRef(
            ClientId, TenantId, "CUC-001", IsActive: customerActive, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false);

        var handler = new MarkQuotationSentHandler(
            repository,
            unitOfWork,
            audit,
            lifecycle,
            new QuotationPdfProvider(
                repository,
                new StubQuotationResponseComposer(),
                renderer,
                new RecordingPdfStorage("https://r2.example.com/cot.pdf"),
                new FixedTenantClock(Now),
                new StubQuotationLogoLookup(),
                tenant,
                new StubPdfContentStore(),
                new RichTextHtmlReader()),
            new StubQuotationCustomerLookup(customer),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>
            {
                [ProductId] = new(ProductId, ProductId, "Tornillo 1/4", "TOR-001", ImageUrl: null, Scales: []),
            }),
            tenant,
            new StubMembershipDirectory(AdvisorId.Value),
            deniedPermission is null
                ? new StubExecutionContext(SubjectId, TenantId)
                : new StubExecutionContext(SubjectId, TenantId, deniedPermission),
            new FixedClock(Now),
            FixedTenantModules.Simulated);

        return new Harness(
            handler,
            new MarkQuotationSentCommand(TenantId, quotation.Id.Value),
            repository,
            audit,
            lifecycle,
            renderer,
            unitOfWork);
    }
}
