using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

internal enum ContactBlockDirection { Block, Unblock }

internal enum ContactBlockVerdict { Applied, WindowClosed, UnblockRejected, ConnectionRejected, Failed }

/// <summary>Spec 2026-10-26 §3.3: el veredicto y, para el log, el código, subcódigo y <c>fbtrace_id</c> de Meta (nunca el número).</summary>
internal sealed record ContactBlockAnswer(ContactBlockVerdict Verdict, int? Code, int? Subcode, string? FbTraceId);

/// <summary>
/// Spec 2026-10-26 §3.3: la tabla de respuestas de <c>block_users</c>. Sólo se reconoce lo que Meta documenta (131047 y las credenciales);
/// no hay constantes para «ya estaba bloqueado» / «no estaba bloqueado» porque no se publican: cualquier otra cosa es <c>Failed</c> y el
/// log trae el código para decidir después.
/// </summary>
internal static class ContactBlockResponse
{
    public static ContactBlockAnswer Classify(MessagingGraphResult<BlockUsersResult> result, ContactBlockDirection direction)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Failure is { } failure)
        {
            return Answer(failure.Code, failure.Subcode, failure.FbTraceId, direction);
        }

        if (result.Value is not { } value)
        {
            return new ContactBlockAnswer(ContactBlockVerdict.Failed, null, null, null);
        }

        return value.Applied
            ? new ContactBlockAnswer(ContactBlockVerdict.Applied, null, null, value.FbTraceId)
            : Answer(value.FailedCode, value.FailedSubcode, value.FbTraceId, direction);
    }

    private static ContactBlockAnswer Answer(int? code, int? subcode, string? fbTraceId, ContactBlockDirection direction)
    {
        var verdict = code switch
        {
            MetaErrorCodes.CustomerWindowClosed => direction == ContactBlockDirection.Block ? ContactBlockVerdict.WindowClosed : ContactBlockVerdict.UnblockRejected,
            { } c when MetaErrorCodes.IsRejectedCredentials(c) => ContactBlockVerdict.ConnectionRejected,
            _ => ContactBlockVerdict.Failed,
        };
        return new ContactBlockAnswer(verdict, code, subcode, fbTraceId);
    }
}
