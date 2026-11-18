namespace Modules.Siigo.Domain;

/// <summary>
/// Spec 2026-11-18 §5.2: la tasa de retención del catálogo de impuestos de QEP (<c>TaxRateId</c>) y el impuesto de Siigo que la
/// representa. Siigo no recibe porcentaje ni valor, sólo el id de un impuesto de <c>GET /v1/taxes</c>, y puede haber varios activos con
/// el mismo tipo y porcentaje: el vínculo lo elige una persona, no se adivina. <see cref="SiigoTaxId"/> nulo es «sin vincular». Único por
/// <c>(connection_id, tax_rate_id)</c>.
/// </summary>
public sealed class SiigoWithholdingLink
{
    private SiigoWithholdingLink()
    {
    }

    public SiigoWithholdingLink(Guid taxRateId, string? siigoTaxId)
    {
        TaxRateId = taxRateId;
        SiigoTaxId = SiigoTaxLink.Normalize(siigoTaxId);
    }

    public Guid ConnectionId { get; internal set; }

    public Guid TenantId { get; internal set; }

    public Guid TaxRateId { get; private set; }

    public string? SiigoTaxId { get; private set; }

    internal void Relink(string? siigoTaxId) => SiigoTaxId = SiigoTaxLink.Normalize(siigoTaxId);
}
