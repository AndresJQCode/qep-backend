using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>
/// El nombre con el que la importación crea una etiqueta nueva (spec 2026-11-17): tipo oración, primera letra en
/// mayúscula y el resto en minúsculas, recortado. La pantalla de etiquetas no pasa por acá: guarda lo que escribe el usuario.
/// </summary>
public sealed class TagNameFormatTests
{
    [Theory]
    [InlineData("evento MACEL", "Evento macel")]
    [InlineData("  vip ", "Vip")]
    [InlineData("ñAPA", "Ñapa")]
    [InlineData("", "")]
    public void SentenceCaseLowersTheRestAndUppersTheFirst(string input, string expected)
    {
        Assert.Equal(expected, TagNameFormat.SentenceCase(input));
    }
}
