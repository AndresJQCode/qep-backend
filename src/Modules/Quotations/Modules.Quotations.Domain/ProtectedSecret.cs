namespace Modules.Quotations.Domain;

/// <summary>
/// Un secreto cifrado y el id de la llave que lo cifró (spec 2026-10-07, «Manejo del secreto»).
/// Opaco para el dominio: cifrar y descifrar es de <c>IWhatsAppSecretProtector</c>, en
/// Infrastructure; acá sólo se guarda y se reemplaza.
/// </summary>
public sealed record ProtectedSecret(string KeyId, byte[] Ciphertext)
{
    // Spec 2026-10-07: una mitad nula no existe. Sin esto, una fila con una sola de las dos columnas
    // llegaría hasta TryUnprotect, que promete no lanzar. Se redeclaran las propiedades posicionales
    // para validar al construir (el CHECK token_pair ya impide la fila a medias en la base).
    public string KeyId { get; init; } = KeyId ?? throw new ArgumentNullException(nameof(KeyId));

    public byte[] Ciphertext { get; init; } = Ciphertext ?? throw new ArgumentNullException(nameof(Ciphertext));

    /// <summary>El ancho de <c>api_token_key_id</c> y del patrón de ids <c>^[a-z0-9]{1,32}$</c>.</summary>
    public const int KeyIdMaxLength = 32;

    // El ToString de un record imprime todas sus propiedades. Los bytes no le sirven a nadie en un
    // log y no hay por qué sacarlos de la base.
    public override string ToString() => $"ProtectedSecret {{ KeyId = {KeyId} }}";
}
