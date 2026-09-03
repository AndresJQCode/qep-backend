namespace Modules.Reporting.Domain;

/// <summary>
/// Cuánto se movió un precio en una fila del histórico.
///
/// **Un lado nulo cuenta como cero, no anula la resta.** `ProductPriceChange` deja
/// <c>PreviousValue</c> en null cuando el precio base estaba vacío o la escala no existía, y
/// <c>NewValue</c> en null cuando se limpió o desapareció. Devolver null ahí dejaría sin número
/// justo las dos filas que más se miran —el alta y la baja de un precio—, así que la diferencia
/// es siempre un decimal.
/// </summary>
public static class PriceChangeDifference
{
    public static decimal Between(decimal? previousValue, decimal? newValue) =>
        (newValue ?? 0m) - (previousValue ?? 0m);
}
