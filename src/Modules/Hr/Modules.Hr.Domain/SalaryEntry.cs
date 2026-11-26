namespace Modules.Hr.Domain;

public sealed record SalaryEntry(DateOnly EffectiveDate, decimal Amount, bool Teleworker, bool AcknowledgeBelowMinimum);
