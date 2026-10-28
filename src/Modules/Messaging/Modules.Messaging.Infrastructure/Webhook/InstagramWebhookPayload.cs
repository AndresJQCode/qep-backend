namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Spec 2026-10-14 §6.2: una entrada del webhook de Instagram (<c>entry[]</c>): la cuenta profesional (<c>entry[].id</c>, el
/// <c>igUserId</c> de la conexión) y sus eventos de <c>messaging[]</c>, en orden.</summary>
internal sealed record InstagramEntry(string AccountId, IReadOnlyList<InstagramEvent> Events);

internal abstract record InstagramEvent;

/// <summary>Un mensaje del hilo, ya con el <c>kind</c> del mapa de §6.2. <paramref name="IsEcho"/>: lo mandó el negocio desde la app de
/// Instagram (o desde QEP; la ingesta lo deduplica por <c>mid</c>); <c>Message.UserId</c> es siempre la persona.</summary>
internal sealed record InstagramMessageEvent(InboundMessage Message, bool IsEcho) : InstagramEvent;

/// <summary><c>messaging_seen</c>: la persona leyó hasta <paramref name="Mid"/> (nuestro).</summary>
internal sealed record InstagramSeenEvent(string UserId, string Mid) : InstagramEvent;

/// <summary><c>is_deleted</c>: la persona borró su mensaje <paramref name="Mid"/>.</summary>
internal sealed record InstagramDeletedEvent(string UserId, string Mid) : InstagramEvent;

/// <summary>Lo que no tiene la forma documentada o no se procesa (<c>unreact</c>, un emisor que no es IGSID, un evento sin cuerpo).</summary>
internal sealed record InstagramIgnoredEvent(string Reason) : InstagramEvent;
