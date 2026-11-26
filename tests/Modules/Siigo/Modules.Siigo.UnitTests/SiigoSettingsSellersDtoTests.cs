using Modules.Siigo.Application;
using Modules.Siigo.Application.Settings;
using Modules.Siigo.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-20: un vínculo de vendedor cuya membresía ya no existe (purgada) no se pierde de la pantalla: sale como una fila
/// inactiva para poder verlo y quitarlo.</summary>
public sealed class SiigoSettingsSellersDtoTests
{
    [Fact]
    public async Task ALinkWhoseMembershipWasPurgedStillShowsAsAnInactiveRow()
    {
        var settings = SiigoSettings.Create(Guid.CreateVersion7(), Guid.CreateVersion7());
        var active = Guid.CreateVersion7();
        var purged = Guid.CreateVersion7();
        settings.ReplaceSellerLinks([new SiigoSellerLink(active, "744", "ANDREA"), new SiigoSellerLink(purged, "800", "VANESA")]);
        var members = new FakeMembers([new SiigoMemberRef(active, "Andrea", "andrea@qcode.co", true)]);

        var dto = await SiigoSettingsSupport.ToDtoAsync(
            settings, new SiigoConnectionInfo(settings.ConnectionId, "Siigo", "Active"), new FakeCompanies(), new FakeTaxRates(), members, CancellationToken.None);

        var orphan = Assert.Single(dto.Sellers!, seller => seller.MembershipId == purged);
        Assert.Equal((null, null, false, "800", "VANESA"), (orphan.Name, orphan.Email, orphan.Active, orphan.SiigoUserId, orphan.SiigoUserName));
        Assert.Equal(2, dto.Sellers!.Count);
    }

    [Fact]
    public async Task TheDocumentStepCarriesThePriceModeByItsEnumName()
    {
        var settings = SiigoSettings.Create(Guid.CreateVersion7(), Guid.CreateVersion7());
        settings.UpdateDocument(new SiigoDocumentSettings("doc-1", null, null, false, false, false, null, SiigoPriceMode.DiscountedPrice));

        var dto = await SiigoSettingsSupport.ToDtoAsync(
            settings, new SiigoConnectionInfo(settings.ConnectionId, "Siigo", "Active"), new FakeCompanies(), new FakeTaxRates(), new FakeMembers([]), CancellationToken.None);

        Assert.Equal("DiscountedPrice", dto.Document.PriceMode);
    }

    private sealed class FakeMembers(IReadOnlyList<SiigoMemberRef> roster) : ISiigoMemberDirectory
    {
        public Task<IReadOnlyList<SiigoMemberRef>> ListAsync(Guid tenantId, IReadOnlyCollection<Guid> alsoInclude, CancellationToken cancellationToken) =>
            Task.FromResult(roster);
    }

    private sealed class FakeCompanies : ISiigoCompanyDirectory
    {
        public Task<SiigoCompanyRef?> FindCompanyAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
            Task.FromResult<SiigoCompanyRef?>(null);

        public Task<IReadOnlyList<SiigoBillingAccountRef>> ListBillingAccountsAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SiigoBillingAccountRef>>([]);
    }

    private sealed class FakeTaxRates : ISiigoTaxRateDirectory
    {
        public Task<IReadOnlyList<int>> ListVatPercentagesInUseAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>([0]);

        public Task<IReadOnlyList<SiigoWithholdingRef>> ListWithholdingRatesAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SiigoWithholdingRef>>([]);
    }
}
