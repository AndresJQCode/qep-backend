namespace Modules.Siigo.Domain;

/// <summary>
/// Spec 2026-11-18 §5.2: el IVA de QEP por porcentaje entero (<c>0</c>, <c>5</c>, <c>19</c>…) y el impuesto de Siigo que lo representa.
/// <see cref="SiigoTaxId"/> nulo es «sin vincular»: el 0 % puede quedar así (una línea al 0 % va sin <c>taxes</c>). Único por
/// <c>(connection_id, tax_percentage)</c>. Como entrada de <c>SiigoSettings.ReplaceTaxLinks</c> sólo lleva el porcentaje y el id; la
/// conexión y el tenant los pone la configuración.
/// </summary>
public sealed class SiigoTaxLink
{
    public const int TaxIdMaxLength = 50;

    private SiigoTaxLink()
    {
    }

    public SiigoTaxLink(int taxPercentage, string? siigoTaxId)
    {
        TaxPercentage = taxPercentage;
        SiigoTaxId = Normalize(siigoTaxId);
    }

    public Guid ConnectionId { get; internal set; }

    public Guid TenantId { get; internal set; }

    public int TaxPercentage { get; private set; }

    public string? SiigoTaxId { get; private set; }

    internal void Relink(string? siigoTaxId) => SiigoTaxId = Normalize(siigoTaxId);

    internal static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
