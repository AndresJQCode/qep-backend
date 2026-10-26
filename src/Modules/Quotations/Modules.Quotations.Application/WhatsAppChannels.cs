namespace Modules.Quotations.Application;

/// <summary>
/// La conexión de WhatsApp que el tenant eligió para enviar sus documentos (spec 2026-10-21 D5–D10): una por tenant,
/// guardada por <c>connection_id</c> y sin llave foránea, porque la conexión vive en Integrations.
/// </summary>
public interface IWhatsAppChannelChoiceStore
{
    /// <summary>La conexión guardada, o <c>null</c> si el tenant nunca eligió. Puede ser una que ya no esté disponible:
    /// quien la lee decide qué hacer con una elección vencida.</summary>
    Task<Guid?> FindAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Inserta, o reemplaza sólo si la fila guardada sigue siendo <paramref name="previous"/> (Decisión 9):
    /// quien eligió mientras otro envío elegía a la vez no pisa lo que el otro acaba de guardar. <c>true</c> si escribió.
    /// Corre dentro de la transacción del llamador.</summary>
    Task<bool> RememberAsync(
        Guid tenantId,
        Guid connectionId,
        Guid? previous,
        Guid chosenByMemberId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Upsert incondicional (el <c>PUT</c> desde Configuración): ahí la persona elige a conciencia y no hay
    /// elección anterior que respetar.</summary>
    Task ReplaceAsync(
        Guid tenantId,
        Guid connectionId,
        Guid chosenByMemberId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
