using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.UnitTests;

/// <summary>
/// El inbox con reclamo (spec 2026-09-13, Sección 1), contra el modelo de EF y no contra una base.
/// El reclamo es SQL crudo (InboxClaims) que nombra las columnas a mano: un nombre que EF pusiera por
/// convención rompería el reclamo sin que el compilador lo vea. Mismo criterio que
/// QuotationsDbContextMappingTests.
/// </summary>
public sealed class NotificationsDbContextMappingTests
{
    [Fact]
    public void TheInboxCarriesTheClaimColumns()
    {
        // El factory de diseño arma el contexto sin abrir conexión: construir el modelo no la necesita.
        using var context = new NotificationsDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        var inbox = model.FindEntityType(typeof(NotificationInboxMessage));

        Assert.NotNull(inbox);
        Assert.Equal("inbox_messages", inbox.GetTableName());
        Assert.Equal("notifications", inbox.GetSchema());
        Assert.Equal(
            ["attempts", "claimed_until", "consumer", "message_id", "processed_at"],
            inbox.GetProperties().Select(property => property.GetColumnName()).Order(StringComparer.Ordinal));
    }

    // processed_at en null es "reclamado y sin terminar". attempts nace en 1 para que las filas que ya
    // existen —todas procesadas— queden con un intento sin que la migración tenga que tocarlas.
    [Fact]
    public void ProcessedAtAndTheLeaseAreNullableAndAttemptsDefaultsToOne()
    {
        using var context = new NotificationsDbContextFactory().CreateDbContext([]);
        var inbox = context.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(NotificationInboxMessage))!;

        Assert.True(inbox.FindProperty(nameof(NotificationInboxMessage.ProcessedAt))!.IsNullable);
        Assert.True(inbox.FindProperty(nameof(NotificationInboxMessage.ClaimedUntil))!.IsNullable);
        var attempts = inbox.FindProperty(nameof(NotificationInboxMessage.Attempts))!;
        Assert.False(attempts.IsNullable);
        Assert.Equal(1, attempts.GetDefaultValue());
    }
}
