using Microsoft.EntityFrameworkCore;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Persistence;
using Modules.Messaging.Infrastructure.Coexistence;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-29: Integrations publica <see cref="ConnectionEvents.WhatsAppCoexistenceConnected"/> y Messaging lo consume con su propia
/// copia del nombre y del lector del payload, porque ningún módulo referencia al otro. Si un lado renombra el evento o un campo, el
/// consumidor deja de recibirlo o lo marca procesado sin efecto, sin error en ningún lado. Este proyecto ve los dos módulos.
/// </summary>
public sealed class CoexistenceEventParityTests
{
    [Fact]
    public void TheEventNameIsTheOneMessagingConsumes() =>
        Assert.Equal(ConnectionEvents.WhatsAppCoexistenceConnected, CoexistenceEventsProcessor.ConnectedEvent);

    // El literal del contrato: un cambio en las dos copias a la vez también tiene que ser deliberado.
    [Fact]
    public void TheEventNameIsTheOneInTheContract() =>
        Assert.Equal("integrations.whatsapp-coexistence-connected.v1", ConnectionEvents.WhatsAppCoexistenceConnected);

    [Fact]
    public void MessagingReadsThePayloadThatIntegrationsWrites()
    {
        var now = new DateTimeOffset(2026, 10, 29, 15, 30, 0, TimeSpan.Zero);
        var options = new DbContextOptionsBuilder<IntegrationsDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var dbContext = new IntegrationsDbContext(options);
        var connection = IntegrationConnection.Create(
            IntegrationProviders.Zenvia,
            Guid.NewGuid(),
            "WhatsApp",
            new Dictionary<string, string> { [ZenviaFieldKeys.FromNumber] = "573001234567", [ZenviaFieldKeys.TemplateId] = "d0e79149-ed61-4784-9883-d86807f46ce5" },
            new Dictionary<string, string> { [ZenviaFieldKeys.ApiToken] = "token" },
            (_, _, _) => new ProtectedSecret("k1", [1, 2, 3]),
            Guid.NewGuid(),
            now);

        new IntegrationsEventPublisher(dbContext).Publish(ConnectionEvents.WhatsAppCoexistenceConnected, connection, now);

        var written = Assert.Single(dbContext.ChangeTracker.Entries<IntegrationsOutboxMessage>()).Entity;
        Assert.True(CoexistenceEventPayload.TryRead(written.PayloadJson, out var tenantId, out var connectionId, out var occurredAt));
        Assert.Equal(connection.TenantId, tenantId);
        Assert.Equal(connection.Id, connectionId);
        Assert.Equal(now, occurredAt);
    }
}
