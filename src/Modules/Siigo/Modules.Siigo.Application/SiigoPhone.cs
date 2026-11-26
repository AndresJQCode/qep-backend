namespace Modules.Siigo.Application;

/// <summary>
/// El teléfono como lo pide Siigo al crear un cliente (spec 2026-11-25): <paramref name="Indicative"/> es el código del país sin «+»
/// (<c>null</c> si no se conoce) y <paramref name="Number"/>, el número. Lleva datos del cliente: nunca se imprime ni se loguea (el
/// <c>ToString</c> no escribe nada).
/// </summary>
public sealed record SiigoPhone(string? Indicative, string Number)
{
    public override string ToString() => "SiigoPhone { }";
}
