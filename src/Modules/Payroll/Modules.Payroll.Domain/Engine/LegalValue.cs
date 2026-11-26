namespace Modules.Payroll.Domain.Engine;

/// <summary>Un valor legal resuelto a la fecha del período: la clave de Hr, el valor y desde cuándo rige (va al rastro).</summary>
public sealed record LegalValue(string Key, decimal Value, DateOnly EffectiveFrom);
