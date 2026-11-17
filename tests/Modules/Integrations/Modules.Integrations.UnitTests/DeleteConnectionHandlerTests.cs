using Modules.Integrations.Application;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Plan 2026-11-12 (correcciones de la revisión), Tarea 3, decisión del owner: borrar una conexión que atiende un chatbot o un agente
/// se bloquea con 409 y el ocupante, para que la pantalla diga a quién quitársela. No se limpia en cascada.
/// </summary>
public sealed class DeleteConnectionHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeletingAConnectionABotServesIsAConflictWithTheOccupant()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        var agentId = Guid.CreateVersion7();
        bed.BotOccupancy.Occupy(bed.TenantId, connection.Id, new ConnectionBotOccupant("agent", agentId, "Asesor virtual"));

        var error = await Assert.ThrowsAsync<ConnectionInUseByBotException>(() =>
            bed.DeleteHandler().HandleAsync(new DeleteConnectionCommand(bed.TenantId, connection.Id, 1), Ct));

        Assert.Equal("integrations.connection.in_use_by_bot", error.Code);
        Assert.Contains("Asesor virtual", error.Message, StringComparison.Ordinal);
        var occupant = error.ProblemExtensions["occupant"]!;
        Assert.Equal("agent", occupant.GetType().GetProperty("kind")!.GetValue(occupant));
        Assert.Equal(agentId, occupant.GetType().GetProperty("id")!.GetValue(occupant));
        Assert.Single(bed.Repository.Connections);
        Assert.Empty(bed.Events.Published);
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task DeletingAFreeConnectionStillWorks()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        Assert.True(await bed.DeleteHandler().HandleAsync(new DeleteConnectionCommand(bed.TenantId, connection.Id, 1), Ct));

        Assert.Empty(bed.Repository.Connections);
    }

    [Fact]
    public async Task AnOccupantOfAnotherConnectionOrAnotherTenantDoesNotBlockTheDelete()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        // The same connection id under another tenant, and another connection of the same tenant: neither is the one being deleted.
        bed.BotOccupancy.Occupy(Guid.CreateVersion7(), connection.Id, new ConnectionBotOccupant("agent", Guid.CreateVersion7(), "Other tenant"));
        bed.BotOccupancy.Occupy(bed.TenantId, Guid.CreateVersion7(), new ConnectionBotOccupant("chatbot", Guid.CreateVersion7(), "Other line"));

        Assert.True(await bed.DeleteHandler().HandleAsync(new DeleteConnectionCommand(bed.TenantId, connection.Id, 1), Ct));

        Assert.Empty(bed.Repository.Connections);
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    /// <summary>Plan 2026-11-12, Task 6: the check and the delete are one transaction, the one where the adapter takes the tenant's slot
    /// lock. Outside it the lock would be released right after the check, and a bot published in between would keep a dead connection.</summary>
    [Fact]
    public async Task TheOccupancyCheckRunsInsideTheTransactionThatDeletes()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        var checkedInsideTransaction = false;
        var connectionsAtCheck = -1;
        bed.BotOccupancy.OnFind = () =>
        {
            checkedInsideTransaction = bed.UnitOfWork.InTransaction;
            connectionsAtCheck = bed.Repository.Connections.Count;
        };

        Assert.True(await bed.DeleteHandler().HandleAsync(new DeleteConnectionCommand(bed.TenantId, connection.Id, 1), Ct));

        Assert.True(checkedInsideTransaction);
        Assert.Equal(1, connectionsAtCheck);
        Assert.Equal(1, bed.UnitOfWork.Transactions);
        Assert.True(bed.UnitOfWork.SavedInsideTransaction);
        Assert.False(bed.UnitOfWork.InTransaction);
    }

    [Fact]
    public async Task AConflictDisposesTheTransactionWithoutSaving()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.BotOccupancy.Occupy(bed.TenantId, connection.Id, new ConnectionBotOccupant("chatbot", Guid.CreateVersion7(), "Bot de ventas"));

        await Assert.ThrowsAsync<ConnectionInUseByBotException>(() =>
            bed.DeleteHandler().HandleAsync(new DeleteConnectionCommand(bed.TenantId, connection.Id, 1), Ct));

        Assert.Equal(1, bed.UnitOfWork.Transactions);
        Assert.False(bed.UnitOfWork.InTransaction);
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }
}
