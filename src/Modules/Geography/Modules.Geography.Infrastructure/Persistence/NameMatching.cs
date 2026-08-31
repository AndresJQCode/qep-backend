using System.Globalization;
using System.Text;

namespace Modules.Geography.Infrastructure.Persistence;

/// <summary>
/// Normaliza un nombre de departamento/ciudad para compararlo sin importar mayusculas, minusculas
/// ni tildes ("Bogota" == "BOGOTÁ" == "bogotá"). El Excel de importacion de clientes
/// (<c>ImportCustomers.cs</c>) lo escribe el usuario a mano, y DIVIPOLA lo trae siempre con tilde
/// — sin esto, una fila con "Bogota" (sin tilde) fallaba con <c>city_not_found</c> aunque la ciudad
/// existiera.
///
/// El emparejamiento se hace en memoria (<see cref="ILike"/>/SQL ya cubria mayusculas pero no
/// tildes, y Postgres no tiene una funcion de "sin tildes" nativa sin la extension `unaccent`) sobre
/// listas ya acotadas por la consulta que las trae — 33 departamentos, o las ciudades de un solo
/// departamento — asi que cargarlas entera y comparar en C# no pesa.
/// </summary>
internal static class NameMatching
{
    public static string Normalize(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }
}
