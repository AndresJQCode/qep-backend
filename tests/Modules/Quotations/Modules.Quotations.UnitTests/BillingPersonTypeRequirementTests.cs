using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

// Spec 2026-10-26: con OnCustomerSave, una fila de facturación con nombre propio exige su tipo de persona en los
// mismos lugares que exige el número (crear, guardar, enviar, convertir). El helper es lo que esos cuatro llaman.
public sealed class BillingPersonTypeRequirementTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static StubTenantDirectory Directory(
        PersonTypeRequirement requirement, string country = "CO") =>
        new()
        {
            Regional = new("America/Bogota", "dd/MM/yyyy", "COP", TenantNumberFormats.CommaDecimal, country, requirement)
        };

    private static Quotation NewQuotation(
        PartyPersonType? billingPersonType, bool withBillingRow = true, bool finalConsumer = false)
    {
        var parties = QuotationParties.Empty with
        {
            Billing = withBillingRow
                ? new QuotationPartyDetails
                {
                    Name = "Otra S.A.S.",
                    IdentificationNumber = "900123456",
                    IdentificationType = PartyIdentificationType.Nit,
                    PersonType = billingPersonType,
                }
                : null,
            BillsToFinalConsumer = finalConsumer,
        };
        return Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", Guid.CreateVersion7(),
            new MemberId(Guid.CreateVersion7()), null, null, null, parties,
            null, "COP", false, false, new MemberId(Guid.CreateVersion7()), DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task OnCustomerSaveRequiresTheBillingRowsPersonType()
    {
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            BillingPersonTypeRequirement.EnsureForQuotationAsync(
                Directory(PersonTypeRequirement.OnCustomerSave), TenantId, NewQuotation(null),
                TestContext.Current.CancellationToken));

        Assert.Equal("quotation.billing.person_type_required", error.Code);
        Assert.Equal(["Parties.Billing.PersonType"], error.FieldErrors.Keys);
    }

    [Fact]
    public async Task OnCustomerSaveAcceptsABillingRowWithPersonType()
    {
        await BillingPersonTypeRequirement.EnsureForQuotationAsync(
            Directory(PersonTypeRequirement.OnCustomerSave), TenantId, NewQuotation(PartyPersonType.Juridica),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ACustomerBilledQuotationIsNotChecked()
    {
        await BillingPersonTypeRequirement.EnsureForQuotationAsync(
            Directory(PersonTypeRequirement.OnCustomerSave), TenantId, NewQuotation(null, withBillingRow: false),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AFinalConsumerQuotationIsNotChecked()
    {
        await BillingPersonTypeRequirement.EnsureForQuotationAsync(
            Directory(PersonTypeRequirement.OnCustomerSave), TenantId,
            NewQuotation(null, withBillingRow: false, finalConsumer: true),
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(PersonTypeRequirement.Never)]
    [InlineData(PersonTypeRequirement.OnOrderApproval)]
    public async Task NeverAndOnOrderApprovalDoNotRequireIt(PersonTypeRequirement requirement)
    {
        await BillingPersonTypeRequirement.EnsureForQuotationAsync(
            Directory(requirement), TenantId, NewQuotation(null), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ANonColombianTenantIsNotChecked()
    {
        // El requisito guardado queda en OnCustomerSave, pero Effective lo vuelve Never fuera de CO.
        await BillingPersonTypeRequirement.EnsureForQuotationAsync(
            Directory(PersonTypeRequirement.OnCustomerSave, country: "ES"), TenantId, NewQuotation(null),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ATenantWithoutSettingsIsNotChecked()
    {
        await BillingPersonTypeRequirement.EnsureForQuotationAsync(
            new StubTenantDirectory { Regional = null }, TenantId, NewQuotation(null),
            TestContext.Current.CancellationToken);
    }
}
