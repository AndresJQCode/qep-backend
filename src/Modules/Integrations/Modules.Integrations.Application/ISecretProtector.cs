using System.Diagnostics.CodeAnalysis;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Application;

/// <summary>
/// Cifra y descifra los secretos de las conexiones (spec 2026-10-08, «Secreto en reposo»; era
/// <c>IWhatsAppSecretProtector</c> en 6612298). La implementación vive en Infrastructure y la llave
/// fuera de la base: un volcado de la base sola no alcanza para leer una credencial. Atado a conexión y
/// campo (D7). Nunca registra nada.
/// </summary>
public interface ISecretProtector
{
    /// <summary>La llave con la que se cifra; <c>null</c> si falta o está vacía.</summary>
    string? ActiveKeyId { get; }

    /// <summary>La llave está configurada (vacío = ausente). No dice que sea la que cifró una fila:
    /// para eso, <see cref="TryUnprotect"/>.</summary>
    bool HasKey(string keyId);

    /// <summary>Cifra con <see cref="ActiveKeyId"/>; sin ella lanza.</summary>
    ProtectedSecret Protect(Guid connectionId, string fieldKey, string plaintext);

    /// <summary>Descifra con la llave del <c>KeyId</c> del secreto; lanza si no puede.</summary>
    string Unprotect(Guid connectionId, string fieldKey, ProtectedSecret secret);

    /// <summary><c>false</c> —sin lanzar y sin registrar— con la llave ausente, bytes dañados, AAD de
    /// otra conexión o de otro campo, o una llave distinta con el mismo id.</summary>
    bool TryUnprotect(Guid connectionId, string fieldKey, ProtectedSecret secret, [NotNullWhen(true)] out string? plaintext);
}
