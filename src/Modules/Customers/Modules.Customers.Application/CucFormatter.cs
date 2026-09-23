using System.Globalization;

namespace Modules.Customers.Application;

/// <summary>
/// Arma el CUC final a partir de sus tres partes: el prefijo de la clasificacion del cliente, el
/// codigo DIVIPOLA del departamento de su ciudad y el consecutivo que emite
/// <see cref="ICucGenerator"/>. Ejemplo: prefijo <c>CLI</c> + departamento <c>08</c> (Antioquia) +
/// consecutivo <c>1</c> => <c>CLI08000001</c>.
///
/// Pura y sin dependencias a proposito: es la pieza "encapsulada y reutilizable" que pide la
/// generacion de CUC, separada del mecanismo de concurrencia del consecutivo (que resuelve
/// <c>CucGenerator</c> con un <c>UPDATE ... RETURNING</c> atomico) y de donde salen el prefijo y
/// el codigo de departamento (que resuelve el handler contra <c>ClientClassification</c> y
/// <c>ICustomerGeographyLookup</c>). Se prueba con casos unitarios simples, sin base de datos.
/// </summary>
public static class CucFormatter
{
    /// <summary>El consecutivo siempre ocupa seis digitos, con ceros a la izquierda.</summary>
    public const int SequenceDigits = 6;

    /// <summary>
    /// Los dos digitos que ocupan el lugar del departamento cuando el cliente **no es de
    /// Colombia** y por lo tanto no tiene uno: DIVIPOLA es el estandar colombiano.
    ///
    /// <c>00</c> y no omitirlos. El CUC tiene que conservar su largo porque sus ultimos ocho
    /// caracteres son la parte estable
    /// (<see cref="Modules.Customers.Domain.Customer.StableSuffixOf"/>): con ellos
    /// <c>Customer.Update</c> reescribe el prefijo al cambiar la clasificacion y la importacion
    /// masiva matchea un cliente existente. Un CUC mas corto haria que esos ocho caracteres se
    /// comieran parte del prefijo, y las dos cosas se romperian en silencio.
    ///
    /// No choca con ningun departamento real: los codigos DIVIPOLA de departamento van de
    /// <c>05</c> a <c>99</c>, y <c>00</c> no es ninguno.
    /// </summary>
    public const string ForeignDepartmentCode = "00";

    /// <summary>
    /// El prefijo de una clasificacion mide hasta 20 (<c>ClientClassification.PrefixMaxLength</c>,
    /// en <c>Modules.Customers.Domain</c>) y el codigo de departamento DIVIPOLA siempre son 2
    /// digitos (invariante del modulo Geography): 20 + 2 + <see cref="SequenceDigits"/> (6) = 28,
    /// que entra sin ajustar <c>Customer.CucMaxLength</c> (32).
    /// </summary>
    public static string Build(
        string classificationPrefix, string departmentDivipolaCode, long sequence)
    {
        ArgumentException.ThrowIfNullOrEmpty(classificationPrefix);
        ArgumentException.ThrowIfNullOrEmpty(departmentDivipolaCode);

        // "D6" con cultura invariante, no ToString() a secas: el separador de miles y los digitos
        // de una cultura arabe o hindi convertirian el mismo consecutivo en codigos distintos
        // segun donde corra el servidor, y el CUC es un identificador. Pasados los seis digitos el
        // formato simplemente crece — recortar seria emitir un codigo repetido.
        var paddedSequence = sequence.ToString(
            $"D{SequenceDigits}", CultureInfo.InvariantCulture);

        return $"{classificationPrefix}{departmentDivipolaCode}{paddedSequence}";
    }
}
