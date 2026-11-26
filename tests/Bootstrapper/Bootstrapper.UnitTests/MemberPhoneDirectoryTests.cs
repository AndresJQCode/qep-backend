using Modules.Customers.Domain;
using Modules.Hr.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

public sealed class MemberPhoneDirectoryTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Ana = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HrModuleOffReturnsNothingAndDoesNotAskHr()
    {
        var hr = new FakeHr(new HrMemberPhone(Ana, "3001234567"));
        var directory = new MemberPhoneDirectory(
            new FakeModules(TenantModuleSet.Empty), new FakeTenants("CO"), hr, new FakeNormalizer());

        var found = await directory.FindHrPhonesAsync(Tenant, [Ana], Ct);

        Assert.Empty(found);
        Assert.Equal(0, hr.Calls);
    }

    [Fact]
    public async Task ATenantWithoutARowReturnsNothingAndDoesNotAskHr()
    {
        var hr = new FakeHr(new HrMemberPhone(Ana, "3001234567"));
        var directory = new MemberPhoneDirectory(
            new FakeModules(null), new FakeTenants("CO"), hr, new FakeNormalizer());

        var found = await directory.FindHrPhonesAsync(Tenant, [Ana], Ct);

        Assert.Empty(found);
        Assert.Equal(0, hr.Calls);
    }

    [Fact]
    public async Task NormalizesDigitsWithTheTenantCountry()
    {
        var normalizer = new FakeNormalizer();
        var directory = new MemberPhoneDirectory(
            new FakeModules(TenantModuleSet.FromStored(TenantModuleKeys.All)),
            new FakeTenants("CO"),
            new FakeHr(new HrMemberPhone(Ana, "3001234567")),
            normalizer);

        var found = await directory.FindHrPhonesAsync(Tenant, [Ana], Ct);

        Assert.Equal("+573001234567", found[Ana]);
        Assert.Equal("CO", normalizer.LastCountry);
    }

    [Fact]
    public async Task DropsWhatDoesNotNormalize()
    {
        var directory = Build(new FakeHr(new HrMemberPhone(Ana, "300 abc")));

        var found = await directory.FindHrPhonesAsync(Tenant, [Ana], Ct);

        Assert.DoesNotContain(Ana, found.Keys);
    }

    [Fact]
    public async Task DropsANormalizedValueThatIsNotE164()
    {
        var directory = Build(new FakeHr(new HrMemberPhone(Ana, "short")));

        var found = await directory.FindHrPhonesAsync(Tenant, [Ana], Ct);

        Assert.DoesNotContain(Ana, found.Keys);
    }

    private static MemberPhoneDirectory Build(FakeHr hr) =>
        new(
            new FakeModules(TenantModuleSet.FromStored(TenantModuleKeys.All)),
            new FakeTenants("CO"),
            hr,
            new FakeNormalizer());

    private sealed class FakeHr(params HrMemberPhone[] phones) : IHrMemberPhones
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<HrMemberPhone>> ListAsync(
            Guid tenantId,
            IReadOnlyCollection<Guid> memberIds,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<HrMemberPhone>>(
                phones.Where(phone => memberIds.Contains(phone.MemberId)).ToList());
        }
    }

    private sealed class FakeModules(TenantModuleSet? set) : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(set);
    }

    private sealed class FakeTenants(string country) : ITenantDirectory
    {
        public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantRegionalSettings?>(new TenantRegionalSettings("UTC", "dd/MM/yyyy", "COP", "es-CO", country));
    }

    // The real libphonenumber normalizer is not reachable from here: this maps the few shapes the tests need.
    private sealed class FakeNormalizer : IPhoneNumberNormalizer
    {
        public string? LastCountry { get; private set; }

        public string? ToE164(string? phone, string country)
        {
            LastCountry = country;
            return phone switch
            {
                "3001234567" => "+573001234567",
                "+573001234567" => "+573001234567",
                "short" => "+12",
                _ => null,
            };
        }

        public bool IsKnownRegion(string regionCode) => true;

        public string? RegionOf(string? e164) => null;
    }
}
