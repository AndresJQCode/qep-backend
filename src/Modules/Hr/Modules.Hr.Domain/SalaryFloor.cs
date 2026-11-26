namespace Modules.Hr.Domain;

/// <summary>Los pisos legales vigentes a <paramref name="Date"/>: el mínimo mensual y el piso del salario integral.</summary>
public sealed record SalaryFloor(DateOnly Date, decimal Smmlv, decimal IntegralFloor);
