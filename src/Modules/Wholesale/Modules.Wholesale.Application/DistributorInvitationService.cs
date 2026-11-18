using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// La invitación vista desde el link, fuera del tenant del mayorista: la consultan sin sesión, la usan las rutas
/// <c>session*</c> antes de tocar Identity y la acepta quien tiene la sesión del correo invitado. El tenant del mayorista sale
/// de la invitación, no de un <c>IExecutionContext</c>: quien llama todavía no es miembro de nada.
/// </summary>
public interface IDistributorInvitationService
{
    /// <summary>El resumen del link (D-20). 404 <c>not_found</c> si el token no existe, rotó o el distribuidor se borró.</summary>
    Task<InvitationSummaryDto> FindAsync(string token, CancellationToken cancellationToken);

    /// <summary>La invitación todavía aceptable: 404 → 410 <c>expired</c> → 409 <c>already_accepted</c>, en ese orden.</summary>
    Task<Distributor> RequirePendingAsync(string token, CancellationToken cancellationToken);

    /// <summary>Los tenants que el usuario administra como owner, con si se pueden vincular y qué módulos prendería.</summary>
    Task<IReadOnlyList<LinkableTenantDto>> ListLinkableTenantsAsync(
        string token, Guid userId, string? userEmail, CancellationToken cancellationToken);

