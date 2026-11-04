namespace Modules.Store.Domain;

/// <summary>Qué aceptó el visitante: cuándo, desde qué IP, y la política que regía y desde cuándo (spec §4.2).</summary>
public sealed record StoreConsent(DateTimeOffset At, string Ip, string PolicyUrl, DateTimeOffset PolicySetAt);
