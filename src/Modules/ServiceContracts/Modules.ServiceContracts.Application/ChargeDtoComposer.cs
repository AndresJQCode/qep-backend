using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Application;

/// <summary>
/// Arma el <see cref="ChargeDto"/> de una lista de cobros con todo lo que la pantalla pinta: cliente, encargado (del perfil del
/// cliente), quién registró el pago y el último aviso. Un solo armador para el detalle del servicio, <c>nextCharge</c> de la
/// lista y la bandeja «Por cobrar» (ruling F2): cada lectura es un viaje por lote, nunca uno por fila.
/// </summary>
public sealed class ChargeDtoComposer(
    IServiceContractsCustomerLookup customers,
    IServiceContractsMemberDirectory members,
    ICustomerBillingProfileRepository profiles,
    IChargeRepository chargeRepository)
{
    // Lo que se muestra de un cliente que el directorio ya no resuelve (la fila se purgó o se fusionó): el nombre vacío no
    // es mejor que este rótulo y perder la fila de la lista sería peor.
    internal const string UnknownCustomerName = "Sin nombre";

    /// <summary>
    /// Los DTO en el mismo orden de <paramref name="items"/>. <paramref name="services"/> trae el servicio de cada cobro (el nombre
    /// viaja en el DTO); <paramref name="today"/> es la «hoy» del tenant, que decide qué está vencido.
    /// </summary>
    public async Task<IReadOnlyList<ChargeDto>> ComposeAsync(
        Guid tenantId,
        IReadOnlyList<Charge> items,
        IReadOnlyDictionary<ServiceContractId, ServiceContract> services,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var customerIds = items.Select(charge => charge.CustomerId).Distinct().ToArray();
        var customerRefs = (await customers.FindManyAsync(tenantId, customerIds, cancellationToken))
            .ToDictionary(customer => customer.Id);
        var profileByCustomer = (await profiles.ListByCustomersAsync(tenantId, customerIds, cancellationToken))
            .ToDictionary(profile => profile.CustomerId);
        var memberIds = profileByCustomer.Values.Select(profile => profile.AssigneeMemberId)
            .Concat(items.Where(charge => charge.PaidByMemberId.HasValue).Select(charge => charge.PaidByMemberId!.Value))
            .Distinct()
            .ToArray();
        var memberRefs = memberIds.Length == 0
            ? new Dictionary<Guid, ServiceContractsMemberRef>()
            : (await members.FindAsync(tenantId, memberIds, cancellationToken)).ToDictionary(member => member.MemberId);
        var reminders = await chargeRepository.ListLastRemindersAsync(
            items.Select(charge => charge.Id).ToArray(), cancellationToken);

        MemberRefDto MemberOf(Guid memberId) =>
            ServiceContractsMapping.ToMemberRefDto(memberId, memberRefs.GetValueOrDefault(memberId));

        var result = new List<ChargeDto>(items.Count);
        foreach (var charge in items)
        {
            var customer = customerRefs.TryGetValue(charge.CustomerId, out var found)
                ? new CustomerRefDto(found.Id, found.Name, found.PhoneE164, found.Email)
                : new CustomerRefDto(charge.CustomerId, UnknownCustomerName, null, null);
            // Sin perfil no hay encargado: el contrato lo marca nulo, no inventa uno.
            var assignee = profileByCustomer.TryGetValue(charge.CustomerId, out var profile)
                ? MemberOf(profile.AssigneeMemberId)
                : null;
            var paidBy = charge.PaidByMemberId is { } paidById ? MemberOf(paidById) : null;

            result.Add(ServiceContractsMapping.ToChargeDto(
                charge,
                services[charge.ServiceId],
                customer,
                assignee,
                paidBy,
                reminders.GetValueOrDefault(charge.Id),
                today));
        }

        return result;
    }
}
