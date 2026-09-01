namespace Modules.Customers.Application;

/// <summary>
/// Deja el Excel exportado en el almacenamiento de objetos y devuelve el enlace con el que se
/// descarga.
///
/// Puerto en Application con adaptador en el composition root, por la misma razon que
/// <see cref="ICustomerGeographyLookup"/>: <c>Modules.Customers.Application</c> no puede referenciar
/// otro modulo de negocio —<c>CustomersLayerTests</c> lo verifica— y el almacenamiento vive en
/// <c>Modules.Storage</c>. Este modulo describe lo que necesita; donde termina el archivo y como se
/// firma el enlace es problema del adaptador.
/// </summary>
public interface ICustomerExportStorage
{
    Task<CustomerExportUpload> UploadAsync(
        Guid tenantId,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken);
}

/// <summary>
/// El enlace de descarga y hasta cuando sirve. La expiracion viaja porque el correo se la tiene que
/// decir al destinatario: un enlace que caduca sin aviso se lee como un error del sistema.
/// </summary>
public sealed record CustomerExportUpload(string DownloadUrl, DateTimeOffset ExpiresAt);
