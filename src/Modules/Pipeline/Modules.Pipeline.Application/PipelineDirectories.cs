namespace Modules.Pipeline.Application;

/// <summary>Spec 2026-10-13 §5.3: una conversación de Messaging como la ve Pipeline.</summary>
public sealed record PipelineConversation(Guid Id, Guid? CustomerId, bool IsOpen, DateTimeOffset? LastInboundAt, DateTimeOffset? LastMessageAt);

/// <summary>D37: a lo sumo el tope pedido en <see cref="Items"/>, y en <see cref="Count"/> el total real, para que el contador de la
/// bandeja diga la verdad aunque la lista se corte.</summary>
public sealed record PipelineConversationPage(int Count, IReadOnlyList<PipelineConversation> Items);

/// <summary>§5.2 y D21: el último mensaje de las conversaciones del cliente (entrante o saliente) y cuáles son.</summary>
public sealed record PipelineCustomerActivity(Guid CustomerId, DateTimeOffset? LastMessageAt, IReadOnlyList<Guid> ConversationIds);

/// <summary>D36 y §6: nombre y completitud del cliente; un <c>Incomplete</c> se acepta (H13).</summary>
public sealed record PipelineCustomerRef(Guid Id, string Name, bool IsComplete);

/// <summary>
/// D9: lo que Pipeline necesita de Messaging, con adaptador en Bootstrapper sobre <c>MessagingDbContext</c> (como
/// <c>QuotationsReportSource</c> lee <c>QuotationsDbContext</c>). Pipeline no referencia Messaging.
/// </summary>
public interface IPipelineConversationDirectory
{
    /// <summary>§5.1, D35 y D37: las pendientes de triage —abiertas, <b>con</b> cliente, sin negocio abierto del cliente y sin una
    /// decisión tomada contra su último entrante—, el entrante más reciente primero y las que no tienen entrante al final (P13). A
    /// lo sumo <paramref name="limit"/>, con el total real: el tope lo aplica la consulta, no la memoria (P16).</summary>
    Task<PipelineConversationPage> ListOpenAsync(Guid tenantId, int limit, CancellationToken cancellationToken);

    /// <summary>Por cliente, todas sus conversaciones (abiertas o no), la de actividad más reciente primero, y su último mensaje.</summary>
    Task<IReadOnlyDictionary<Guid, PipelineCustomerActivity>> FindActivityAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    Task<PipelineConversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
}

/// <summary>D9: adaptador en Bootstrapper sobre <c>ICustomerWhatsAppDirectory.FindRefsAsync</c>. Un id de otro tenant no aparece.</summary>
public interface IPipelineCustomerDirectory
{
    Task<IReadOnlyDictionary<Guid, PipelineCustomerRef>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>
    /// Spec 2026-10-24 §4 «lo que llega tarde»: el cliente que quedó si este se fusionó; el mismo id si no (o si no es del
    /// tenant). Un salto basta: la fusión reescribe la cadena (C → B → A queda C → A), así que nadie apunta a un fusionado.
    /// </summary>
    Task<Guid> ResolveSurvivorAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);
}

/// <summary>Mismo criterio que <c>IMessagingMemberNames</c>: nombre visible de la membresía o su correo, sólo para mostrar. Sólo las
/// del tenant, en cualquier estado (quitadas y suspendidas incluidas); la que no tiene nombre ni correo, o no es del tenant, no
/// aparece y quien llama decide qué mostrar. <b>No</b> sirve para saber si alguien es miembro: eso es
/// <see cref="IPipelineActiveMembers"/>.</summary>
public interface IPipelineMemberNames
{
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);
}

/// <summary>Quién puede ser dueño de un negocio (<c>pipeline.deal.owner_not_member</c>): las membresías del tenant en estado
/// <c>Active</c>. Una invitada todavía no puede entrar y una suspendida, quitada o vencida no debe recibir trabajo. Adaptador en
/// Bootstrapper sobre Tenancy; un id de otro tenant o inexistente no aparece.</summary>
public interface IPipelineActiveMembers
{
    Task<IReadOnlySet<Guid>> FindActiveAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);

    /// <summary>D21 del spec del frontend: todas las membresías <c>Active</c> del tenant, para el selector de dueño. Es la misma
    /// regla que <see cref="FindActiveAsync"/> aplica a unos ids. Sólo ids y sin orden: el nombre lo da
    /// <see cref="IPipelineMemberNames"/> y el orden lo decide quien llama.</summary>
    Task<IReadOnlyList<Guid>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);
}
