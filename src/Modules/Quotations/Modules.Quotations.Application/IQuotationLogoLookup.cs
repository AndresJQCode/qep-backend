namespace Modules.Quotations.Application;

/// <summary>
/// El logo del tenant, para imprimirlo en el PDF de la cotización (decisión 9 del spec
/// 2026-09-19). Lee los bytes del <b>original privado</b> con <c>IObjectStorage.DownloadAsync</c>
/// y no la URL pública por HTTP: el objeto privado es el canónico, mismo criterio que
/// <see cref="QuotationPdf.StorageKey"/> con el PDF ya generado — evita un egreso de red nuevo y
/// no depende de que el bucket público esté configurado en ese ambiente.
/// </summary>
public interface IQuotationLogoLookup
{
    /// <summary>`null` sin logo, o si el archivo no está <c>Available</c> (alguien lo borró por
    /// Storage): el PDF sale sin logo en vez de fallar.</summary>
    Task<QuotationLogoRef?> FindAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<byte[]> ReadAsync(QuotationLogoRef reference, CancellationToken cancellationToken);
}

/// <summary><paramref name="Extension"/> sale del <c>MimeType</c>, con punto (`.png`, `.jpg`,
/// `.webp`) — los mismos tres tipos que <c>TenantLogoStorage</c> admite al asignar el logo.
/// </summary>
public sealed record QuotationLogoRef(Guid FileId, string StorageKey, string Extension);
