using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;

namespace Modules.Messaging.UnitTests;

/// <summary>Revisión final (spec 2026-11-17): la sincronización que pierde la carrera suelta lo pendiente del espejo, y con ello los avisos
/// de liberación de la imagen de encabezado que agregó al eliminar filas. Si la eliminación se suelta y el aviso no, el siguiente guardado
/// del request (el llenado implícito sigue) liberaría la imagen de una plantilla que sigue existiendo. Sin base: sólo el tracker.</summary>
public sealed class DiscardMirrorChangesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ALostSyncDropsItsReleasesWithItsRemovals()
    {
        using var context = new MessagingDbContextFactory().CreateDbContext([]);
        var tenantId = Guid.CreateVersion7();
        var removed = MessageTemplate.Mirrored(tenantId, Guid.CreateVersion7(), "1", "promo", "es", "MARKETING", "NAMED", "APPROVED", null, "[]", Now);
        context.Attach(removed);
        context.Remove(removed);
        new TemplateHeaderImageReleasePublisher(context).Publish(tenantId, Guid.CreateVersion7(), Now);
        var otherEvent = new MessagingOutboxRecord { Id = Guid.CreateVersion7(), EventName = "messaging.something-else.v1", CorrelationId = "c", OccurredAt = Now };
        context.Add(otherEvent);

        MessagingSaveChanges.DiscardMirrorChanges(context);

        var left = context.ChangeTracker.Entries().ToList();
        Assert.Equal(otherEvent, Assert.Single(left).Entity);
        Assert.Equal(EntityState.Added, left[0].State);
    }
}
