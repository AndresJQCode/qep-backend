namespace Modules.Quotations.Domain;

/// <summary>De dónde salió una tasa congelada: del cliente o de los selectores de facturación propia
/// (cotización), o del pedido, que tiene una sola fuente.</summary>
public enum WithholdingSource
{
    Customer,
    Billing,
    Document,
}

/// <summary>
/// Fila hija de una cotización o un pedido: una tasa de retención congelada y lo que dio en el último
/// recálculo. Se guardan también las que no aplican (con <see cref="Applied"/> en false), porque la tasa
/// puede empezar a aplicar cuando cambian las líneas, la empresa o el consumidor final.
/// </summary>
public sealed class DocumentWithholding
{
    // EF Core materializa por acá.
    private DocumentWithholding()
    {
        Rate = null!;
    }

    internal DocumentWithholding(WithholdingSource source, short position, WithholdingRate rate)
    {
        Source = source;
        Position = position;
        Rate = rate;
    }

    public WithholdingSource Source { get; private set; }

    /// <summary>El orden de llegada dentro de su fuente.</summary>
    public short Position { get; private set; }

    public WithholdingRate Rate { get; private set; }

    public decimal BaseAmount { get; private set; }

    /// <summary>0 en las que no aplican.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Distingue «no aplica» de una tasa al 0 % que sí aplicó (plan P3).</summary>
    public bool Applied { get; private set; }

    /// <summary>Guarda lo que dio el cálculo para esta tasa; null si no aplicó.</summary>
    internal void Settle(AppliedWithholding? applied)
    {
        if (applied is { } result && result.Rate != Rate)
        {
            throw new InvalidOperationException("The applied withholding belongs to another tax rate.");
        }

        Applied = applied is not null;
        BaseAmount = applied?.Base ?? 0m;
        Amount = applied?.Amount ?? 0m;
    }
}
