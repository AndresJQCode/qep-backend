namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-12 §3.6: una respuesta rápida. <c>shortcut</c> viaja sin <c>/</c>; la pantalla lo dibuja con la barra.</summary>
public sealed record QuickReplyDto(Guid Id, string Shortcut, string Body, DateTimeOffset UpdatedAt, long Version);

/// <summary>BFF (D-Q5): la SPA no conoce su nombre de asesor (<c>/auth/me</c> sólo da <c>userId</c>, mismo motivo que
/// <c>AssignedToDto.IsMe</c>) ni tiene garantizado el del tenant en la caja. Sin nombre viajan <c>""</c>, nunca <c>null</c>: el
/// compositor reemplaza la variable por vacío y el asesor lo corrige antes de enviar.</summary>
public sealed record QuickReplyVariablesDto(string Asesor, string Empresa);

/// <summary>D-Q7: la lista completa, sin paginar y por <c>shortcut</c>: el compositor filtra en memoria mientras se escribe.</summary>
public sealed record QuickReplyListDto(IReadOnlyList<QuickReplyDto> Items, QuickReplyVariablesDto Variables);
