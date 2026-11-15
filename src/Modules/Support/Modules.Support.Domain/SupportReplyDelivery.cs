namespace Modules.Support.Domain;

/// <summary>Cómo salió una respuesta (plan E13 a E15): texto en el hilo, la plantilla «Aviso de respuesta» o correo. Se persiste
/// por nombre.</summary>
public enum SupportReplyDelivery
{
    Text,
    Template,
    Email,
}
