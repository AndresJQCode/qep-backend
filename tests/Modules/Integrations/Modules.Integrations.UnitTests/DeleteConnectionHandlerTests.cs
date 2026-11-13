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
        bed.BotOccupancy.Occupant = new ConnectionBotOccupant("agent", agentId, "Asesor virtual");

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
}
