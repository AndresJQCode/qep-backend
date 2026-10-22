using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Pos.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Customers → Pos (spec 2026-10-14 «cliente identificado»). Traduce el agregado a los tipos de la caja, con el tipo de
/// documento como cadena del contrato, y deja que Customers decida las reglas del alta.
/// </summary>
internal sealed class PosCustomerLookup(
    ICustomerRepository customers,
    ICustomerProvisioning provisioning,
    IExecutionContext executionContext)
    : IPosCustomerLookup
{
    // La bitácora de Customers distingue el origen del alta; la caja se identifica como «pos».
    private const string Source = "pos";

    public async Task<IReadOnlyList<PosCustomerRef>> SearchAsync(
        Guid tenantId, string? search, int take, CancellationToken cancellationToken)
    {
        var found = await customers.SearchSelectableAsync(tenantId, search, take, cancellationToken);
        return found.Select(ToRef).ToList();
    }

    public async Task<PosCustomerSnapshot?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customers.FindAsync(tenantId, new CustomerId(customerId), cancellationToken);
        return customer is null
            ? null
            : new PosCustomerSnapshot(
                customer.Id.Value,
                customer.Name,
                customer.IdentificationType?.ToWireValue(),
                customer.IdentificationNumber,
                customer.IsActive);
    }

    public async Task<PosCustomerRef?> FindByIdentificationAsync(
        Guid tenantId, string type, string number, CancellationToken cancellationToken)
    {
        var parsed = IdentificationTypeParser.Parse(type);
        var key = (parsed, number.Trim());
        var owners = await customers.FindExistingIdentificationsAsync(tenantId, [key], cancellationToken);
        if (!owners.TryGetValue(key, out var ownerId))
        {
            return null;
        }

        // Sin filtrar por activo: si el dueño del documento está inactivo, la caja tiene que saberlo en vez de
        // intentar crear otra ficha con el mismo documento.
        var owner = await customers.FindAsync(tenantId, ownerId, cancellationToken);
        return owner is null ? null : ToRef(owner);
    }

    public async Task<PosCustomerRef> CreateFromRegisterAsync(
        Guid tenantId, NewPosCustomer input, CancellationToken cancellationToken)
    {
        var provisioned = await provisioning.CreateFromRegisterAsync(
            tenantId,
            executionContext.SubjectId,
            new RegisterCustomerData(input.Name, input.IdentificationType, input.IdentificationNumber, input.Phone),
            Source,
            cancellationToken);

        // Se relee en vez de armar el ref con lo tecleado: el agregado ya normalizó el nombre y el número.
        var customer = await customers.FindAsync(tenantId, new CustomerId(provisioned.Id), cancellationToken)
            ?? throw new InvalidOperationException("The customer just provisioned for the register could not be read back.");
        return ToRef(customer);
    }

    // Todo cliente que llega acá tiene documento: la búsqueda del selector lo exige y FindByIdentification parte de él.
    private static PosCustomerRef ToRef(Customer customer) => new(
        customer.Id.Value,
        customer.Name,
        customer.IdentificationType!.Value.ToWireValue(),
        customer.IdentificationNumber!,
        customer.IsComplete,
        customer.IsActive);
}
