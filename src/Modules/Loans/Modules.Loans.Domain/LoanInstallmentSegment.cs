namespace Modules.Loans.Domain;

/// <summary>Un tramo de una cuota con el mismo capital; se copia del cálculo y no se recalcula.</summary>
public sealed class LoanInstallmentSegment
{
    private LoanInstallmentSegment()
    {
    }

    public Guid Id { get; private set; }

    public Guid InstallmentId { get; private set; }

    /// <summary>Posición dentro de la cuota, desde 1.</summary>
    public int Ordinal { get; private set; }

    public DateOnly StartOn { get; private set; }

    public DateOnly EndOn { get; private set; }

    public decimal Principal { get; private set; }

    public int Days { get; private set; }

    public decimal Interest { get; private set; }

    internal static LoanInstallmentSegment From(Guid installmentId, int ordinal, InterestSegment segment) => new()
    {
        Id = Guid.CreateVersion7(),
        InstallmentId = installmentId,
        Ordinal = ordinal,
        StartOn = segment.Start,
        EndOn = segment.End,
        Principal = segment.Principal,
        Days = segment.Days,
        Interest = segment.Interest,
    };
}
