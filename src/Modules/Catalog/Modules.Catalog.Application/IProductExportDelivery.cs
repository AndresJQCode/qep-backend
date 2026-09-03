namespace Modules.Catalog.Application;

/// <summary>
/// Entrega el Excel ya armado: lo sube al almacenamiento, firma un enlace de descarga y se lo
/// manda por correo a quien lo pidio.
///
/// Un solo puerto para los tres pasos, y declarado aca, por la regla de siempre: ningun modulo
/// de negocio referencia a otro. Storage, Notifications e Identity los conoce el
/// <c>Bootstrapper</c>, que es quien implementa esto y el unico que puede referenciar a los
/// tres. Catalog solo sabe "entregame este archivo a esta persona".
/// </summary>
public interface IProductExportDelivery
{
    Task<ProductExportDelivered> DeliverAsync(
        Guid tenantId,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken);
}

/// <summary>
/// Hasta cuando sirve el enlace, y si el correo llego a salir.
///
/// <paramref name="EmailSent"/> en false no es un error: significa que el archivo quedo subido
/// pero la persona no tiene correo registrado. El endpoint lo devuelve igual con 202 y el
/// frontend ya distingue ese caso (avisa que no va a poder enviarlo), que es mejor que fallar
/// la peticion entera por algo que no se arregla reintentando.
/// </summary>
public sealed record ProductExportDelivered(DateTimeOffset ExpiresAt, bool EmailSent);
