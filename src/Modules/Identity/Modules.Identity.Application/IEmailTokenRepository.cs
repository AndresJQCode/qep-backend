using Modules.Identity.Domain;

namespace Modules.Identity.Application;

public interface IEmailTokenRepository
{
    void Add(EmailToken token);

    /// <summary>
    /// Marca como usados los tokens vigentes del usuario. Con <paramref name="purpose"/> nulo
    /// alcanza todos los propósitos. Carga y marca: entra en la unidad de trabajo del llamador.
    /// </summary>
    Task InvalidateUnusedAsync(
        Guid userId,
        EmailTokenPurpose? purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Consume el token de forma atómica y fuera de la unidad de trabajo (D6): una sola sentencia
    /// que se confirma sola, así dos requests concurrentes no pueden gastar el mismo token.
    /// Devuelve el id del usuario, o null si el token no existe, venció, ya se usó o es de otro propósito.
    /// </summary>
    Task<Guid?> TryConsumeAsync(
        string tokenHash,
        EmailTokenPurpose purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}