    /// <summary>Acepta la invitación (D-3, D-16, D-17). Ver <see cref="DistributorInvitationService"/>.</summary>
    Task<AcceptedInvitationDto> AcceptAsync(
        string token,
        Guid userId,
        string? userEmail,
        AcceptInvitationChoice choice,
        string correlationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// La aceptación son pasos idempotentes sin transacción común (D-3): tenant (con el id reservado), módulos, proveedor en el
/// distribuidor, cliente en el mayorista y, al final, el commit de <c>wholesale</c> que consume la invitación. Un fallo a mitad
/// deja la invitación pendiente y el reintento termina sobre el mismo tenant.
/// <para>Orden fijo: 1) invitación por hash (404); 2) correo de la sesión (403, antes de revelar nada más); 3) ya aceptada:
/// mismo usuario → el resultado otra vez (D-16, ruling W9), otro → 409; vencida → 410; 4) mayorista <c>Active</c> con
/// <c>wholesale</c> prendido (409 <c>unavailable</c>); 5) destino; 6) proveedor; 7) cliente; 8) activar, auditar y guardar.</para>
/// </summary>
public sealed class DistributorInvitationService(
    IClock clock,
    IDistributorRepository distributors,
    ITenantDirectory tenantDirectory,
    ITenantModules tenantModules,
    ITenantRegistration registration,
    ITenantModuleGrants moduleGrants,
    ITenantOwnership ownership,
    IWholesaleCompanyLookup companies,
    IWholesaleSupplierLinker supplierLinker,
    IWholesaleCustomerProvisioning customerProvisioning,
    IWholesaleUnitOfWork unitOfWork,
    IWholesaleAuditPublisher auditPublisher)
    : IDistributorInvitationService
{
    public const string SameTenantReason = "same_tenant";

    public const string AlreadyLinkedReason = "already_linked";

    private static readonly string[] AcceptedFields = ["status", "distributorTenantId", "customerId"];

    public async Task<InvitationSummaryDto> FindAsync(string token, CancellationToken cancellationToken)
    {
        var distributor = await FindByTokenAsync(token, cancellationToken);
        var wholesalerName = await tenantDirectory.GetDisplayNameAsync(new TenantId(distributor.TenantId), cancellationToken);

        return new InvitationSummaryDto(
            wholesalerName ?? DistributorComposer.UnknownName,
            distributor.BusinessName,
            distributor.ContactName,
            distributor.Email,
            distributor.InvitationState(clock.UtcNow).ToString().ToLowerInvariant(),
            distributor.Invitation.ExpiresAt);
    }

    public async Task<Distributor> RequirePendingAsync(string token, CancellationToken cancellationToken)
    {
        var distributor = await FindByTokenAsync(token, cancellationToken);
        EnsurePending(distributor, clock.UtcNow);
        return distributor;
    }

    public async Task<IReadOnlyList<LinkableTenantDto>> ListLinkableTenantsAsync(
        string token, Guid userId, string? userEmail, CancellationToken cancellationToken)
    {
        var distributor = await FindByTokenAsync(token, cancellationToken);
        EnsureEmail(distributor, userEmail);
        EnsurePending(distributor, clock.UtcNow);

        var owned = await ownership.ListOwnedActiveTenantsAsync(userId, cancellationToken);
        var linked = await distributors.LinkedTenantIdsAsync(distributor.TenantId, cancellationToken);
        var result = new List<LinkableTenantDto>(owned.Count);
        foreach (var tenant in owned)
        {
            var reason = tenant.TenantId == distributor.TenantId ? SameTenantReason
                : linked.Contains(tenant.TenantId) ? AlreadyLinkedReason
                : null;
            var modules = await tenantModules.FindAsync(tenant.TenantId, cancellationToken);
            var missing = WholesaleModuleSets.LinkedDistributorRequired
                .Where(key => modules is null || !modules.IsEnabled(key))
                .Select(key => key.Value)
                .ToList();
            result.Add(new LinkableTenantDto(tenant.TenantId, tenant.DisplayName, reason is null, reason, missing));
        }

        return result;
    }

    public async Task<AcceptedInvitationDto> AcceptAsync(
        string token,
        Guid userId,
        string? userEmail,
        AcceptInvitationChoice choice,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var now = clock.UtcNow;

        // 1-3
        var distributor = await FindByTokenAsync(token, cancellationToken);
        EnsureEmail(distributor, userEmail);
        if (distributor.Status != DistributorStatus.Invited)
        {
            return await RepeatedAsync(distributor, userId, cancellationToken);
        }

        if (distributor.Invitation.IsExpiredAt(now))
        {
            throw WholesaleConflicts.InvitationExpired();
        }

        // 4
        var wholesaler = new TenantId(distributor.TenantId);
        if (await tenantDirectory.GetStatusAsync(wholesaler, cancellationToken) != TenantStatus.Active
            || await tenantModules.FindAsync(distributor.TenantId, cancellationToken) is not { } wholesalerModules
            || !wholesalerModules.IsEnabled(TenantModuleKeys.Wholesale))
        {
            throw WholesaleConflicts.InvitationUnavailable();
        }

        var wholesalerName = await tenantDirectory.GetDisplayNameAsync(wholesaler, cancellationToken)
            ?? throw WholesaleConflicts.InvitationUnavailable();
        // D-2 y ruling W16: sin una empresa facturadora activa y con documento admitido no hay identidad para el proveedor;
        // lo arregla el mayorista.
        var company = await companies.FindAsync(distributor.TenantId, distributor.BillingCompanyId, cancellationToken);
        if (company is not { Eligible: true, IsActive: true })
        {
            throw WholesaleConflicts.InvitationUnavailable();
        }

        // 5
        var (target, created, enabled) = await ResolveTargetAsync(
            distributor, userId, wholesalerName, choice, correlationId, cancellationToken);

        // 6-7: idempotentes; un reintento devuelve el mismo proveedor y el mismo cliente.
        await supplierLinker.EnsureLinkedSupplierAsync(
            target, distributor.TenantId, wholesalerName, company, userId, cancellationToken);
        var customerId = await customerProvisioning.EnsureCustomerAsync(
            distributor.TenantId,
            distributor.InvitedByUserId,
            distributor.BusinessName,
            distributor.IdentificationNumber,
            distributor.Email,
            cancellationToken);

        // 8
        distributor.Accept(userId, target, customerId, now);
        auditPublisher.Publish(
            distributor.TenantId,
            userId,
            WholesaleAuditActions.DistributorAccepted,
            WholesaleAuditActions.Resources.Distributor,
            distributor.Id.ToString(),
            "success",
            AcceptedFields,
            now);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is RequestConcurrencyException
            || exception is ResourceConflictException { Code: WholesaleErrorCodes.DistributorAlreadyLinked })
        {
            return await AfterLostRaceAsync(token, userId, exception, cancellationToken);
        }

        var tenantName = await tenantDirectory.GetDisplayNameAsync(new TenantId(target), cancellationToken);
        return new AcceptedInvitationDto(
            target, tenantName ?? DistributorComposer.UnknownName, created, enabled.Select(key => key.Value).ToList());
    }

    private async Task<(Guid Target, bool Created, IReadOnlyList<TenantModuleKey> Enabled)> ResolveTargetAsync(
        Distributor distributor,
        Guid userId,
        string wholesalerName,
        AcceptInvitationChoice choice,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var reserved = distributor.Invitation.ReservedTenantId;
        var owned = await ownership.ListOwnedActiveTenantsAsync(userId, cancellationToken);

        // D-17: un intento anterior ya creó el tenant reservado; se termina sobre él, pida lo que pida el cuerpo. Sólo si es
        // de este usuario: nunca se le entrega a nadie un tenant ajeno.
        if (await tenantDirectory.GetStatusAsync(new TenantId(reserved), cancellationToken) is not null)
        {
            if (owned.All(tenant => tenant.TenantId != reserved))
            {
                throw WholesaleConflicts.NotTenantOwner();
            }

            return (reserved, true, WholesaleModuleSets.InvitedDistributor);
        }

        switch (choice)
        {
            case NewTenantChoice newTenant:
                await registration.EnsureRegistrableAsync(newTenant.Data, cancellationToken);
                await registration.RegisterInvitedTenantAsync(
                    new TenantId(reserved), userId, newTenant.Data, WholesaleModuleSets.InvitedDistributor, correlationId,
                    cancellationToken);
                return (reserved, true, WholesaleModuleSets.InvitedDistributor);

            case LinkTenantChoice link:
                if (owned.All(tenant => tenant.TenantId != link.TenantId))
                {
                    throw WholesaleConflicts.NotTenantOwner();
                }

                if (link.TenantId == distributor.TenantId)
                {
                    throw WholesaleConflicts.SelfLink();
                }

                if (await distributors.IsLinkedAsync(distributor.TenantId, link.TenantId, cancellationToken))
                {
                    throw WholesaleConflicts.AlreadyLinked();
                }

                var enabled = await moduleGrants.EnsureActiveAsync(
                    new TenantId(link.TenantId),
                    WholesaleModuleSets.LinkedDistributorRequired,
                    userId,
                    $"Invitación de mayorista: {wholesalerName}",
                    cancellationToken);
                return (link.TenantId, false, enabled);

            default:
                throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unknown acceptance mode.");
        }
    }

    /// <summary>
    /// Otra pestaña ganó el guardado (412) o el tenant ya quedó vinculado por otra relación (el índice único). Se sueltan los
    /// cambios y se relee: si la aceptó este mismo usuario, la respuesta es la de un doble clic.
    /// </summary>
    private async Task<AcceptedInvitationDto> AfterLostRaceAsync(
        string token, Guid userId, Exception lost, CancellationToken cancellationToken)
    {
        unitOfWork.DiscardPendingChanges();
        var fresh = await distributors.FindByTokenHashAsync(InvitationTokens.HashOf(token), cancellationToken)
            ?? throw WholesaleConflicts.InvitationNotFound();
        if (fresh.Status != DistributorStatus.Invited)
        {
            return await RepeatedAsync(fresh, userId, cancellationToken);
        }

        if (lost is ResourceConflictException)
        {
            throw WholesaleConflicts.AlreadyLinked();
        }

        throw lost;
    }

    /// <summary>D-16 y ruling W9: la misma persona vuelve a aceptar. No escribe nada.</summary>
    private async Task<AcceptedInvitationDto> RepeatedAsync(Distributor distributor, Guid userId, CancellationToken cancellationToken)
    {
        if (distributor.Invitation.AcceptedByUserId != userId || distributor.DistributorTenantId is not { } tenantId)
        {
            throw WholesaleConflicts.InvitationAlreadyAccepted();
        }

        // Ruling W16: con la relación bloqueada no hay «el mismo resultado» que devolver.
        if (distributor.Status == DistributorStatus.Blocked)
        {
            throw WholesaleConflicts.RelationshipInactive();
        }

        var name = await tenantDirectory.GetDisplayNameAsync(new TenantId(tenantId), cancellationToken);
        return new AcceptedInvitationDto(tenantId, name ?? DistributorComposer.UnknownName, false, []);
    }

    private async Task<Distributor> FindByTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw WholesaleConflicts.InvitationNotFound();
        }

        return await distributors.FindByTokenHashAsync(InvitationTokens.HashOf(token), cancellationToken)
            ?? throw WholesaleConflicts.InvitationNotFound();
    }

    private static void EnsureEmail(Distributor distributor, string? userEmail)
    {
        if (!distributor.InvitedEmailMatches(userEmail))
        {
            throw WholesaleConflicts.EmailMismatch();
        }
    }

    /// <summary>El dominio diría 422; el contrato es 410 para la vencida y 409 para la aceptada.</summary>
    private static void EnsurePending(Distributor distributor, DateTimeOffset now)
    {
        switch (distributor.InvitationState(now))
        {
            case InvitationViewState.Expired:
                throw WholesaleConflicts.InvitationExpired();
            case InvitationViewState.Accepted:
                throw WholesaleConflicts.InvitationAlreadyAccepted();
        }
    }
}
