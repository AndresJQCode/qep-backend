using System.Security.Cryptography;
using System.Text;

namespace Modules.Geography.Infrastructure.Seed;

/// <summary>
/// La huella del contenido que <see cref="RegionSeeder"/> aplicaría para un país: SHA-256 de los bytes crudos de sus dos
/// archivos, en hexadecimal minúscula. Se calcula leyendo el recurso embebido en bloques, sin parsear el JSON ni crear
/// objetos por fila, y es lo único que el arranque necesita para saber si el país ya está al día.
/// </summary>
internal static class RegionSeedHash
{
    /// <summary>
    /// Sube este número cuando cambie **la lógica** del importador (cómo se transforma o se guarda un dato) y no sólo
    /// los archivos: sin esto, una corrección del importador no se aplicaría a una base cuyo hash ya coincide.
    /// </summary>
    internal const int SeederVersion = 1;

    public static string Compute(string country, Stream regions, Stream cities)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"regions-seed-v{SeederVersion}\n{country}\n"));
        Append(hash, regions);
        // El NUL separa los dos archivos: un JSON nunca lo contiene, así que mover bytes de uno al otro cambia la huella.
        hash.AppendData([0]);
        Append(hash, cities);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, Stream stream)
    {
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }
    }
}
