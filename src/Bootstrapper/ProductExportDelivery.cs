using System.Globalization;
using BuildingBlocks.Application;
using Modules.Catalog.Application;
using Modules.Identity.Application;
using Modules.Notifications.Application;
using Modules.Storage.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Entrega la exportacion del catalogo: sube el Excel a R2, firma un enlace de descarga y se lo
/// manda por correo a quien la pidio.
///
/// **Vive aca y no en Catalog** por la misma razon que <see cref="ProductImageLookup"/>: junta
/// Storage, Identity y Notifications, y ningun modulo de negocio referencia a otro. El
/// composition root ya referencia a los tres y su trabajo es exactamente cablearlos.
/// </summary>
internal sealed class ProductExportDelivery(
    IObjectStorage storage,
    IEmailChannel email,
    IUserDirectory users,
    IExecutionContext executionContext,
    IClock clock) : IProductExportDelivery
{
    private const string ContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// Cuanto vive el enlace. Es lo que se le promete a quien recibe el correo, asi que el
    /// numero esta aca y viaja en la respuesta: si cambia, cambia en un solo lugar.
    /// </summary>
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(24);

    public async Task<ProductExportDelivered> DeliverAsync(
        Guid tenantId,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken)
    {
        // Prefijo por tenant y carpeta propia: el objeto no comparte espacio de nombres con los
        // archivos que suben los usuarios, asi que una politica de retencion sobre `exports/`
        // no toca nada mas.
        var key = $"tenants/{tenantId}/exports/{Guid.CreateVersion7()}/{fileName}";

        await storage.UploadAsync(key, content, ContentType, cancellationToken);
        var expiresAt = clock.UtcNow.Add(LinkLifetime);

        var address = await users.GetEmailAsync(executionContext.SubjectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(address))
        {
            // El archivo ya quedo subido. No se falla la peticion: no hay nada que reintentar
            // —la persona no tiene correo— y el frontend avisa ese caso en vez de prometer un
            // envio que no va a ocurrir.
            return new ProductExportDelivered(expiresAt, EmailSent: false);
        }

        var link = await storage.CreatePresignedDownloadUrlAsync(key, cancellationToken);
        await email.SendAsync(BuildMessage(address, fileName, link, expiresAt), cancellationToken);

        return new ProductExportDelivered(expiresAt, EmailSent: true);
    }

    private static EmailMessage BuildMessage(
        string address, string fileName, Uri link, DateTimeOffset expiresAt)
    {
        // Cultura invariante: el correo lo arma el servidor y su locale no tiene nada que ver
        // con quien lo lee. Sin esto el formato cambiaria segun donde corra el proceso.
        var expires = expiresAt.ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var subject = $"Tu exportación de productos: {fileName}";

        var html =
            $"""
            <p>Tu exportación de productos está lista.</p>
            <p><a href="{link}">Descargar {fileName}</a></p>
            <p>El enlace vence el {expires}.</p>
            """;

        var text =
            $"""
            Tu exportación de productos está lista.

            Descargar {fileName}: {link}

            El enlace vence el {expires}.
            """;

        return new EmailMessage(address, subject, html, text);
    }
}
