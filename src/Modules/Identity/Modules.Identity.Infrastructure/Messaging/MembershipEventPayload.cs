using System.Text.Json;

namespace Modules.Identity.Infrastructure.Messaging;

/// <summary>
/// Lo único que Identity lee de los eventos de membresía de Tenancy
/// (<c>tenancy.membership-suspended.v1</c>, <c>tenancy.membership-removed.v1</c>): el id del
/// usuario afectado. Por nombre de propiedad, porque el payload lo serializa Tenancy.
/// </summary>
internal static class MembershipEventPayload
{
    public static Guid ReadUserId(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.GetProperty("userId").GetGuid();
    }

    /// <summary>
    /// Para los logs de falla: el payload puede ser justo lo que hace fallar al mensaje, así que
    /// leerlo no puede lanzar. Las excepciones son las de <c>JsonDocument.Parse</c> y de
    /// <c>GetProperty</c> y <c>GetGuid</c> con un campo ausente, de otro tipo o con un Guid
    /// inválido.
    /// </summary>
    public static Guid? TryReadUserId(string payloadJson)
    {
        try
        {
            return ReadUserId(payloadJson);
        }
        catch (Exception exception) when (exception is JsonException
            or KeyNotFoundException
            or FormatException
            or InvalidOperationException)
        {
            return null;
        }
    }
}
