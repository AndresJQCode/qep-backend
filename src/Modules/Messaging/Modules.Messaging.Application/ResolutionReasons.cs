using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

// Spec 2026-10-15: el motivo de resolución. La elección (D-R4) la usa el resolver; el catálogo (§3.4) lo agrega la Task 6.

/// <summary>Los textos de <c>errors</c>, tuteando.</summary>
public static class ResolutionReasonMessages
{
    public const string Name = "Escribe un motivo de 1 a 60 caracteres.";

    public const string NameTooLong = "El motivo puede tener hasta 60 caracteres.";

    public const string Both = "Elige un motivo de la lista o escribe uno nuevo, no los dos.";
}

internal static class ResolutionReasonSupport
{
    public static ResourceNotFoundException NotFound(Guid reasonId) =>
        new(MessagingErrorCodes.ResolutionReasonNotFound, $"Resolution reason '{reasonId}' was not found.");
}

/// <summary>El motivo que llevará la resolución. <see cref="IsNew"/>: todavía no existe y el resolver lo crea con este id.</summary>
internal sealed record ChosenReason(Guid Id, string Name, bool IsNew);

internal static class ResolutionReasonChoice
{
    /// <summary>D-R4. <c>reasonId</c> tiene que estar vigente en el tenant (404 si no). <c>reasonName</c> en blanco es «sin motivo»; si
    /// coincide con uno vigente (lower de Postgres, recortado) lo reutiliza; si no, es uno nuevo. No cuenta el tope: eso lo hace el
    /// handler sólo si de verdad va a crear, porque reutilizar nunca choca con el tope (D-R9).</summary>
    public static async Task<ChosenReason?> ChooseAsync(
        IResolutionReasonRepository reasons, Guid tenantId, Guid? reasonId, string? reasonName, CancellationToken cancellationToken)
    {
        if (reasonId is { } id)
        {
            var existing = await reasons.FindActiveAsync(tenantId, id, cancellationToken) ?? throw ResolutionReasonSupport.NotFound(id);
            return new ChosenReason(existing.Id, existing.Name, IsNew: false);
        }

        var trimmed = ResolutionReason.TrimName(reasonName);
        if (trimmed.Length == 0)
        {
            return null;
        }

        var byName = await reasons.FindActiveByNameAsync(tenantId, trimmed, cancellationToken);
        return byName is null ? new ChosenReason(Guid.CreateVersion7(), trimmed, IsNew: true) : new ChosenReason(byName.Id, byName.Name, IsNew: false);
    }
}
