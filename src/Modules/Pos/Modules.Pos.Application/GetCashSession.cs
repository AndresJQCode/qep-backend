using BuildingBlocks.Application;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>El resumen de cierre (o el vivo si está abierta). La propia siempre; una ajena con pos.register.read.</summary>
public sealed record GetCashSessionQuery(Guid TenantId, Guid SessionId) : IQuery<PosSessionSummaryResponse>;

public sealed class GetCashSessionHandler(
    ICashSessionRepository sessions,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<GetCashSessionQuery, PosSessionSummaryResponse>
{
    public async Task<PosSessionSummaryResponse> HandleAsync(GetCashSessionQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleRead);
        var session = await sessions.FindAsync(query.TenantId, new CashSessionId(query.SessionId), cancellationToken)
            ?? throw PosNotFound.Session(query.SessionId);
        await PosScope.EnsureCanSeeAsync(session.CashierId, membershipDirectory, executionContext, query.TenantId, cancellationToken);
        return PosSessionMapping.ToSummary(session, await tenantClock.GetAsync(query.TenantId, cancellationToken));
    }
}
