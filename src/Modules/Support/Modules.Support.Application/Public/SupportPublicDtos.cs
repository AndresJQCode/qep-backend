namespace Modules.Support.Application;

// El contrato del formulario público (plan, «Contrato para las pantallas», Públicas). Lo lee la página de soporte en el navegador,
// sin BFF: los nombres de los campos son contrato.

/// <summary>La consulta de un ticket: radicado (con o sin prefijo), PIN y el token de Turnstile. El PIN viaja en el cuerpo y no en la
/// URL, para que no quede en logs (plan F9).</summary>
public sealed record SupportPublicLookupRequest(string? Number, string? Pin, string? TurnstileToken);
