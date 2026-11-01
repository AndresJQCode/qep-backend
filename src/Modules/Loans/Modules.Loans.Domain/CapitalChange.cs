namespace Modules.Loans.Domain;

/// <summary>El capital pendiente pasa a <paramref name="BalanceAfter"/> desde <paramref name="EffectiveOn"/> inclusive.</summary>
public sealed record CapitalChange(DateOnly EffectiveOn, decimal BalanceAfter);
