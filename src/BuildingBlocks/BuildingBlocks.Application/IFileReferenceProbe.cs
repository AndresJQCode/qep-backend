namespace BuildingBlocks.Application;

/// <summary>
/// Cómo un módulo declara que todavía referencia un archivo de Storage. Storage la consulta antes de
/// purgar un comprobante de pago que sigue en staging/ (spec 2026-09-16, D11) y no lo purga mientras
/// alguna sonda responda <c>true</c>: un comprobante adjunto cuyo movimiento todavía no se procesó
/// está referenciado aunque no tenga clave pública.
/// </summary>
/// <remarks>
/// Mismo diseño que <see cref="IUserReferenceProbe"/>: vive en BuildingBlocks para que Storage decida
/// sin referenciar a los módulos de negocio, y cada módulo registra la suya sin referenciar a Storage.
/// </remarks>
public interface IFileReferenceProbe
{
    /// <summary>Nombre del módulo que responde, para el log de por qué se retuvo el archivo.</summary>
    string Source { get; }

    Task<bool> HasReferencesAsync(Guid fileId, CancellationToken cancellationToken);
}
