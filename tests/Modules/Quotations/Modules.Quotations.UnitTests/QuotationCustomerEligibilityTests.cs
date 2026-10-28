using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-10-10 §6.3 (RF10): un incompleto es <c>client_incomplete</c>, antes que
/// <c>client_cuc_missing</c> (un incompleto tampoco tiene CUC, y la pantalla tiene que decir «completa la ficha»).</summary>
public sealed class QuotationCustomerEligibilityTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();

    private static QuotationCustomerRef Ref(string? cuc, bool isActive = true, bool isComplete = true, Guid? tenantId = null) =>
        new(ClientId, tenantId ?? TenantId, cuc, isActive, "Laura", null, null, WithRetention: false, VatSurplus: false, IsComplete: isComplete);

    [Theory]
    [InlineData(null, true, false, "quotation.quotation.client_incomplete")]
    [InlineData("CLI08000001", false, false, "quotation.quotation.client_incomplete")]
    [InlineData(null, true, true, "quotation.quotation.client_cuc_missing")]
    [InlineData("CLI08000001", false, true, "quotation.quotation.client_inactive")]
    public void TheChecksRunInOrder(string? cuc, bool isActive, bool isComplete, string expectedCode)
    {
        var error = Assert.Throws<QuotationsDomainException>(() => QuotationCustomerEligibility.Ensure(Ref(cuc, isActive, isComplete), TenantId, ClientId));
        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public void AnotherTenantIsStillNotFoundEvenIfIncomplete()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            QuotationCustomerEligibility.Ensure(Ref(null, isComplete: false, tenantId: Guid.CreateVersion7()), TenantId, ClientId));
        Assert.Equal("quotation.quotation.client_not_found", error.Code);
    }

    [Fact]
    public void ACompleteActiveCustomerWithCucPasses() =>
        QuotationCustomerEligibility.Ensure(Ref("CLI08000001"), TenantId, ClientId);
}
