namespace Modules.Pos.Domain;

/// <summary>
/// Copia de las dos constantes de Quotations.FinalConsumer (spec, decisión 11): moverlas obligaría
/// a tocar siete llamadores de Quotations por nada, y el frontend ya las duplica igual. Una prueba
/// de PosTypesTests las compara contra la fuente para que no se separen.
/// </summary>
public static class PosFinalConsumer
{
    public const string Name = "Consumidor final";
    public const string IdentificationNumber = "222222222222";
}
