namespace Modules.Quotations.Application;

/// <summary>
/// La copia pública de un comprobante de pago, para que el Excel de pedidos lo enlace (spec
/// 2026-09-15). Puerto y no configuración en el handler, mismo criterio que
/// <see cref="IWhatsAppSender"/>: el composition root registra la implementación que publica o la
/// que no hace nada según <c>Quotations:PaymentProofs:PublicLinks</c> (P3), y los handlers no leen
/// configuración. Las dos viven en el Bootstrapper, el único proyecto que ve Quotations y Storage.
///
/// Apagar la opción deja de publicar y de mostrar enlaces, pero no despublica lo que ya se copió.
/// </summary>
public interface IPaymentProofPublisher
{
    /// <summary>Copia el archivo al bucket público y devuelve la clave pública, o null si la
    /// opción está apagada.</summary>
    Task<string?> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>Borra una copia pública. Sólo para el rollback de P7, cuando el request falla
    /// después de copiar.</summary>
    Task DeleteAsync(string publicKey, CancellationToken cancellationToken);

    /// <summary>La URL pública de una clave, o null si la opción está apagada.</summary>
    string? UrlFor(string publicKey);
}
