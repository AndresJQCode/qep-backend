using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// La invitación vista desde el link, fuera del tenant del mayorista: la consultan sin sesión, la usan las rutas
/// <c>session*</c> antes de tocar Identity y la acepta quien tiene una sesión, con cualquier correo. El tenant del mayorista sale
/// de la invitación, no de un <c>IExecutionContext</c>: quien llama todavía no es miembro de nada.
/// </summary>
public interface IDistributorInvitationService
{
    /// <summary>El resumen del link (D-20). 404 <c>not_found</c> si el token no existe, rotó o el distribuidor se borró.</summary>
    Task<InvitationSummaryDto> FindAsync(string token, CancellationToken cancellationToken);

    /// <summary>La invitación todavía aceptable: 404 → 410 <c>expired</c> → 409 <c>already_accepted</c>, en ese orden.</summary>
    Task<Distributor> RequirePendingAsync(string token, CancellationToken cancellationToken);

    /// <summary>Los tenants que el usuario administra como owner o admin, con si se pueden vincular y qué módulos prendería.
    /// <paramref name="userEmail"/> ya no se compara con el invitado (spec 2026-11-19).</summary>
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
/// La aceptación son pasos idempotentes sin transacción común (D-3): tenant (con el id reservado), módulos, empresa y proveedor
/// en el distribuidor, cliente en el mayorista y, al final, el commit de <c>wholesale</c> que consume la invitación. Un fallo a mitad
/// deja la invitación pendiente y el reintento termina sobre el mismo tenant.
/// <para>Antes de la primera escritura en otro módulo se confirma la marca <c>AcceptanceStartedAt</c>
/// (<see cref="Distributor.StartAcceptance"/>), que le cierra al mayorista el cambio de documento. Va después de todo lo que
/// rechaza sin escribir, así que un accept rechazado no deja marca, y no consume la invitación ni cambia el estado. Si su
/// guardado pierde por concurrencia (un <c>PUT</c> o la otra pestaña confirmaron entre la lectura y la marca), se suelta lo
/// rastreado y se vuelve a empezar desde la lectura: nunca se escribe en otro módulo con datos viejos.</para>
/// <para>Orden fijo: 1) invitación por hash (404); 2-3) ya aceptada: mismo usuario → el resultado otra vez (D-16, ruling W9),
/// otro → 409; vencida → 410; 4) mayorista <c>Active</c> con <c>wholesale</c> prendido (409 <c>unavailable</c>); 5) destino:
/// sus validaciones, la marca confirmada y recién entonces su escritura (tenant nuevo o módulos);
/// 6) empresa del distribuidor: siempre si <c>created</c> (el tenant reservado, también en un reintento D-17 con cuerpo
/// <c>LinkTenant</c>), y al vincular un tenant que ya existía sólo si no tiene ninguna; en los dos casos la invitación tiene que
/// traer la ciudad que su país exige; 7) proveedor;
/// 8) cliente; 9) correo de la cuenta, activar, auditar y guardar. Spec 2026-11-19: el link identifica la
/// invitación y el correo ya no es una llave; <c>userEmail</c> es el de la cuenta que acepta y, si es otro, reemplaza al
/// invitado en el mismo guardado.</para>
/// </summary>
public sealed partial class DistributorInvitationService(
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
    IWholesaleCompanyProvisioning companyProvisioning,
    IWholesaleUnitOfWork unitOfWork,
    IWholesaleAuditPublisher auditPublisher,
    IWholesaleEventPublisher eventPublisher,
    ILogger<DistributorInvitationService> logger)
    : IDistributorInvitationService
{
    public const string SameTenantReason = "same_tenant";

    public const string AlreadyLinkedReason = "already_linked";

    private static readonly string[] AcceptedFields = ["status", "distributorTenantId", "customerId"];

    // Spec 2026-11-19 §6: el campo, nunca los correos.
    private static readonly string[] EmailChangedFields = ["email"];

    // Cuántas veces se vuelve a leer si la marca pierde el guardado: cada derrota es otro guardado que confirmó en el medio.
    private const int StartRetries = 3;

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
        EnsurePending(distributor, clock.UtcNow);

        var administered = await ownership.ListAdministeredActiveTenantsAsync(userId, cancellationToken);
        var linked = await distributors.LinkedTenantIdsAsync(distributor.TenantId, cancellationToken);
        var result = new List<LinkableTenantDto>(administered.Count);
        foreach (var tenant in administered)
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

        for (var retry = 0; ; retry++)
        {
            // 1-3
            var distributor = await FindByTokenAsync(token, cancellationToken);
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
            // D-2 y ruling W16: sin una empresa facturadora activa y con documento admitido no hay identidad para el
            // proveedor; lo arregla el mayorista.
            var company = await companies.FindAsync(distributor.TenantId, distributor.BillingCompanyId, cancellationToken);
            if (company is not { Eligible: true, IsActive: true })
            {
                throw WholesaleConflicts.InvitationUnavailable();
            }

            // 5: primero lo que rechaza sin escribir; la escritura queda en el plan.
            var plan = await PlanTargetAsync(
                distributor, userId, wholesalerName, choice, correlationId, cancellationToken);

            // La marca, confirmada antes de escribir en otro módulo. Un reintento ya la encuentra y no guarda nada.
            if (distributor.StartAcceptance(now))
            {
                try
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                }
                catch (RequestConcurrencyException) when (retry < StartRetries)
                {
                    unitOfWork.DiscardPendingChanges();
                    continue;
                }
            }

            return await CompleteAsync(
                token, distributor, plan, wholesalerName, company, userId, userEmail, correlationId, now, cancellationToken);
        }
    }

    /// <summary>Todo lo que escribe, ya con la marca confirmada: la escritura del destino y los pasos 6 a 9.</summary>
    private async Task<AcceptedInvitationDto> CompleteAsync(
        string token,
        Distributor distributor,
        TargetPlan plan,
        string wholesalerName,
        WholesaleCompanyRef company,
        Guid userId,
        string? userEmail,
        string correlationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (target, created) = (plan.TenantId, plan.Created);
        var enabled = await plan.WriteAsync(cancellationToken);

        // El correo que queda en el distribuidor (el de la cuenta, si vino), no el que se va a reemplazar: lo usan la empresa y el
        // cliente.
        var email = string.IsNullOrWhiteSpace(userEmail) ? distributor.Email : userEmail.Trim().ToLowerInvariant();

        // 6: la empresa del distribuidor (spec 2026-11-23 §3; owner, 2026-11-24). Si `created`, el tenant es el que se reservó al invitar:
        // la empresa nace siempre, también en un reintento D-17 con cuerpo LinkTenant. Si no (vinculó un tenant que ya existía), sólo
        // nace si ese tenant no tiene ninguna empresa: Companies lo decide bajo candado. Una invitación de antes de la migración no trae
        // la ciudad que su país exige: acepta sin empresa, no se rompe una invitación viva.
        if (distributor.HasCompanyCity)
        {
            var draft = new WholesaleCompanyDraft(
                distributor.BusinessName,
                distributor.Country,
                distributor.IdentificationType,
                distributor.IdentificationNumber,
                distributor.CityId,
                distributor.CityName,
                distributor.Address,
                email,
                distributor.Phone);
            if (created)
            {
                await companyProvisioning.EnsureCompanyAsync(target, userId, draft, cancellationToken);
            }
            else
            {
                await companyProvisioning.EnsureFirstCompanyAsync(target, userId, draft, cancellationToken);
            }
        }
        else
        {
            LogCompanySkippedWithoutCity(distributor.Id.Value);
        }

        // 7-8: idempotentes; un reintento devuelve el mismo proveedor y el mismo cliente.
        await supplierLinker.EnsureLinkedSupplierAsync(
            target, distributor.TenantId, wholesalerName, company, userId, cancellationToken);
        var customerId = await customerProvisioning.EnsureCustomerAsync(
            distributor.TenantId,
            distributor.InvitedByUserId,
            distributor.BusinessName,
            distributor.Country,
            distributor.IdentificationType,
            distributor.IdentificationNumber,
            email,
            distributor.Phone,
            distributor.CityId,
            distributor.CityName,
            distributor.Address,
            distributor.ContactName,
            cancellationToken);

        // 9: el correo de la cuenta, la activación, sus auditorías y el evento van en el mismo guardado (spec 2026-11-19 §6): si
        // el commit falla no cambia nada.
        ReplaceEmail(distributor, userId, userEmail, correlationId, now);
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

    // Sólo el id del distribuidor: nada de su razón social, su documento ni su correo en el log.
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Distributor {DistributorId} accepted without a company: the invitation has no city for its country.")]
    private partial void LogCompanySkippedWithoutCity(Guid distributorId);

    /// <summary>El destino ya validado. <see cref="WriteAsync"/> es su escritura en Tenancy (registrar el tenant o prender
    /// módulos) y devuelve los módulos que reporta la respuesta; no se llama antes de confirmar la marca.</summary>
    private sealed record TargetPlan(
        Guid TenantId, bool Created, Func<CancellationToken, Task<IReadOnlyList<TenantModuleKey>>> WriteAsync);

    // El tenant reservado ya existe y es de este usuario: no hay nada que escribir en Tenancy.
    private static TargetPlan OverExistingReservedTenant(Guid reserved) =>
        new(reserved, true, _ => Task.FromResult(WholesaleModuleSets.InvitedDistributor));

    /// <summary>Elige el destino y hace todas sus validaciones. No escribe.</summary>
    private async Task<TargetPlan> PlanTargetAsync(
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

            return OverExistingReservedTenant(reserved);
        }

        switch (choice)
        {
            case NewTenantChoice newTenant:
                // Spec 2026-11-22: el tenant nuevo nace con el país del distribuidor guardado, nunca con uno del cuerpo del accept.
                var tenantData = newTenant.Data with { Country = distributor.Country };
                try
                {
                    await registration.EnsureRegistrableAsync(tenantData, cancellationToken);
                }
                catch (TenantDomainException)
                {
                    // Review Focus 1: la otra pestaña commiteó el tenant reservado entre la relectura de arriba y este chequeo, y
                    // el slug «tomado» es el suyo. Se termina sobre él como en D-17; cualquier otro rechazo sale tal cual.
                    if (!await IsReservedAndOwnedAsync(reserved, userId, cancellationToken))
                    {
                        throw;
                    }

                    return OverExistingReservedTenant(reserved);
                }

                return new TargetPlan(reserved, true, async cancellation =>
                {
                    await registration.RegisterInvitedTenantAsync(
                        new TenantId(reserved), userId, tenantData, WholesaleModuleSets.InvitedDistributor, correlationId,
                        cancellation);
                    return WholesaleModuleSets.InvitedDistributor;
                });

            case LinkTenantChoice link:
                // Spec 2026-11-19 §5: owner o rol de sistema admin en una membresía activa. El código sigue siendo el de siempre.
                var administered = await ownership.ListAdministeredActiveTenantsAsync(userId, cancellationToken);
                if (administered.All(tenant => tenant.TenantId != link.TenantId))
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

                return new TargetPlan(link.TenantId, false, cancellation => moduleGrants.EnsureActiveAsync(
                    new TenantId(link.TenantId),
                    WholesaleModuleSets.LinkedDistributorRequired,
                    userId,
                    $"Invitación de mayorista: {wholesalerName}",
                    cancellation));

            default:
                throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unknown acceptance mode.");
        }
    }

    private async Task<bool> IsReservedAndOwnedAsync(Guid reserved, Guid userId, CancellationToken cancellationToken) =>
        await tenantDirectory.GetStatusAsync(new TenantId(reserved), cancellationToken) is not null
        && (await ownership.ListOwnedActiveTenantsAsync(userId, cancellationToken)).Any(tenant => tenant.TenantId == reserved);

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

    /// <summary>
    /// Decisión del owner (2026-11-19): prevalece el correo de la cuenta que acepta, sin aprobación y con aviso a las dos partes.
    /// Sin correo de cuenta (no debería pasar: toda cuenta tiene uno) se conserva el invitado.
    /// </summary>
    private void ReplaceEmail(Distributor distributor, Guid userId, string? userEmail, string correlationId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(userEmail))
        {
            return;
        }

        var previous = distributor.Email;
        if (!distributor.ReplaceEmail(userEmail, now))
        {
            return;
        }

        auditPublisher.Publish(
            distributor.TenantId,
            userId,
            WholesaleAuditActions.DistributorEmailChanged,
            WholesaleAuditActions.Resources.Distributor,
            distributor.Id.ToString(),
            "success",
            EmailChangedFields,
            now);
        eventPublisher.DistributorEmailChanged(
            distributor.TenantId, distributor.Id.Value, previous, distributor.Email, distributor.BusinessName, correlationId, now);
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
