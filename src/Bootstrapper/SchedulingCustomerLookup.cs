using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Scheduling.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Customers → Scheduling (plan 2026-11-21, corrección 5). Calco de <see cref="PosCustomerLookup"/>: la Agenda no referencia Clientes, así
/// que este adaptador traduce el agregado a nombre y teléfono y deja que Clientes decida las reglas del alta. Toda lectura va acotada al
/// tenant: un cliente de otro tenant no aparece ni se puede agendar.
/// </summary>
internal sealed class SchedulingCustomerLookup(
    ICustomerRepository customers,
    ICustomerProvisioning provisioning,
    IExecutionContext executionContext)
    : ISchedulingCustomerLookup
{
    // La bitácora de Clientes distingue el origen del alta; esta es la de la Agenda.
    private const string Source = "scheduling";

    public async Task<IReadOnlyList<SchedulingCustomerRef>> SearchAsync(
        Guid tenantId, string? search, int take, CancellationToken cancellationToken)
    {
        // Con los incompletos sin documento: quien llama por teléfono casi nunca tiene ficha completa, y la cita no necesita documento.
        var found = await customers.SearchSelectableAsync(tenantId, search, take, includeIncompleteWithoutDocument: true, cancellationToken);
        return [.. found.Select(ToRef)];
    }

    public async Task<SchedulingCustomerRef?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customers.FindAsync(tenantId, new CustomerId(customerId), cancellationToken);
        return customer is null ? null : ToRef(customer);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        if (customerIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var names = await customers.FindNamesByIdsAsync(tenantId, [.. customerIds.Select(id => new CustomerId(id))], cancellationToken);
        return names.ToDictionary(pair => pair.Key.Value, pair => pair.Value);
    }

    public async Task<SchedulingCustomerRef> CreateAsync(
        Guid tenantId, NewSchedulingCustomer input, CancellationToken cancellationToken)
    {
        var provisioned = await provisioning.CreateFromRegisterAsync(
            tenantId,
            executionContext.SubjectId,
            new RegisterCustomerData(input.Name, IdentificationType: null, IdentificationNumber: null, input.Phone),
            Source,
            cancellationToken);

        // Se relee en vez de armar el ref con lo tecleado: el agregado ya normalizó el nombre y el teléfono.
        var customer = await customers.FindAsync(tenantId, new CustomerId(provisioned.Id), cancellationToken)
            ?? throw new InvalidOperationException("The customer just provisioned for the agenda could not be read back.");
        return ToRef(customer);
    }

    private static SchedulingCustomerRef ToRef(Customer customer) => new(customer.Id.Value, customer.Name, customer.Phone, customer.IsActive);
}
