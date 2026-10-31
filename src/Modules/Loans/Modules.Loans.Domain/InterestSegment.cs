namespace Modules.Loans.Domain;

/// <summary>Un tramo del período con el mismo capital; ambos extremos inclusive.</summary>
public sealed record InterestSegment(DateOnly Start, DateOnly End, decimal Principal, int Days, decimal Interest);
