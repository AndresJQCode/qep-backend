using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-10 §8.2: quien escribió, como lo ve Messaging (el <c>waId</c> son los dígitos sin «+»).</summary>
/// <param name="Channel">Spec 2026-10-14 decisión 10: en Instagram <c>UserId</c> es el IGSID y el cliente se empareja por
/// <c>instagram_user_id</c>; nunca por teléfono, que no viene.</param>
public sealed record MessagingContact(
    string UserId, string? WaId, string? ProfileName, string? Username, ConversationChannel Channel = ConversationChannel.WhatsApp);

/// <summary><c>Created</c> decide el evento: <c>CustomerCreated</c> si se creó, si no <c>CustomerLinked</c> (§8.2).</summary>
public sealed record MessagingEnsuredCustomer(Guid CustomerId, bool Created);

/// <summary>Spec 2026-10-11 §4.4: cómo escribirle a un cliente. <c>PhoneE164</c> con «+» (el <c>waId</c> son sus dígitos) y
/// <c>WhatsAppUserId</c> es el BSUID; sin ninguno de los dos no se le puede escribir. <c>MarketingOptOut</c> es la bandera de
/// la ficha: las plantillas <c>MARKETING</c> lo excluyen y las <c>UTILITY</c> no.</summary>
public sealed record MessagingCustomerContact(
    Guid CustomerId, string Name, string? PhoneE164, string? WhatsAppUserId, bool IsActive, bool MarketingOptOut);

/// <summary>Spec 2026-10-11 §4.4: cuántos tiene una audiencia y por qué algunos no recibirían nada. Cubos excluyentes y con
/// precedencia —inactivo, luego sin WhatsApp, luego con opt-out—: «le llegará a» es <c>Total - Inactive - WithoutWhatsApp</c>,
/// y a eso se le resta <c>OptedOut</c> sólo si la plantilla es <c>MARKETING</c>.</summary>
public sealed record MessagingAudienceCount(int Total, int Inactive, int OptedOut, int WithoutWhatsApp);

/// <summary>Spec 2026-10-11 §6.2: una etiqueta como la dibuja el detalle de una campaña. <c>Color</c> viaja por nombre
/// (<c>gray</c>, <c>blue</c>…), igual que <c>TagDto</c> de Customers.</summary>
public sealed record MessagingTagRef(Guid Id, string Name, string Color);

/// <summary>Spec 2026-10-09 §6.5: los clientes de QEP por teléfono, con adaptador en Bootstrapper sobre
/// <c>ICustomerPhoneDirectory</c> (Messaging no referencia Customers). El <c>waId</c> de Meta son los
/// dígitos del E.164; el adaptador traduce. «Nombres y clientes» de §7.6: una consulta por página.</summary>
public interface IMessagingCustomerDirectory
{
    /// <summary>Un cliente por <c>waId</c> (D-M7); los que no tienen cliente no aparecen. Sólo para conversaciones viejas
    /// sin <c>customer_id</c> (spec 2026-10-10 §6.1.2).</summary>
    Task<IReadOnlyDictionary<string, CustomerRefDto>> MatchAsync(Guid tenantId, IReadOnlyCollection<string> waIds, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-10 §6.1.2: nombre e isComplete de los clientes de una página, por id.</summary>
    Task<IReadOnlyDictionary<Guid, CustomerRefDto>> FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>§6.1.6: los clientes cuyo nombre contiene el término (con tope), para <c>customer_id = ANY(@customerIds)</c>.</summary>
    Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, CancellationToken cancellationToken);

    /// <summary>Los <c>waId</c> de los clientes cuyo nombre contiene <paramref name="term"/>, para el
    /// <c>wa_id = ANY(@phones)</c> de la búsqueda de la lista (§8.7). Con tope.</summary>
    Task<IReadOnlyList<string>> FindWaIdsByNameAsync(Guid tenantId, string term, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-10 §8.2: el cliente de QEP de esta persona, creándolo incompleto si no existe. Corre antes
    /// y fuera de la transacción de la ingesta; es idempotente por BSUID.</summary>
    Task<MessagingEnsuredCustomer> EnsureAsync(Guid tenantId, MessagingContact contact, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-10 §8.3: el cliente con el BSUID anterior pasa al nuevo. Si el nuevo ya es de otro cliente no
    /// se toca (lo registra el adaptador). El teléfono del cliente no cambia: es dato maestro.</summary>
    Task ReplaceUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-11 §4.4: el cliente del tenant para escribirle, o <c>null</c> si no existe en este tenant.</summary>
    Task<MessagingCustomerContact?> FindContactAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>§4.4: los clientes que tienen alguna de las etiquetas, más los sueltos, sin duplicados y ordenados por id. Se
    /// entrega por lotes de 500 internamente (paginado por id): quien consume decide cuándo parar y cuántos acumula. Incluye
    /// inactivos, sin WhatsApp y con opt-out; omitirlos y con qué motivo es de la campaña.</summary>
    IAsyncEnumerable<MessagingCustomerContact> ListAudienceAsync(
        Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>§4.4: el conteo de la misma audiencia que <see cref="ListAudienceAsync"/>.</summary>
    Task<MessagingAudienceCount> CountAudienceAsync(
        Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-11 §6.2: nombre y color de las etiquetas del tenant entre las pedidas. Una borrada (o de otro tenant) no
    /// aparece: el detalle de la campaña la omite.</summary>
    Task<IReadOnlyList<MessagingTagRef>> FindTagsAsync(Guid tenantId, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken);

    /// <summary>§4.4 y decisión 10: Meta respondió 131050. Idempotente; el origen queda en <c>meta</c>.</summary>
    Task MarkMarketingOptOutAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>Decisión del owner 2026-10-12 (P1): la persona tocó el botón de respuesta rápida «Detener promociones». Idempotente; el
    /// origen queda en <c>button</c>.</summary>
    Task MarkMarketingOptOutByButtonAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);
}
