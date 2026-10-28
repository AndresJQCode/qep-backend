using Modules.Customers.Domain;

namespace Modules.Customers.Application;

// Spec 2026-10-11 §4.2. Estos DTO viajan tal cual por HTTP (Api no los duplica): son la forma que la pantalla dibuja.

/// <summary>El autor de una nota. <c>DisplayName</c> nunca es null: si el usuario ya no existe vale «Usuario eliminado».</summary>
public sealed record NoteAuthorDto(Guid UserId, string DisplayName);

/// <summary><c>CanEdit</c> y <c>CanDelete</c> vienen calculados para que la pantalla no repita la regla (BFF): editar es
/// del autor con <c>customers.customer.manage</c>; borrar es de ese mismo autor o de quien tenga
/// <c>customers.note.moderate</c>.</summary>
public sealed record CustomerNoteDto(
    Guid Id,
    string Body,
    NoteAuthorDto Author,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool CanEdit,
    bool CanDelete,
    long Version);

/// <summary><c>Color</c> viaja por nombre en minúscula (<c>gray</c>…<c>purple</c>): la paleta la pinta el frontend.
/// <c>CustomerCount</c> viaja siempre, incluso en cero.</summary>
public sealed record TagDto(Guid Id, string Name, string Color, int CustomerCount, long Version);

/// <summary>Con <c>OptedOut = false</c> los otros dos campos son null. <c>Source</c> es <c>manual</c> o <c>meta</c>.</summary>
public sealed record MarketingConsentDto(bool OptedOut, DateTimeOffset? OptedOutAt, string? Source);

public sealed record CreateCustomerNoteRequest(string Body);

public sealed record UpdateCustomerNoteRequest(string Body, long ExpectedVersion);

public sealed record CreateTagRequest(string Name, string Color);

public sealed record UpdateTagRequest(string Name, string Color, long ExpectedVersion);

public sealed record SetCustomerTagsRequest(IReadOnlyList<Guid> TagIds);

public sealed record SetMarketingConsentRequest(bool OptedOut);

internal static class EngagementMapping
{
    public static TagDto ToDto(this TagWithCount item) => item.Tag.ToDto(item.CustomerCount);

    public static TagDto ToDto(this Tag tag, int customerCount) =>
        new(tag.Id.Value, tag.Name, tag.Color.ToWireValue(), customerCount, tag.Version);

    public static MarketingConsentDto ToConsentDto(this Customer customer) =>
        new(customer.MarketingOptOut, customer.MarketingOptOutAt, customer.MarketingOptOutSource?.ToWireValue());
}
