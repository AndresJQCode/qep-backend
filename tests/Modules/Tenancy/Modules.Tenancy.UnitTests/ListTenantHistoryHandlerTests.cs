using BuildingBlocks.Application;
using FluentValidation;
using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §5, <c>GET …/history</c>.</summary>
public sealed class ListTenantHistoryHandlerTests
{
    private static readonly TenantId Operator = TenantId.New();
    private static readonly TenantId Target = TenantId.New();
    private static readonly Guid Andres = Guid.CreateVersion7();
    private static readonly Guid Gone = Guid.CreateVersion7();
    private static readonly DateTimeOffset At = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static ListTenantHistoryHandler Handler(
        FixedOperatorTenantReader reader, CountingUserDirectory users, TenantStatus? targetStatus = TenantStatus.Active) =>
        new(reader, new FixedTenantDirectory(targetStatus), users,
            new OperatorContext(Operator, OperatorPermissions.TenantsRead), new FixedOperatorTenant(Operator.Value),
            new ListTenantHistoryValidator());

    [Fact]
    public async Task BatchesKeepTheirChangesAndEachActorIsResolvedOnce()
    {
        var cascade = Guid.CreateVersion7();
        var reader = new FixedOperatorTenantReader
        {
            History = new(
            [
                [
                    TenantChange.ForModule(Target, cascade, TenantModuleKeys.Customers, TenantModuleStatus.Active, TenantModuleStatus.Inactive, ChangeReason.Cancellation, "nota", Andres, At),
                    TenantChange.ForModule(Target, cascade, TenantModuleKeys.Quotations, TenantModuleStatus.Active, TenantModuleStatus.Inactive, ChangeReason.Cancellation, "nota", Andres, At),
                ],
                [TenantChange.ForTenantStatus(Target, Guid.CreateVersion7(), TenantStatus.Active, TenantStatus.Suspended, ChangeReason.Nonpayment, null, Andres, At.AddDays(-1))],
                [TenantChange.ForModule(Target, Guid.CreateVersion7(), TenantModuleKeys.Pos, null, TenantModuleStatus.Active, ChangeReason.Courtesy, null, Gone, At.AddDays(-2))],
            ], Total: 3),
        };
        var users = new CountingUserDirectory(new Dictionary<Guid, string> { [Andres] = "andres@qcode.co" });

        var page = await Handler(reader, users).HandleAsync(
            new ListTenantHistoryQuery(Operator, Target, null, 1, 25), TestContext.Current.CancellationToken);

        Assert.Equal((3, 1, 25), (page.Total, page.Page, page.PageSize));
        var first = page.Items[0];
        Assert.Equal((cascade, "module", "cancellation", "nota", "andres@qcode.co"),
            (first.BatchId, first.Kind, first.Reason, first.Note, first.ActorEmail));
        Assert.Equal(
            [new OperatorHistoryChangeDto("customers", "active", "inactive"), new OperatorHistoryChangeDto("quotations", "active", "inactive")],
            first.Changes);
        Assert.Equal(("tenant_status", new OperatorHistoryChangeDto(null, "Active", "Suspended")),
            (page.Items[1].Kind, Assert.Single(page.Items[1].Changes)));
        Assert.Null(page.Items[2].ActorEmail);                     // usuario que ya no existe
        Assert.Null(Assert.Single(page.Items[2].Changes).FromStatus);
        Assert.Equal(2, users.Calls);                              // deduplicado por página
    }

    [Fact]
    public async Task TheModuleFilterReachesTheReaderTyped()
    {
        var reader = new FixedOperatorTenantReader();

        await Handler(reader, new CountingUserDirectory(new Dictionary<Guid, string>())).HandleAsync(
            new ListTenantHistoryQuery(Operator, Target, "reporting", 2, 10), TestContext.Current.CancellationToken);

        Assert.Equal(["history:reporting:2:10"], reader.Asked);
    }

    // int.MaxValue: (page - 1) * pageSize daría la vuelta a un OFFSET negativo y saldría 500.
    [Theory]
    [InlineData("inventory", 1, 25)]
    [InlineData("REPORTING", 1, 25)]
    [InlineData(null, 0, 25)]
    [InlineData(null, 1, 101)]
    [InlineData(null, int.MaxValue, 100)]
    public async Task AnInvalidQueryIsAValidationError(string? module, int page, int pageSize) =>
        await Assert.ThrowsAsync<ValidationException>(() =>
            Handler(new FixedOperatorTenantReader(), new CountingUserDirectory(new Dictionary<Guid, string>()))
                .HandleAsync(new ListTenantHistoryQuery(Operator, Target, module, page, pageSize), TestContext.Current.CancellationToken));

    [Fact]
    public async Task AMissingTargetIsNotFound() =>
        Assert.Equal("tenancy.tenant.not_found", (await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Handler(new FixedOperatorTenantReader(), new CountingUserDirectory(new Dictionary<Guid, string>()), targetStatus: null)
                .HandleAsync(new ListTenantHistoryQuery(Operator, Target, null, 1, 25), TestContext.Current.CancellationToken))).Code);

    [Fact]
    public async Task ANonOperatorIsForbiddenBeforeLookingUpTheTarget()
    {
        var reader = new FixedOperatorTenantReader();
        var directory = new FixedTenantDirectory(TenantStatus.Active);
        var handler = new ListTenantHistoryHandler(reader, directory, new CountingUserDirectory(new Dictionary<Guid, string>()),
            new OperatorContext(Operator, OperatorPermissions.ModulesManage), new FixedOperatorTenant(Operator.Value),
            new ListTenantHistoryValidator());

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new ListTenantHistoryQuery(Operator, Target, null, 1, 25), TestContext.Current.CancellationToken));
        Assert.Empty(directory.Asked);
        Assert.Empty(reader.Asked);
    }

    private sealed class CountingUserDirectory(IReadOnlyDictionary<Guid, string> emails) : IUserDirectory
    {
        public int Calls { get; private set; }

        public Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(emails.GetValueOrDefault(userId));
        }
    }
}
