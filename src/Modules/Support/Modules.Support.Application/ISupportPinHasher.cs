namespace Modules.Support.Application;

/// <summary>
/// El hash de la clave de consulta (plan F8): PBKDF2 en Infrastructure, no SHA-256, porque seis dígitos se invierten con un millón
/// de intentos. <see cref="Verify"/> con <c>hash</c> null verifica contra un hash señuelo y devuelve <c>false</c>: así un radicado
/// inexistente tarda lo mismo que uno con el PIN errado (anti-enumeración, plan F9).
/// </summary>
public interface ISupportPinHasher
{
    string Hash(string pin);

    bool Verify(string? hash, string pin);
}
