namespace Modules.Support.Domain;

/// <summary>Por dónde se le responde al solicitante (plan D8, spec §8.3). <see cref="Portal"/> (fase 3, plan F12) es la página de
/// consulta del formulario público: la respuesta se publica, no se envía. Es el único canal de un ticket anónimo y sólo existe para
/// ellos. Se persiste por nombre: un valor nuevo va al final.</summary>
public enum SupportReplyChannel
{
    Thread,
    WhatsAppTemplate,
    Email,
    Portal,
}
