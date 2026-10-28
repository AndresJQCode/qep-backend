using BuildingBlocks.Application;
using BuildingBlocks.Domain.Pricing;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class UpdatePriceRoundingHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] OnlyUsd = ["USD"];

    private readonly TenantId _tenantId = TenantId.New();
    private readonly InMemoryPriceRoundingRepository _repository = new();
    private readonly RecordingAuditRecorder _audit = new();
    private readonly RecordingTenancyUnitOfWork _unitOfWork = new([]);

    [Fact]
    public async Task SavingUsdUpStoresOneRowAndAuditsUsd()
    {
        var result = await Handler().HandleAsync(
            Command(("USD", 2, "Up")), TestContext.Current.CancellationToken);

        var row = Assert.Single(_repository.Rows);
        Assert.Equal("USD", row.Currency);
        Assert.Equal(new PriceRounding(2, PriceRoundingMode.Up), row.Rule);
        Assert.Equal(1, _unitOfWork.Commits);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("tenancy.price_rounding.updated", entry.Action);
        Assert.Equal(_tenantId.Value, entry.TenantId);
        Assert.Equal(OnlyUsd, entry.ChangedFields);
        var usd = Assert.Single(result.Currencies, currency => currency.Currency == "USD");
        Assert.False(usd.IsDefault);
        Assert.Equal("Up", usd.Mode);
    }

    [Fact]
    public async Task SavingTheDefaultDeletesTheRow()
    {
        await Handler().HandleAsync(Command(("USD", 2, "Up")), TestContext.Current.CancellationToken);

        var result = await Handler().HandleAsync(
            Command(("USD", 2, "Nearest")), TestContext.Current.CancellationToken);

        Assert.Empty(_repository.Rows);
        Assert.All(result.Currencies, currency => Assert.True(currency.IsDefault));
        Assert.Equal(OnlyUsd, _audit.Entries[^1].ChangedFields);
    }

    [Fact]
    public async Task SavingTheSameRulesWritesNothing()
    {
        await Handler().HandleAsync(Command(("USD", 2, "Up")), TestContext.Current.CancellationToken);
        var commits = _unitOfWork.Commits;
        var audits = _audit.Entries.Count;

        await Handler().HandleAsync(Command(("USD", 2, "Up")), TestContext.Current.CancellationToken);

        Assert.Equal(commits, _unitOfWork.Commits);
        Assert.Equal(audits, _audit.Entries.Count);
    }

    [Fact]
    public async Task AnotherTenantIsForbidden()
    {
        var exception = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Handler(contextTenant: TenantId.New()).HandleAsync(
                Command(("USD", 2, "Up")), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", exception.Code);
        Assert.Empty(_repository.Rows);
    }

    [Fact]
    public async Task WithoutSettingsUpdateIsForbidden()
    {
        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Handler(permission: TenancyPermissions.SettingsRead).HandleAsync(
                Command(("USD", 2, "Up")), TestContext.Current.CancellationToken));
        Assert.Empty(_repository.Rows);
    }

    private UpdatePriceRoundingCommand Command(params (string Currency, int Decimals, string Mode)[] changes)
    {
        // Siempre viaja el catálogo completo: lo que la prueba no menciona queda en su valor por defecto.
        var inputs = Currencies.All
            .Select(currency => changes.FirstOrDefault(change => change.Currency == currency.Code) is
                { Currency: not null } change
                ? new CurrencyPriceRoundingInput(change.Currency, change.Decimals, change.Mode)
                : new CurrencyPriceRoundingInput(currency.Code, currency.Decimals, "Nearest"))
            .ToArray();
        return new UpdatePriceRoundingCommand(_tenantId, inputs);
    }

    private UpdatePriceRoundingHandler Handler(TenantId? contextTenant = null, string? permission = null) =>
        new(
            _repository,
            _unitOfWork,
            new PermissionExecutionContext(contextTenant ?? _tenantId, permission ?? TenancyPermissions.SettingsUpdate),
            _audit,
            new FixedClock(Now),
            new UpdatePriceRoundingValidator());

    private sealed class PermissionExecutionContext(TenantId tenantId, string permission) : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.CreateVersion7();

        public TenantId TenantId { get; } = tenantId;

        public bool HasPermission(string requested) => requested == permission;
    }

    private sealed class InMemoryPriceRoundingRepository : ITenantPriceRoundingRepository
    {
        public List<TenantPriceRounding> Rows { get; } = [];

        public Task<IReadOnlyList<TenantPriceRounding>> ListByTenantAsync(
            TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TenantPriceRounding>>(
                Rows.Where(row => row.TenantId == tenantId).ToList());

        public void Add(TenantPriceRounding rounding) => Rows.Add(rounding);

        public void Remove(TenantPriceRounding rounding) => Rows.Remove(rounding);
    }
}
