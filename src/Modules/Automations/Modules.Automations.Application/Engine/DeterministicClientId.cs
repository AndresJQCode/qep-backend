using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Modules.Automations.Application.Engine;

/// <summary>
/// Ruling P2-R5 (spec 2026-10-21 §5.3): los ids que hacen que reintentar una fila del inbox no duplique nada. Una unidad que se deshizo
/// después de que Meta aceptó un mensaje vuelve a correr con la misma corrida (<see cref="RunIdFor"/>) desde el mismo
/// <c>StepNumber</c> guardado, así que cada envío deriva el mismo <c>clientId</c> y <c>IBotOutbound</c> lo reconoce como ya enviado.
/// No usa los <c>attempts</c> de la fila (como decía el plan): cambian en cada reintento y harían justo lo contrario.
/// </summary>
public static class DeterministicClientId
{
    /// <param name="baseStepNumber">El <c>StepNumber</c> que la corrida tenía guardado al empezar la unidad (0 si la corrida es nueva). El
    /// motor lo sube con cada bloque en que entra, así que dos unidades confirmadas nunca comparten base.</param>
    /// <param name="effectIndex">El orden del envío dentro de la unidad, contando también los que siguen a un HTTP o a un horario.</param>
    public static Guid For(Guid runId, int baseStepNumber, int effectIndex) =>
        Hash(string.Create(CultureInfo.InvariantCulture, $"{runId:D}:{baseStepNumber}:{effectIndex}"));

    /// <summary>La corrida que arranca el entrante <paramref name="messageId"/>: un arranque deshecho y reintentado reusa el mismo id.</summary>
    public static Guid RunIdFor(Guid messageId) => Hash(string.Create(CultureInfo.InvariantCulture, $"run:{messageId:D}"));

    /// <summary>Los primeros 16 bytes del SHA-256: estable entre procesos y versiones de .NET, a diferencia de <c>GetHashCode</c>.</summary>
    private static Guid Hash(string seed)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(seed), digest);
        return new Guid(digest[..16]);
    }
}
