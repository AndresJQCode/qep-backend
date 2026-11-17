namespace Modules.Support.Domain;

/// <summary>Cómo salió una respuesta (plan E13 a E15): texto en el hilo, la plantilla «Aviso de respuesta» o correo. <see cref="Portal"/>
/// (fase 3, plan F12) no sale: queda publicada en la página de consulta. Se persiste por nombre.</summary>
public enum SupportReplyDelivery
{
    Text,
    Template,
    Email,
    Portal,
}
