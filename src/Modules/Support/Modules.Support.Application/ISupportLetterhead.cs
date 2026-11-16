using System.Text.Json.Serialization;

namespace Modules.Support.Application;

/// <summary>El logo del tenant para el membrete, PNG o JPEG (<c>qcode-pdf</c> no imprime WebP). <see cref="FileName"/> es el nombre del
/// asset (<c>logo.png</c>, <c>logo.jpg</c>) con que la plantilla lo pide. <see cref="Content"/> lleva <c>[JsonIgnore]</c>: viaja sólo como
/// asset del request, nunca en base64 dentro de <c>data</c> (mismo criterio que <c>QuotationPdfLogo</c>).</summary>
public sealed record SupportPdfLogo(string FileName, [property: JsonIgnore] byte[] Content);

/// <summary>Fase 4b (H5): lo que imprime el membrete de la respuesta formal. Sale de la empresa que responde (Companies), no del tenant,
/// que no tiene NIT, dirección ni correo; el logo sí es el del tenant.</summary>
public sealed record SupportLetterheadData(
    string CompanyName,
    string DocumentTypeCode,
    string DocumentNumber,
    string? Address,
    string? CityName,
    string? Email,
    string? Phone,
    SupportPdfLogo? Logo);

/// <summary>
/// Fase 4b (H5, owner 2026-11-16, Q-6): el membrete de la respuesta formal, con adaptador en Bootstrapper sobre Companies, Tenancy y
/// Storage (Soporte no los referencia).
/// </summary>
public interface ISupportLetterhead
{
    /// <summary>La empresa de la versión del formulario <paramref name="formVersionId"/> si se da, es del tenant y eligió una; si no, la
    /// de la versión vigente del tenant. Null sin empresa: la respuesta formal se rechaza con
    /// <c>support.reply.formal_organization_required</c>. Un logo que no se puede leer no falla: el membrete sale sin imagen.</summary>
    Task<SupportLetterheadData?> FindAsync(Guid tenantId, Guid? formVersionId, CancellationToken cancellationToken);

    /// <summary>Fase 4b, Tarea 8: si <see cref="FindAsync"/> daría un membrete, sin leer el logo. Lo usa <c>reply-options</c>, que se pide
    /// cada vez que se abre un ticket: bajar el logo de R2 para decir «organization_required» no vale lo que cuesta.</summary>
    Task<bool> ExistsAsync(Guid tenantId, Guid? formVersionId, CancellationToken cancellationToken);
}
