using BuildingBlocks.Domain.Taxes;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>Dónde va una retención en la factura de Siigo.</summary>
public enum SiigoWithholdingTarget
{
    /// <summary>Dentro de los <c>taxes</c> de cada ítem, después del IVA (ReteFuente).</summary>
    ItemTax,

    /// <summary>En <c>retentions</c> de la factura (ReteICA).</summary>
    Invoice,

    /// <summary>ReteIVA, autorretención u otro: no se manda y es un faltante hasta que se decida (V-7).</summary>
    Unsupported,
}

/// <summary>Spec 2026-11-18 §6.3. Usa las constantes de tipo de <c>TaxCountries</c>, las mismas de <c>DocumentWithholding.Type</c>.</summary>
public static class SiigoWithholdingPlacement
{
    public static SiigoWithholdingTarget For(string qepType)
    {
        if (string.Equals(qepType, TaxCountries.RetefuenteType, StringComparison.OrdinalIgnoreCase))
        {
            return SiigoWithholdingTarget.ItemTax;
        }

        return string.Equals(qepType, TaxCountries.ReteIcaType, StringComparison.OrdinalIgnoreCase)
            ? SiigoWithholdingTarget.Invoice
            : SiigoWithholdingTarget.Unsupported;
    }
}
