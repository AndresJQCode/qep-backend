namespace Modules.Siigo.Infrastructure.Batches;

/// <summary>Las tandas de facturación (<c>Siigo:Batches</c>). Sólo el intervalo del worker cuando no hay timbre: lo demás es contrato del
/// dominio (50 pedidos, 5 intentos).</summary>
public sealed class SiigoInvoiceBatchOptions
{
    public const string SectionName = "Siigo:Batches";

    /// <summary>Cada cuántos segundos mira el worker si hay pedidos por facturar, sin timbre. Mínimo 1.</summary>
    public int PollSeconds { get; set; } = 5;
}
