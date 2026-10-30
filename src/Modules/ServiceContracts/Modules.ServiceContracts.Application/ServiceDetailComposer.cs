using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <summary>
/// Arma las respuestas de servicio del contrato (spec §4): el <see cref="ServiceDetailDto"/> de crear, leer, editar y cambiar de
/// estado, y los <see cref="ServiceDto"/> de la lista. El detalle lo reutiliza todo mutador del servicio.
/// </summary>
public sealed class ServiceDetailComposer(
    ITenantClock tenantClock,
    IServiceContractsCustomerLookup customers,
    IServiceContractsMemberDirectory members,
    ICustomerBillingProfileRepository profiles,
    IChargeRepository chargeRepository,
    ChargeDtoComposer chargeComposer)
{
    /// <summary>
    /// El detalle de un servicio ya cargado (y acotado a su tenant) por quien llama: cliente, perfil con su encargado y todos sus
    /// cobros por <c>dueDate</c> desc. Se llama después de guardar: lee los cobros de la base, no del contexto.
    /// </summary>
    public async Task<ServiceDetailDto> ComposeAsync(ServiceContract service, CancellationToken cancellationToken)
    {
        var tenantId = service.TenantId;
        var today = (await tenantClock.GetAsync(tenantId, cancellationToken)).Today;

        var charges = await chargeRepository.ListByServiceAsync(service.Id, cancellationToken);
        var services = new Dictionary<ServiceContractId, ServiceContract> { [service.Id] = service };
        var chargeDtos = await chargeComposer.ComposeAsync(tenantId, charges, services, today, cancellationToken);

        var customer = await FindCustomerAsync(tenantId, service.CustomerId, cancellationToken);
        var profile = await profiles.FindAsync(tenantId, service.CustomerId, cancellationToken)
            ?? throw ServiceContractsNotFound.Profile(service.CustomerId);
        var found = await members.FindAsync(tenantId, [profile.AssigneeMemberId], cancellationToken);
        var profileDto = ServiceContractsMapping.ToProfileDto(
            profile, ServiceContractsMapping.ToMemberRefDto(profile.AssigneeMemberId, found.Count > 0 ? found[0] : null));

        var pending = charges.Where(charge => charge.Status == ChargeStatus.Pending).ToList();
        var next = pending.MinBy(charge => charge.DueDate);
        var nextDto = next is null ? null : chargeDtos.First(dto => dto.Id == next.Id);

        return new ServiceDetailDto(
            ServiceContractsMapping.ToServiceDto(service, customer, nextDto, pending.Count, TotalsOf(pending)),
            profileDto,
            chargeDtos);
    }

    /// <summary>
    /// Los <see cref="ServiceDto"/> de una página de servicios, en el mismo orden. <paramref name="customerRefs"/> son los clientes
    /// que la lista ya resolvió para ordenar; los cobros abiertos de todos los servicios salen de una sola consulta.
    /// </summary>
    public async Task<IReadOnlyList<ServiceDto>> ComposeSummariesAsync(
        Guid tenantId,
        IReadOnlyList<ServiceContract> page,
        IReadOnlyDictionary<Guid, CustomerRefDto> customerRefs,
        CancellationToken cancellationToken)
    {
        if (page.Count == 0)
        {
            return [];
        }

        var today = (await tenantClock.GetAsync(tenantId, cancellationToken)).Today;
        var pending = await chargeRepository.ListPendingByServicesAsync(
            page.Select(service => service.Id).ToArray(), cancellationToken);
        var pendingByService = pending.ToLookup(charge => charge.ServiceId);

        // Sólo el próximo cobro de cada servicio se arma completo (cliente, encargado, último aviso); el resto sólo cuenta.
        var nextCharges = page
            .Select(service => pendingByService[service.Id].MinBy(charge => charge.DueDate))
            .OfType<Charge>()
            .ToList();
        var servicesById = page.ToDictionary(service => service.Id);
        var nextDtos = (await chargeComposer.ComposeAsync(tenantId, nextCharges, servicesById, today, cancellationToken))
            .ToDictionary(dto => dto.Id);

        return page.Select(service =>
        {
            var open = pendingByService[service.Id].ToList();
            var next = open.MinBy(charge => charge.DueDate);
            var customer = customerRefs.TryGetValue(service.CustomerId, out var found)
                ? found
                : new CustomerRefDto(service.CustomerId, ChargeDtoComposer.UnknownCustomerName, null, null);
            return ServiceContractsMapping.ToServiceDto(
                service, customer, next is null ? null : nextDtos[next.Id], open.Count, TotalsOf(open));
        }).ToList();
    }

    private static IReadOnlyList<CurrencyAmountDto> TotalsOf(IEnumerable<Charge> pending) =>
        CurrencyTotals.Of(pending.Select(charge => (charge.Currency, charge.Amount)));

    private async Task<CustomerRefDto> FindCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customers.FindAsync(tenantId, customerId, cancellationToken);
        return customer is null
            ? new CustomerRefDto(customerId, ChargeDtoComposer.UnknownCustomerName, null, null)
            : new CustomerRefDto(customer.Id, customer.Name, customer.PhoneE164, customer.Email);
    }
}
