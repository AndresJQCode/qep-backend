using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Colas de la plataforma: doble capa de operador y mapeo de las filas tal como las entrega la fuente.</summary>
public sealed class GetOperatorQueuesHandlerTests
{
    private static readonly TenantId Operator = TenantId.New();
    private static readonly TenantId Other = TenantId.New();

    private static GetOperatorQueuesHandler Build(FixedOperatorQueueSource source, IExecutionContext context) =>
        new(source, context, new FixedOperatorTenant(Operator.Value));

    [Theory]
    [InlineData("other-tenant")]
    [InlineData("no-permission")]
    [InlineData("not-operator")]
    [InlineData("usage-permission-only")]
    public async Task AWrongTenantMissingPermissionOrNonOperatorIsForbidden(string scenario)
    {
        var source = new FixedOperatorQueueSource();
        var (context, routeTenant) = scenario switch
        {
            "other-tenant" => (new OperatorContext(Other, OperatorPermissions.QueuesRead), Operator),
            "no-permission" => (new OperatorContext(Operator), Operator),
            "usage-permission-only" => (new OperatorContext(Operator, OperatorPermissions.UsageRead), Operator),
            _ => (new OperatorContext(Other, OperatorPermissions.QueuesRead), Other),
        };

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Build(source, context).HandleAsync(new GetOperatorQueuesQuery(routeTenant), TestContext.Current.CancellationToken));
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task TheRowsTravelInOrderWithTheirNullsUntouched()
    {
        var source = new FixedOperatorQueueSource
        {
            Rows =
            [
                new("webhook", 3, 1, 12.5),
                new("campaign", 0, 0, null),
                new("media", null, null, null),
            ],
        };

        var result = await Build(source, new OperatorContext(Operator, OperatorPermissions.QueuesRead))
            .HandleAsync(new GetOperatorQueuesQuery(Operator), TestContext.Current.CancellationToken);

        Assert.Equal(source.Rows, result.Queues);
        Assert.Equal(["webhook", "campaign", "media"], result.Queues.Select(queue => queue.Queue));
        Assert.Null(result.Queues[2].Backlog);
        Assert.Equal(0, result.Queues[1].Backlog);
        Assert.Equal(1, source.Calls);
    }
}

internal sealed class FixedOperatorQueueSource : IOperatorQueueSource
{
    public IReadOnlyList<OperatorQueueDto> Rows { get; set; } = [];
    public int Calls { get; private set; }

    public Task<IReadOnlyList<OperatorQueueDto>> ListAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Rows);
    }
}
