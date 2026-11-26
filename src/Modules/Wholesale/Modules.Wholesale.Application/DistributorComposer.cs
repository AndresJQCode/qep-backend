using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// Arma los DTOs de distribuidores. Listas, nombres de miembros y nombres de tenants se resuelven en lote (una llamada por
/// puerto, nunca una por fila), y <c>allowedActions</c> y <c>canResend</c> cruzan el estado con
/// <c>wholesale.distributor.manage</c>. El label de la conexión del aviso sólo se pide en el detalle y sólo si hay aviso (D-7).
/// </summary>
public sealed class DistributorComposer(
    IExecutionContext executionContext,
    IClock clock,
    IWholesalePriceLists priceLists,
    IWholesaleMemberDirectory members,
    IWholesaleCompanyLookup companies,
    IWholesaleCityLookup cities,
    IWholesaleTenantDirectory tenants,
    IWholesaleWhatsAppConnections whatsAppConnections)
{
    /// <summary>Lo que se muestra cuando la membresía, la lista, la empresa o el tenant ya no se encuentran.</summary>
    public const string UnknownName = "Sin nombre";

    public async Task<DistributorDetailDto> DetailAsync(Distributor distributor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distributor);

        var lists = await ListsAsync(distributor.TenantId, cancellationToken);
        var names = await members.FindNamesAsync(distributor.TenantId, [distributor.AdvisorMemberId], cancellationToken);
        var company = await companies.FindAsync(distributor.TenantId, distributor.BillingCompanyId, cancellationToken);
        var tenantNames = await TenantNamesAsync([distributor], cancellationToken);
        var notice = await NoticeOfAsync(distributor, cancellationToken);
        var city = await CityOfAsync(distributor, cancellationToken);
        var now = clock.UtcNow;
        var invitation = distributor.Invitation;

        return new DistributorDetailDto(
            distributor.Id.Value,
            distributor.Status.ToString(),
            distributor.Version,
            distributor.BusinessName,
            distributor.Country,
            distributor.IdentificationType,
            distributor.IdentificationNumber,
            distributor.ContactName,
            distributor.Email,
            distributor.Phone,
            PriceListOf(distributor, lists),
            AdvisorOf(distributor, names),
            new DistributorCompanyDto(distributor.BillingCompanyId, company?.Name ?? UnknownName),
            TenantOf(distributor, tenantNames),
            distributor.CustomerId,
            new DistributorInvitationDto(
                invitation.SentAt, invitation.ExpiresAt, IsExpired(distributor, now), invitation.AcceptedAt, notice),
            distributor.CreatedAt,
            distributor.ActivatedAt,
            AllowedActions(distributor),
            City: city,
            Address: distributor.Address);
    }

    public async Task<IReadOnlyList<DistributorSummaryDto>> SummariesAsync(
        Guid tenantId, IReadOnlyList<Distributor> distributors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(distributors);
        if (distributors.Count == 0)
        {
            return [];
        }

        var lists = await ListsAsync(tenantId, cancellationToken);
        var names = await members.FindNamesAsync(
            tenantId, distributors.Select(item => item.AdvisorMemberId).Distinct().ToArray(), cancellationToken);
        var tenantNames = await TenantNamesAsync(distributors, cancellationToken);
        var canManage = executionContext.HasPermission(WholesalePermissions.DistributorManage);
        var now = clock.UtcNow;

        return distributors
            .Select(distributor => new DistributorSummaryDto(
                distributor.Id.Value,
                distributor.Status.ToString(),
                distributor.Version,
                distributor.BusinessName,
                distributor.Country,
                distributor.IdentificationType,
                distributor.IdentificationNumber,
                distributor.ContactName,
                distributor.Email,
                distributor.Phone,
                PriceListOf(distributor, lists),
                AdvisorOf(distributor, names),
                TenantOf(distributor, tenantNames),
                IsExpired(distributor, now),
                distributor.Status == DistributorStatus.Invited
                    ? new DistributorInvitationSummaryDto(distributor.Invitation.ExpiresAt, canManage)
                    : null,
                distributor.CreatedAt))
            .ToList();
    }

    private async Task<IReadOnlyDictionary<Guid, string>> TenantNamesAsync(
        IReadOnlyList<Distributor> distributors, CancellationToken cancellationToken)
    {
        var ids = distributors
            .Where(item => item.DistributorTenantId.HasValue)
            .Select(item => item.DistributorTenantId!.Value)
            .Distinct()
            .ToArray();
        return ids.Length == 0 ? new Dictionary<Guid, string>() : await tenants.FindNamesAsync(ids, cancellationToken);
    }

    private async Task<DistributorWhatsAppNoticeDto?> NoticeOfAsync(
        Distributor distributor, CancellationToken cancellationToken)
    {
        if (distributor.Invitation.NoticeStatus is not { } status || distributor.WhatsAppConnectionId is not { } connectionId)
        {
            return null;
        }

        var label = await whatsAppConnections.FindLabelAsync(distributor.TenantId, connectionId, cancellationToken);
        return new DistributorWhatsAppNoticeDto(status.ToString(), distributor.Invitation.NoticeFailureCode, label ?? UnknownName);
    }

    // La ciudad resuelta de Colombia, o la escrita afuera; nula en una invitación anterior a la migración.
    private async Task<DistributorCityDto?> CityOfAsync(Distributor distributor, CancellationToken cancellationToken)
    {
        if (distributor.CityId is { } cityId)
        {
            var city = await cities.FindAsync(cityId, cancellationToken);
            return new DistributorCityDto(cityId, city?.Name ?? UnknownName, city?.DepartmentId, city?.DepartmentName);
        }

        return distributor.CityName is { } cityName ? new DistributorCityDto(null, cityName) : null;
    }

    private static bool IsExpired(Distributor distributor, DateTimeOffset now) =>
        distributor.InvitationState(now) == InvitationViewState.Expired;

    /// <summary>Un tenant tiene un puñado de listas: se leen todas una vez, en vez de una consulta por id contra Catalog.</summary>
    private async Task<IReadOnlyDictionary<PriceListId, WholesalePriceList>> ListsAsync(
        Guid tenantId, CancellationToken cancellationToken) =>
        (await priceLists.ListAsync(tenantId, cancellationToken)).ToDictionary(list => list.Id);

    /// <summary>
    /// Spec 2026-11-22 (D-8): un distribuidor cuya lista ya no existe —o que viene de antes de la unificación— sale con
    /// <see cref="UnknownName"/> y <c>Exists = false</c>, para que la pantalla le pida elegir una.
    /// </summary>
    private static DistributorPriceListDto PriceListOf(
        Distributor distributor, IReadOnlyDictionary<PriceListId, WholesalePriceList> lists) =>
        lists.TryGetValue(distributor.PriceListId, out var list)
            ? new DistributorPriceListDto(list.Id.Value, list.Name, list.AdjustmentPercent)
            : new DistributorPriceListDto(distributor.PriceListId.Value, UnknownName, 0m, Exists: false);

    private static DistributorAdvisorDto AdvisorOf(Distributor distributor, IReadOnlyDictionary<Guid, string> names) =>
        new(distributor.AdvisorMemberId,
            names.TryGetValue(distributor.AdvisorMemberId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : UnknownName);

    private static DistributorTenantDto? TenantOf(Distributor distributor, IReadOnlyDictionary<Guid, string> names) =>
        distributor.DistributorTenantId is { } id
            ? new DistributorTenantDto(id, names.TryGetValue(id, out var name) ? name : UnknownName)
            : null;

    private List<string> AllowedActions(Distributor distributor) =>
        executionContext.HasPermission(WholesalePermissions.DistributorManage)
            ? distributor.AllowedActions().Select(action => action.ToString().ToLowerInvariant()).ToList()
            : [];
}
