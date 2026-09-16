namespace BuildingBlocks.Application;

/// <summary>
/// Cómo un módulo declara que todavía referencia un objeto del bucket público. Storage la consulta
/// antes de borrar un objeto de <c>payment-proofs/</c> que parece huérfano (spec 2026-09-16, D12) y
/// no lo borra mientras alguna sonda responda <c>true</c>: su URL puede estar en un Excel ya enviado.
/// </summary>
/// <remarks>
/// Mismo diseño que <see cref="IUserReferenceProbe"/> y <see cref="IFileReferenceProbe"/>. Storage
/// registra la suya para <c>FileResource.PublicStorageKey</c> y Quotations la suya para
/// <c>OrderPaymentProof.PublicStorageKey</c>, lo que cubre los comprobantes de v1 y de v2.
/// </remarks>
public interface IPublicObjectReferenceProbe
{
    /// <summary>Nombre del módulo que responde, para el log de por qué se conservó el objeto.</summary>
    string Source { get; }

    Task<bool> HasReferencesAsync(string publicStorageKey, CancellationToken cancellationToken);
}
