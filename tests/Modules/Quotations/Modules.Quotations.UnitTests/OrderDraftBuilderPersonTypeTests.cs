using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

// Spec 2026-10-26: el pedido manual que se crea con una fila de facturación propia también exige su tipo de persona
// cuando el tenant tiene OnCustomerSave (antes sólo lo exigía «Editar pedido»). El cálculo previo no lo exige.
public sealed class OrderDraftBuilderPersonTypeTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid CompanyId = Guid.CreateVersion7();
    private static readonly Guid VariantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);

    private static OrderDraftInput NewInput(string? billingPersonType) =>
        new(
            TenantId, ClientId, new MemberId(Guid.CreateVersion7()), null, null, false, null,
            new QuotationPartiesRequest(
                new QuotationPartyRequest(
                    "Otra S.A.S.", "900123456", "3001234567", "otra@example.com", "Calle 1 # 2-3",
                    Guid.CreateVersion7(), Guid.CreateVersion7(),
                    PersonType: billingPersonType, IdentificationType: "NIT"),
                null),
            new QuotationBillingAccountRequest(CompanyId, "Bancolombia", "7542", "COP"),
            [new OrderItemAddition(VariantId, 1m)],
            OrderPaymentStatus.PaymentPending, [], AllowIncompleteCustomer: false);

    private static OrderDraftBuilder NewBuilder(PersonTypeRequirement requirement)
    {
        var settings = new FixedQuotationSettingsStore();
        return new OrderDraftBuilder(
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>
            {
                [CompanyId] = new(
                    CompanyId, "Mi empresa", "800111222", true, null, null,
                    [new QuotationCompanyAccountRef("Bancolombia", "7542", "COP")])
            }),
            new StubQuotationProductPricingLookup(new QuotationVariantPricingRef(
                VariantId, VariantId, TenantId, "Aceite", "ACE", true,
                new Dictionary<string, decimal> { ["COP"] = 10_000m }, [], 19)),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
            settings,
            new FixedTenantPriceRounding(),
            new ShippingResolver(new StubCarrierRepository(), new StubQuotationTaxRateLookup(null), settings),
            new FixedTenantDefaultCurrency(),
            new StubTenantDirectory
            {
                Regional = new(
                    "America/Bogota", "dd/MM/yyyy", "COP", TenantNumberFormats.CommaDecimal, "CO", requirement)
            });
    }

    [Fact]
    public async Task OnCustomerSaveRejectsACreatedOrderWhoseBillingRowHasNoPersonType()
    {
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            NewBuilder(PersonTypeRequirement.OnCustomerSave).BuildAsync(
                NewInput(null), "PED-2026-0001", Now, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.billing.person_type_required", error.Code);
        Assert.Equal(["Parties.Billing.PersonType"], error.FieldErrors.Keys);
    }

    [Fact]
    public async Task OnCustomerSaveAcceptsACreatedOrderWhoseBillingRowHasPersonType()
    {
        var order = await NewBuilder(PersonTypeRequirement.OnCustomerSave).BuildAsync(
            NewInput("Juridica"), "PED-2026-0001", Now, TestContext.Current.CancellationToken);

        Assert.Equal(PartyPersonType.Juridica, order.Billing?.PersonType);
    }
}
