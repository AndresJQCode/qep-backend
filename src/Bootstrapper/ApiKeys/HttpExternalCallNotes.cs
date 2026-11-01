using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;

namespace Bootstrapper.ApiKeys;

/// <summary>Le da a los módulos, que no ven HTTP, la nota de la llamada en curso. Sin nota no anota: el registro nunca tumba un envío.</summary>
internal sealed class HttpExternalCallNotes(IHttpContextAccessor accessor) : IExternalCallNotes
{
    public void NoteMessage(Guid messageId)
    {
        if (accessor.HttpContext is { } context)
        {
            ExternalCallNote.Find(context)?.NoteMessage(messageId);
        }
    }
}
