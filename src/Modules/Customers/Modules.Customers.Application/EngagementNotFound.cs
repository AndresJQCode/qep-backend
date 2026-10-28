using BuildingBlocks.Application;

namespace Modules.Customers.Application;

// Mismo criterio que CustomerNotFound: la búsqueda siempre está acotada al tenant del llamador, así que «no
// encontrada» significa «no entre las de tu tenant». Un tenant ajeno se frena antes, en la autorización, con 403.
internal static class NoteNotFound
{
    public static ResourceNotFoundException For(Guid noteId) =>
        new("customers.note.not_found", $"Customer note '{noteId}' was not found.");
}

internal static class TagNotFound
{
    public static ResourceNotFoundException For(Guid tagId) =>
        new("customers.tag.not_found", $"Tag '{tagId}' was not found.");
}
