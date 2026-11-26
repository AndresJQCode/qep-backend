namespace Modules.Hr.Domain;

/// <summary>La fila vigente de un valor legal a una fecha, con la fecha desde la que rige (la jornada cambia el 15 de julio).</summary>
public sealed record LegalParameterEntry(LegalParameterKey Key, DateOnly EffectiveFrom, decimal Value);
