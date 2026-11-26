namespace Modules.Support.Application;

/// <summary>
/// Another filing with the same <c>clientRequestId</c> in the tenant was committed first (spec 2026-11-15 §9.2, plan L27). The handler
/// catches it and answers with the winner, like a retry; it never reaches HTTP. A backstop: the public filing already looks the key up
/// again under the lock of the number counter, so this only fires if that check is ever bypassed. <c>SupportUnitOfWork</c> raises it
/// from <c>IX_tickets_tenant_client_request</c>, by index name.
/// </summary>
public sealed class SupportClientRequestTakenException(Exception innerException)
    : Exception("A ticket with this client request id was already filed in the tenant.", innerException);
