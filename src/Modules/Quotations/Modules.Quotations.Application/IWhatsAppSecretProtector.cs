using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Cifra y descifra la API key de la cuenta propia de WhatsApp de un tenant (spec 2026-10-07,
/// «Manejo del secreto»). La implementación vive en Infrastructure y la llave fuera de la base:
/// un volcado de la base sola no alcanza para leer una key (criterio 6).
/// </summary>
public interface IWhatsAppSecretProtector
{
    /// <summary>La llave con la que se cifra; <c>null</c> si falta o está vacía.</summary>
    string? ActiveKeyId { get; }

    /// <summary>La llave está configurada (vacío = ausente). No dice que sea la que cifró una
    /// fila: para eso, <see cref="TryUnprotect"/>.</summary>
    bool HasKey(string keyId);

    /// <summary>Cifra con <see cref="ActiveKeyId"/>; sin ella lanza.</summary>
    ProtectedSecret Protect(Guid tenantId, string plaintext);

    /// <summary>Descifra con la llave del <c>KeyId</c> del secreto; lanza si no puede.</summary>
    string Unprotect(Guid tenantId, ProtectedSecret secret);

    /// <summary><c>false</c> —sin lanzar y sin loguear— con la llave ausente, bytes dañados, AAD
    /// de otro tenant o una llave distinta con el mismo id.</summary>
    bool TryUnprotect(Guid tenantId, ProtectedSecret secret, out string? plaintext);
}
