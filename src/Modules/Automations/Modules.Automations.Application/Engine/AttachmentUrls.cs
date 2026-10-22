using System.Text.Json.Nodes;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.Application.Engine;

/// <summary>
/// Spec 2026-10-21 D-M8 y ruling P2-R43: el <c>{saveTo.url}</c> de un adjunto. El bloque Adjunto guarda <c>stored: false</c> y sin url
/// cuando el archivo todavía no está en R2 (<c>MediaCopyWorker</c> corre cada 5 s, más lo que tarde Meta en entregarlo), que es casi
/// siempre: el entrante se procesa enseguida. Por eso la url se firma tarde, cuando un efecto la va a usar y el archivo ya llegó. La
/// llave que se firma es siempre la que Mensajería devuelve para ese <c>messageId</c> en ese momento, nunca una leída de las variables.
/// </summary>
public static class AttachmentUrls
{
    /// <summary>D-M8: <c>{saveTo.url}</c> vale 15 minutos.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// El <c>messageId</c> del adjunto guardado en la variable raíz <paramref name="name"/> si todavía no está en R2; <c>null</c> si la
    /// variable no es un adjunto pendiente. <see cref="Guid.Empty"/> no cuenta: es el adjunto de mentira del simulador (§8), que no tiene
    /// mensaje ni archivo.
    /// </summary>
    public static Guid? PendingMessageId(RunVariables variables, string name)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (variables.Get(name) is not JsonObject saved
            || saved["stored"] is not JsonValue flag
            || !flag.TryGetValue<bool>(out var stored)
            || stored)
        {
            return null;
        }

        return Guid.TryParse(variables.TextOf($"{name}.messageId"), out var messageId) && messageId != Guid.Empty ? messageId : null;
    }

    /// <summary>El adjunto de <paramref name="name"/> ya está en R2: <c>stored: true</c> y la url recién firmada, sobre la misma variable.</summary>
    public static void MarkStored(RunVariables variables, string name, Uri url)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(url);
        if (variables.Get(name) is not JsonObject saved)
        {
            return;
        }

        var updated = (JsonObject)saved.DeepClone();
        updated["stored"] = true;
        updated["url"] = url.AbsoluteUri;
        variables.Set(name, updated);
    }

    /// <summary>
    /// P2-R43: vuelve a mirar los adjuntos pendientes de <paramref name="names"/> y firma los que ya llegaron. Sin espera: el que sigue sin
    /// copiarse queda como estaba. Devuelve una copia si cambió algo, o las mismas variables.
    /// </summary>
    public static async Task<RunVariables> RefreshAsync(
        IAutomationMedia media, Guid tenantId, RunVariables variables, IEnumerable<string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(names);
        RunVariables? refreshed = null;
        foreach (var name in names)
        {
            if (PendingMessageId(variables, name) is not { } messageId
                || await media.TryGetStoredAsync(tenantId, messageId, cancellationToken) is not { } stored)
            {
                continue;
            }

            refreshed ??= variables.Clone();
            MarkStored(refreshed, name, await media.CreateDownloadUrlAsync(stored.Key, Lifetime, cancellationToken));
        }

        return refreshed ?? variables;
    }
}
