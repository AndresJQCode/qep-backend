using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

public enum SupportTicketKind
{
    External,
    Internal,
    All,
}

/// <summary>Lo que el miembro puede hacer en Soporte en este request: sus permisos (claims) y su membresía activa, si la tiene.</summary>
public sealed record SupportAccess(SupportGrants Grants, Guid? MemberId);

/// <summary>Qué tickets lista el miembro: el tipo y, para quien sólo usa tareas, su membresía como participante (el filtro va en la
/// consulta, nunca después de traer filas).</summary>
public sealed record SupportListScope(SupportTicketKind Kind, Guid? ParticipantMemberId);

/// <summary>
/// La puerta única de todo ticket que toca un miembro (spec 2026-11-22 §5.6). Cada handler de ticket pasa por acá en vez de repetir
/// <c>SupportAuthorization.Ensure*</c> y <c>tickets.FindAsync</c>: la tabla de acceso vive en <see cref="SupportTicketAccessPolicy"/> y
/// aquí se aplica con el orden de errores que no deja un oráculo (permiso 403, ticket 404, versión 412). Una instancia por request:
/// memoiza el acceso del tenant. <c>SupportLayerTests</c> exige que ningún handler de ticket cargue un ticket sin pasar por acá.
/// </summary>
public sealed class SupportTicketScope(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    ISupportTicketRepository tickets)
{
    private readonly Dictionary<Guid, SupportAccess> _access = [];

    /// <summary>Tenant de la ruta = activo y al menos uno de los tres permisos (si no, 403 <c>authorization.denied</c>); después el
    /// módulo. <see cref="SupportAccess.MemberId"/> es la membresía activa o null.</summary>
    public async Task<SupportAccess> EnsureAccessAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_access.TryGetValue(tenantId, out var cached))
        {
            return cached;
        }

        var grants = new SupportGrants(
            executionContext.HasPermission(SupportPermissions.Read),
            executionContext.HasPermission(SupportPermissions.Manage),
            executionContext.HasPermission(SupportPermissions.TaskUse));
        if (executionContext.TenantId.Value != tenantId || !(grants.Read || grants.Manage || grants.TaskUse))
        {
            throw SupportAuthorization.Denied();
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Support, cancellationToken);
        var memberId = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken);
        return _access[tenantId] = new SupportAccess(grants, memberId);
    }

    /// <summary>Leer un ticket: nivel <see cref="SupportTicketAccessLevel.None"/> o inexistente son el mismo 404.</summary>
    public async Task<SupportTicket> ViewAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken)
    {
        var access = await EnsureAccessAsync(tenantId, cancellationToken);
        var ticket = await tickets.FindAsync(tenantId, new SupportTicketId(ticketId), cancellationToken);
        if (ticket is null || SupportTicketAccessPolicy.For(access.Grants, ticket, access.MemberId) is SupportTicketAccessLevel.None)
        {
            throw SupportNotFound.Ticket(ticketId);
        }

        return ticket;
    }

    /// <summary>
    /// Actuar sobre un ticket. Sin membresía activa, 403 (la traza nombra membresías; no depende del id). Después, <c>None</c> es 404
    /// (también para un id inexistente), <c>View</c> es 403 y sólo con <c>Act</c> se compara la versión: una versión vieja sobre algo
    /// ajeno es 404, nunca 412, para que la versión no sea un oráculo.
    /// </summary>
    public async Task<(SupportTicket Ticket, Guid Actor)> ActAsync(
        Guid tenantId, Guid ticketId, long? expectedVersion, CancellationToken cancellationToken)
    {
        var access = await EnsureAccessAsync(tenantId, cancellationToken);
        var actor = access.MemberId ?? throw SupportAuthorization.Denied();
        var ticket = await tickets.FindAsync(tenantId, new SupportTicketId(ticketId), cancellationToken)
            ?? throw SupportNotFound.Ticket(ticketId);
        switch (SupportTicketAccessPolicy.For(access.Grants, ticket, actor))
        {
            case SupportTicketAccessLevel.None:
                throw SupportNotFound.Ticket(ticketId);
            case SupportTicketAccessLevel.View:
                throw SupportAuthorization.Denied();
        }

        if (expectedVersion is { } version)
        {
            EnsureVersion(ticket, version);
        }

        return (ticket, actor);
    }

    /// <summary>Las acciones de externos: exige <c>manage</c> (403) y membresía activa (403); 404 si no existe. La versión la compara
    /// quien llama, después de sus propias reglas.</summary>
    public async Task<(SupportTicket Ticket, Guid Actor)> ManageAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken)
    {
        var access = await EnsureAccessAsync(tenantId, cancellationToken);
        if (!access.Grants.Manage)
        {
            throw SupportAuthorization.Denied();
        }

        var actor = access.MemberId ?? throw SupportAuthorization.Denied();
        var ticket = await tickets.FindAsync(tenantId, new SupportTicketId(ticketId), cancellationToken)
            ?? throw SupportNotFound.Ticket(ticketId);
        return (ticket, actor);
    }

    /// <summary>Para quien traduce «no existe» a su propio 422: exige <c>manage</c> (403) y devuelve null si el ticket no existe o es
    /// interno.</summary>
    public async Task<SupportTicket?> FindManagedAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken)
    {
        var access = await EnsureAccessAsync(tenantId, cancellationToken);
        if (!access.Grants.Manage)
        {
            throw SupportAuthorization.Denied();
        }

        var ticket = await tickets.FindAsync(tenantId, new SupportTicketId(ticketId), cancellationToken);
        return ticket is { IsInternal: false } ? ticket : null;
    }

    /// <summary>Un interno no tiene cliente, conversación ni respuestas: 422 <c>support.ticket.internal_no_reply</c>.</summary>
    public static void EnsureExternal(SupportTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (ticket.IsInternal)
        {
            throw new SupportDomainException(SupportErrorCodes.InternalNoReply, "An internal task has no customer nor conversation.");
        }
    }

    public static void EnsureVersion(SupportTicket ticket, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (ticket.Version != expectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The ticket changed after it was loaded.");
        }
    }

    /// <summary>
    /// Qué lista el miembro. Con <c>read</c> o <c>manage</c>, lo pedido (por defecto externos) y sin participante. Sólo con
    /// <c>task.use</c>: pedir externos o todos es 403 y lo demás son sus tareas, con su membresía como participante (sin ella, 403).
    /// </summary>
    public async Task<SupportListScope> ListScopeAsync(
        Guid tenantId, SupportTicketKind? requested, CancellationToken cancellationToken)
    {
        var access = await EnsureAccessAsync(tenantId, cancellationToken);
        if (access.Grants.Read || access.Grants.Manage)
        {
            return new SupportListScope(requested ?? SupportTicketKind.External, ParticipantMemberId: null);
        }

        if (requested is SupportTicketKind.External or SupportTicketKind.All)
        {
            throw SupportAuthorization.Denied();
        }

        return new SupportListScope(SupportTicketKind.Internal, access.MemberId ?? throw SupportAuthorization.Denied());
    }
}
