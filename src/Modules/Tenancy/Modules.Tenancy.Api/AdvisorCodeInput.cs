namespace Modules.Tenancy.Api;

/// <summary>
/// Traduce el <c>advisorCode</c> del JSON al <c>int?</c> que esperan los comandos.
/// </summary>
/// <remarks>
/// El request lo recibe como <see cref="decimal"/> y no como <see cref="int"/> porque un
/// <c>12.5</c> o un <c>99999999999</c> en un <c>int?</c> hacen fallar el binding con
/// <c>BadHttpRequestException</c>, y <c>ApiExceptionHandler</c> reduce toda excepción que no
/// reconoce a un 500 (mismo problema que documenta <c>GeographyEndpoints</c>). Acá cualquier valor
/// que no sea un entero representable llega al validador como <see cref="NotAPositiveInteger"/>,
/// que lo rechaza con <c>errors.AdvisorCode</c>: el único 422 que el formulario sabe marcar.
/// </remarks>
internal static class AdvisorCodeInput
{
    private const int NotAPositiveInteger = -1;

    public static int? ToCommandValue(decimal? value) =>
        value switch
        {
            null => null,
            { } number when number == decimal.Truncate(number)
                && number >= int.MinValue
                && number <= int.MaxValue => (int)number,
            _ => NotAPositiveInteger,
        };
}
