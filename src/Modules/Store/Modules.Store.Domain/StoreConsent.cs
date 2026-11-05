namespace Modules.Store.Domain;

/// <summary>
/// Qué aceptó el visitante: cuándo, desde qué IP, y la política que regía y desde cuándo (spec §4.2). La política es opcional
/// para publicar (decisión del owner, 2026-11-05): sin ella, <see cref="PolicyUrl"/> y <see cref="PolicySetAt"/> quedan null,
/// pero la aceptación igual se exige y se guarda.
/// </summary>
public sealed record StoreConsent(DateTimeOffset At, string Ip, string? PolicyUrl, DateTimeOffset? PolicySetAt);
