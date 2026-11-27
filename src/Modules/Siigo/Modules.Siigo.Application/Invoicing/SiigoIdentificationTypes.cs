namespace Modules.Siigo.Application.Invoicing;

/// <summary>
/// Spec 2026-11-18 §6.2: el tipo de documento de QEP como <c>id_type</c> de Siigo. Tabla fija: CC <c>13</c>, NIT <c>31</c>, CE
/// <c>22</c>, pasaporte <c>41</c>; y, desde el spec 2026-11-26, Tax ID <c>50</c> y NIF y NIE <c>42</c>. (TI, PEP y RC existen en Siigo
/// pero QEP no los tiene.) Contabilidad confirmó <c>50</c> y <c>42</c> el 2026-11-27; todavía no se probaron contra un Siigo real.
/// </summary>
public static class SiigoIdentificationTypes
{
    /// <summary>El documento del consumidor final que define la DIAN (V-3: se confirma con la primera factura real).</summary>
    public const string FinalConsumerIdentification = "222222222222";

    public static string? CodeFor(SiigoIdentificationKind kind) => kind switch
    {
        SiigoIdentificationKind.Cc => "13",
        SiigoIdentificationKind.Nit => "31",
        SiigoIdentificationKind.Ce => "22",
        SiigoIdentificationKind.Pasaporte => "41",
        SiigoIdentificationKind.TaxId => "50",
        SiigoIdentificationKind.Nif or SiigoIdentificationKind.Nie => "42",
        _ => null,
    };
}
