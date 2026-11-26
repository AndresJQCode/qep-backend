using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Wms.IntegrationTests.Tasks;
using static Modules.Wms.IntegrationTests.AdjustmentTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>
/// La sonda con la que WMS retiene a un usuario mientras el libro lo nombre, contra Postgres y resuelta del contenedor real,
/// como la consulta la limpieza de usuarios huérfanos. <c>stock_movements.actor_id</c> guarda el id de la <b>membresía</b>
/// (<c>WmsCaller.RequireAsync</c>); <c>adjustments.proposed_by/approved_by/rejected_by</c> guardan el id del <b>usuario</b>
/// (<c>SubjectId</c>): cada columna se prueba con su id.
/// </summary>
public sealed class WmsUserReferenceProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<bool> HasReferencesAsync(QepApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var probe = Assert.Single(scope.ServiceProvider.GetServices<IUserReferenceProbe>(), candidate => candidate.Source == "wms");
        return await probe.HasReferencesAsync(userId, Ct);
    }

    [Fact]
    public async Task AMovementActorRetainsTheUserThroughTheMembershipAndNotByTheMembershipIdAsAUser()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var user = Guid.Parse(SubjectId);
        await PostedAsync(s.Manager, s, s.BinA, 2m);
        var membershipId = await ScalarAsync<Guid>(database, "SELECT actor_id FROM wms.stock_movements LIMIT 1");

        // Los tres sellos del ajuste apuntan a otro: sólo el movimiento nombra al dueño.
        await ExecuteAsync(
            database,
            "UPDATE wms.adjustments SET proposed_by = @stranger, approved_by = @stranger, rejected_by = NULL",
            ("stranger", Guid.CreateVersion7()));

        Assert.True(await HasReferencesAsync(factory, user));
        Assert.False(await HasReferencesAsync(factory, Guid.CreateVersion7()));

        // actor_id es una membresía: ese mismo valor, leído como id de usuario, no retiene a nadie.
        Assert.NotEqual(user, membershipId);
        Assert.False(await HasReferencesAsync(factory, membershipId));
    }

    [Fact]
    public async Task EachAdjustmentStampRetainsTheUserEvenWithoutAnyMovement()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var user = Guid.Parse(SubjectId);
        var stranger = Guid.CreateVersion7();
        await ProposeAsync(s.Manager, Body(s, [Line(s.BinA, s.VariantId, 3m)]));
        Assert.Equal(0, await ScalarAsync<int>(database, "SELECT count(*)::int FROM wms.stock_movements"));

        async Task<bool> WithStampsAsync(string proposedBy, string approvedBy, string rejectedBy)
        {
            await ExecuteAsync(
                database,
                $"UPDATE wms.adjustments SET proposed_by = {proposedBy}, approved_by = {approvedBy}, rejected_by = {rejectedBy}",
                ("user", user),
                ("stranger", stranger));
            return await HasReferencesAsync(factory, user);
        }

        Assert.Equal(
            (true, true, true, false),
            (
                await WithStampsAsync("@user", "NULL", "NULL"),
                await WithStampsAsync("@stranger", "@user", "NULL"),
                await WithStampsAsync("@stranger", "NULL", "@user"),
                await WithStampsAsync("@stranger", "NULL", "NULL")));
    }

    // Las cuatro columnas del motor de tareas guardan la membresía, como actor_id.
    [Fact]
    public async Task AMemberNamedByATaskRetainsTheUser()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var user = Guid.Parse(SubjectId);
        Guid membership;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var directory = scope.ServiceProvider.GetRequiredService<IMembershipDirectory>();
            membership = Assert.Single(await directory.ListMembershipIdsByUserAsync(user, Ct));
        }

        var stranger = Guid.CreateVersion7();
        var destination = await TaskTestSupport.CreateDestinationAsync(s);
        await TaskTestSupport.SaveAsync(factory, TaskTestSupport.NewMove(s, s.BinA, destination, createdBy: stranger));

        // Deja todo a nombre de un extraño y, si se pide, una sola columna con la membresía.
        async Task<bool> WithMemberInAsync(string? update)
        {
            await ExecuteAsync(
                database,
                "UPDATE wms.tasks SET created_by = @stranger, assigned_member_id = NULL, claimed_by_member_id = NULL; "
                + "UPDATE wms.task_events SET member_id = @stranger; "
                + update,
                ("stranger", stranger),
                ("member", membership));
            return await HasReferencesAsync(factory, user);
        }

        Assert.Equal(
            (true, true, true, true, false),
            (
                await WithMemberInAsync("UPDATE wms.tasks SET created_by = @member"),
                await WithMemberInAsync("UPDATE wms.tasks SET assigned_member_id = @member"),
                await WithMemberInAsync("UPDATE wms.tasks SET claimed_by_member_id = @member"),
                await WithMemberInAsync("UPDATE wms.task_events SET member_id = @member"),
                await WithMemberInAsync(null)));
    }
}
