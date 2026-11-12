namespace Modules.Store.Domain;

public sealed record StoreContact(
    string? WhatsApp,
    string? Address,
    string? Schedule,
    string? InstagramUrl,
    string? FacebookUrl,
    string? TiktokUrl);
