namespace Modules.Payroll.Domain.Engine;

/// <summary>Un tramo del fondo de solidaridad pensional: límites en múltiplos de SMMLV y la tasa que se aplica adentro.</summary>
public sealed record SolidarityBracket(decimal LowerSmmlv, decimal? UpperSmmlv, decimal Rate, DateOnly EffectiveFrom);
