using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §5: lista y detalle, con la doble capa y el 403 antes del 404.</summary>
public sealed class OperatorReadHandlerTests
{
    private static readonly TenantId Operator = TenantId.New();
    private static readonly TenantId Target = TenantId.New();

    private static GetOperatorTenantHandler Detail(FixedOperatorTenantReader reader, IExecutionContext context) =>
        new(reader, context, new FixedOperatorTenant(Operator.Value));

    private static ListOperatorTenantsHandler List(FixedOperatorTenantReader reader, IExecutionContext context) =>
        new(reader, context, new FixedOperatorTenant(Operator.Value), new ListOperatorTenantsValidator());

    // §5, «Orden de chequeos»: el 404 no le sirve de oráculo a quien no es operador. Escenarios por
    // nombre y no TheoryData<IExecutionContext>: xUnit1045 (dato no serializable) es error acá.
    [Theory]
    [InlineData("claim-is-not-the-operator")]
    [InlineData("no-permission")]
    [InlineData("route-is-not-the-claim")]
    public async Task ANonOperatorIsForbiddenBeforeLookingUpTheTarget(string scenario)
    {
        var reader = new FixedOperatorTenantReader();
        var (context, routeTenant) = scenario switch
        {
            "claim-is-not-the-operator" => (new OperatorContext(Target, OperatorPermissions.TenantsRead), Target),
            "no-permission" => (new OperatorContext(Operator), Operator),
            _ => (new OperatorContext(TenantId.New(), OperatorPermissions.TenantsRead), Operator),
        };

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Detail(reader, context).HandleAsync(new GetOperatorTenantQuery(routeTenant, TenantId.New()), TestContext.Current.CancellationToken));
        Assert.Empty(reader.Asked);
    }

    [Fact]
    public async Task AMissingTargetIsNotFoundForTheOperator()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Detail(new FixedOperatorTenantReader(), new OperatorContext(Operator, OperatorPermissions.TenantsRead))
                .HandleAsync(new GetOperatorTenantQuery(Operator, Target), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.tenant.not_found", error.Code);
    }

    [Fact]
    public async Task TheDetailListsEveryModuleInCatalogOrder()
    {
        var reader = new FixedOperatorTenantReader();
        var modules = OperatorSnapshots.Signup()
            .Select(module => module.Key == TenantModuleKeys.Customers
                ? module with { Status = TenantModuleStatus.Inactive }
                : module);
        var suspendedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        reader.Snapshots[Target] = OperatorSnapshots.Of(
            Target, modules, TenantStatus.Suspended,
            new Dictionary<TenantModuleKey, ChangeReason> { [TenantModuleKeys.Customers] = ChangeReason.Cancellation },
            new TenantStatusChange(suspendedAt, ChangeReason.Nonpayment));

        var detail = await Detail(reader, new OperatorContext(Operator, OperatorPermissions.TenantsRead))
            .HandleAsync(new GetOperatorTenantQuery(Operator, Target), TestContext.Current.CancellationToken);

        Assert.Equal(["catalog", "customers", "companies", "quotations", "orders", "reporting", "pos", "messaging"],
            detail.Modules.Select(module => module.Key));
        var byKey = detail.Modules.ToDictionary(module => module.Key);
        Assert.Equal("inactive", byKey["customers"].Status);
        Assert.Equal("cancellation", byKey["customers"].LastReason);
        Assert.Equal("active", byKey["quotations"].Status);
        Assert.False(byKey["quotations"].Enabled);                 // efectivo: le falta customers
        Assert.Equal(["catalog", "customers", "companies"], byKey["quotations"].Dependencies);
        Assert.Equal("none", byKey["pos"].Status);
        Assert.Null(byKey["pos"].Since);
        Assert.Null(byKey["pos"].Source);
        Assert.Equal("Suspended", detail.Status);
        Assert.Equal(suspendedAt, detail.StatusChangedAt);
        Assert.Equal("nonpayment", detail.StatusReason);
        Assert.False(detail.IsOperator);
    }

    [Fact]
    public async Task TheListCarriesTotalModulesTheOperatorMarkAndTheSummary()
    {
        var reader = new FixedOperatorTenantReader
        {
            Listing = new(
                [new OperatorTenantRow(Operator.Value, "qcode", "QCode", TenantStatus.Active, DateTimeOffset.UnixEpoch, 7),
                 new OperatorTenantRow(Target.Value, "origen", "Origen", TenantStatus.Suspended, DateTimeOffset.UnixEpoch, 0)],
                Total: 2, AllTenants: 6, WithoutModules: 1, Inactive: 1),
        };

        var page = await List(reader, new OperatorContext(Operator, OperatorPermissions.TenantsRead))
            .HandleAsync(new ListOperatorTenantsQuery(Operator, "o", 1, 25), TestContext.Current.CancellationToken);

        Assert.Equal(2, page.Total);
        Assert.Equal(new OperatorTenantSummaryDto(6, 1, 1), page.Summary);
        Assert.All(page.Items, item => Assert.Equal(8, item.TotalModules));
        Assert.True(page.Items[0].IsOperator);
        Assert.Equal("Suspended", page.Items[1].Status);
        Assert.Equal(["list:o:1:25"], reader.Asked);
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]   // (page - 1) * pageSize daría la vuelta a un OFFSET negativo: 500
    public async Task OutOfRangePagingIsAValidationError(int page, int pageSize) =>
        await Assert.ThrowsAsync<ValidationException>(() =>
            List(new FixedOperatorTenantReader(), new OperatorContext(Operator, OperatorPermissions.TenantsRead))
                .HandleAsync(new ListOperatorTenantsQuery(Operator, null, page, pageSize), TestContext.Current.CancellationToken));

    [Fact]
    public async Task ASearchLongerThanOneHundredIsAValidationError() =>
        await Assert.ThrowsAsync<ValidationException>(() =>
            List(new FixedOperatorTenantReader(), new OperatorContext(Operator, OperatorPermissions.TenantsRead))
                .HandleAsync(new ListOperatorTenantsQuery(Operator, new string('a', 101), 1, 25), TestContext.Current.CancellationToken));
}
