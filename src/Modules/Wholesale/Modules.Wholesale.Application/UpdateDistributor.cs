using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

public sealed record UpdateDistributorCommand(
    Guid TenantId,
    Guid DistributorId,
    long ExpectedVersion,
    Guid PriceListId,
    Guid AdvisorId,
    Guid BillingCompanyId,
    string? Phone = null,
    Guid? WhatsAppConnectionId = null,
    string? BusinessName = null,
    string? Country = null,
    string? IdentificationType = null,
    string? IdentificationNumber = null,
    string? ContactName = null,
    string? Email = null,
    Guid? CityId = null,
    string? CityName = null,
    string? Address = null)
    : ICommand<DistributorDetailDto>;

/// <summary>
/// Lista, asesor y empresa facturadora, en cualquier estado. Orden: 403 → 422 → 404 → 412 → 409 datos fuera de
/// <c>Invited</c> → las tres verificaciones → 422 de los datos → 422 ciudad → 409 documento → 409 aceptación a medias
/// (sólo si cambia tipo o número y la aceptación ya empezó o el tenant reservado ya existe) → 422 <c>Phone</c> → 422
/// conexión. Un <c>PUT</c> que cargó antes de la marca de la aceptación pierde el guardado con 412: la marca sube la versión.
/// Teléfono y conexión del aviso (spec 2026-11-18) sólo cuentan mientras está invitada (P-2): afuera se ignoran sin 422,
/// porque el formulario reenvía todos los campos. Sin teléfono se borran los dos (P-3). El mismo teléfono con la conexión nula
/// o igual a la guardada no consulta Integrations (D-9): editar no falla porque la conexión dejó de estar lista. Un cambio deja
/// el aviso en <c>null</c> hasta el próximo «Reenviar» (P-3).
/// <para>
/// Decisión del owner 2026-11-24: razón social, país, documento, contacto, correo, ciudad y dirección se editan mientras está
/// invitada, con las reglas de invitar; un campo ausente conserva lo guardado. Fuera de <c>Invited</c>, mandar uno distinto del
/// guardado es <c>wholesale.distributor.invalid_transition</c> (el mismo valor se acepta, porque el formulario reenvía todo).
/// Cambiar el correo <b>no</b> reenvía la invitación ni rota el token: el mayorista usa «Reenviar».
/// </para>
/// </summary>
public sealed class UpdateDistributorHandler(
    IExecutionContext executionContext,
    IClock clock,
    IWholesalePriceLists priceLists,
    IDistributorRepository distributors,
    IWholesaleMemberDirectory members,
    IWholesaleCompanyLookup companies,
    IWholesaleCityLookup cities,
    ITenantDirectory tenants,
    IWholesalePhoneNormalizer phones,
    IWholesaleWhatsAppConnections whatsAppConnections,
    IWholesaleUnitOfWork unitOfWork,
    IWholesaleAuditPublisher auditPublisher,
    DistributorComposer composer,
    IValidator<UpdateDistributorCommand> validator)
    : ICommandHandler<UpdateDistributorCommand, DistributorDetailDto>
{
    private static readonly string[] WrittenFields = ["priceListId", "advisorId", "billingCompanyId"];

    // Mientras está invitada el PUT también escribe el destino del aviso; el valor (el teléfono) nunca va a la auditoría.
    private static readonly string[] InvitedWrittenFields = [.. WrittenFields, "phone", "whatsAppConnectionId"];

    public async Task<DistributorDetailDto> HandleAsync(UpdateDistributorCommand command, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, command.TenantId, WholesalePermissions.DistributorManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var distributor = await distributors.GetAsync(command.TenantId, new DistributorId(command.DistributorId), cancellationToken)
            ?? throw WholesaleConflicts.DistributorNotFound(command.DistributorId);
        WholesaleConflicts.EnsureVersion(distributor.Version, command.ExpectedVersion);
        WholesaleConflicts.EnsureAllowed(distributor, DistributorAction.Edit);
        var invited = distributor.Status == DistributorStatus.Invited;
        var profile = DistributorProfile.Merge(distributor, command);
        var changed = profile.ChangedFields(distributor);
        if (!invited && ChangesData(distributor, changed))
        {
            throw WholesaleConflicts.InvalidTransition();
        }

        await DistributorTerms.EnsureValidAsync(
            priceLists, members, companies, command.TenantId, command.PriceListId, command.AdvisorId, command.BillingCompanyId,
            cancellationToken);
        // Sin cambios no se revalida: una invitación anterior a la ciudad se sigue editando como antes.
        var changesProfile = invited && changed.Count > 0;
        if (changesProfile)
        {
            await EnsureProfileValidAsync(distributor, profile, changed, command, cancellationToken);
        }

        if (invited && ChangesDocument(changed))
        {
            await EnsureAcceptanceNotStartedAsync(distributor, cancellationToken);
        }

        var (phone, connectionId) = invited ? await NoticeTargetAsync(distributor, command, cancellationToken) : (null, null);

        var now = clock.UtcNow;
        if (changesProfile)
        {
            distributor.ChangeProfile(
                profile.Email, profile.BusinessName, profile.Country, profile.IdentificationType, profile.IdentificationNumber,
                profile.ContactName, profile.CityId, profile.CityName, profile.Address);
        }

        if (invited)
        {
            distributor.ChangeNoticeTarget(phone, connectionId);
        }

        distributor.ChangeTerms(new PriceListId(command.PriceListId), command.AdvisorId, command.BillingCompanyId, now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            WholesaleAuditActions.DistributorUpdated,
            WholesaleAuditActions.Resources.Distributor,
            distributor.Id.ToString(),
            "success",
            invited ? [.. InvitedWrittenFields, .. changed] : WrittenFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.DetailAsync(distributor, cancellationToken);
    }

    // La ciudad que no aplica al país (CityName en «CO», CityId afuera) no cuenta como cambio fuera de Invited: el formulario
    // puede reenviar el nombre que pinta el detalle junto al id.
    private static bool ChangesData(Distributor distributor, IReadOnlyList<string> changed)
    {
        var ignored = DistributorProfileValidator.IsColombian(distributor.Country) ? "cityName" : "cityId";
        return changed.Any(field => field != ignored);
    }

    private async Task EnsureProfileValidAsync(
        Distributor distributor,
        DistributorProfile profile,
        IReadOnlyList<string> changed,
        UpdateDistributorCommand command,
        CancellationToken cancellationToken)
    {
        var changesCountry = changed.Contains("country");
        var sendsCity = DistributorProfileValidator.IsColombian(profile.Country)
            ? command.CityId is not null
            : command.CityName is not null;
        var demandCity = changesCountry || sendsCity || distributor.HasCompanyCity;
        await new DistributorProfileValidator(demandCity).ValidateAndThrowAsync(profile, cancellationToken);

        if (changesCountry || changed.Contains("cityId"))
        {
            await DistributorTerms.EnsureCityExistsAsync(cities, profile.Country, profile.CityId, cancellationToken);
        }

        // D-13: contra los demás distribuidores del mayorista.
        if (ChangesDocument(changed)
            && await distributors.IdentificationExistsAsync(
                command.TenantId, DistributorProfile.Code(profile.IdentificationType), profile.IdentificationNumber.Trim(),
                distributor.Id, cancellationToken))
        {
            throw WholesaleConflicts.IdentificationTaken();
        }
    }

    // El país no es parte del documento: si cambiarlo obliga a otro tipo, ya cuenta por identificationType.
    private static bool ChangesDocument(IReadOnlyList<string> changed) =>
        changed.Contains("identificationType") || changed.Contains("identificationNumber");

    // La aceptación no es atómica entre módulos (D-3): si ya empezó, la empresa y el cliente pueden existir con este documento, y
    // como el reintento es idempotente por documento, cambiarlo los duplicaría. La marca la pone la aceptación antes de escribir
    // (también al vincular un tenant existente); el tenant reservado (D-17) cubre las que quedaron a medias antes de la marca.
    private async Task EnsureAcceptanceNotStartedAsync(Distributor distributor, CancellationToken cancellationToken)
    {
        if (distributor.AcceptanceStartedAt is not null)
        {
            throw WholesaleConflicts.AcceptanceInProgress();
        }

        var reserved = new TenantId(distributor.Invitation.ReservedTenantId);
        if (await tenants.GetStatusAsync(reserved, cancellationToken) is not null)
        {
            throw WholesaleConflicts.AcceptanceInProgress();
        }
    }

    private async Task<(string? Phone, Guid? ConnectionId)> NoticeTargetAsync(
        Distributor distributor, UpdateDistributorCommand command, CancellationToken cancellationToken)
    {
        var phone = await DistributorWhatsAppNotice.NormalizeAsync(tenants, phones, command.TenantId, command.Phone, cancellationToken);
        if (phone is null)
        {
            return (null, null);
        }

        // D-9: nada cambió, nada se resuelve.
        if (phone == distributor.Phone
            && distributor.WhatsAppConnectionId is { } saved
            && (command.WhatsAppConnectionId is null || command.WhatsAppConnectionId == saved))
        {
            return (phone, saved);
        }

        var connectionId = await DistributorWhatsAppNotice.ResolveConnectionAsync(
            whatsAppConnections, command.TenantId, command.WhatsAppConnectionId, cancellationToken);
        return (phone, connectionId);
    }
}
