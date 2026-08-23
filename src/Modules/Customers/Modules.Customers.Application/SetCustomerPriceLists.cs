using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

/// <summary>
/// Reemplaza el conjunto entero de listas de precio asignadas a un cliente — mismo criterio de
/// "PUT reemplaza el recurso completo" que <c>UpdateProduct</c> con sus escalas. Una lista que no
/// viene en <see cref="PriceListIds"/> deja de estar asignada; repetir un id en el body no crea
/// una segunda fila, el conjunto es lo que importa.
/// </summary>
public sealed record SetCustomerPriceListsCommand(
    Guid TenantId, Guid CustomerId, IReadOnlyCollection<Guid> PriceListIds)
    : ICommand<IReadOnlyList<CustomerPriceListDto>>;

// Sin validador FluentValidation: el comando no lleva texto libre, sólo una colección de ids. Que
// cada id exista, sea del tenant y esté activo lo resuelve CustomerPriceListResolver, con códigos
// de dominio propios — no hay un campo al que apuntar un mapa de errores cuando el problema es
// "este id no existe", mismo criterio que ProductPriceListResolver en Catalog.
public sealed class SetCustomerPriceListsHandler(
    ICustomerRepository customerRepository,
    ICustomerPriceListRepository priceListRepository,
    ICustomerPriceListLookup priceListLookup,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<SetCustomerPriceListsCommand, IReadOnlyList<CustomerPriceListDto>>
{
    public async Task<IReadOnlyList<CustomerPriceListDto>> HandleAsync(
        SetCustomerPriceListsCommand command,
        CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerManage);

        var customerId = new CustomerId(command.CustomerId);
        var customer = await customerRepository.FindAsync(
            command.TenantId, customerId, cancellationToken)
            ?? throw CustomerNotFound.For(command.CustomerId);

        var desiredIds = command.PriceListIds.Distinct().ToArray();
        var priceLists = await CustomerPriceListResolver.ResolveAsync(
            priceListLookup, command.TenantId, desiredIds, cancellationToken);

        var current = await priceListRepository.ListAsync(
            command.TenantId, customer.Id, cancellationToken);
        var currentIds = current.Select(assignment => assignment.PriceListId).ToHashSet();
        var desiredIdSet = desiredIds.ToHashSet();

        var now = clock.UtcNow;
        foreach (var assignment in current.Where(
            assignment => !desiredIdSet.Contains(assignment.PriceListId)))
        {
            priceListRepository.Remove(assignment);
        }

        foreach (var priceListId in desiredIdSet.Except(currentIds))
        {
            priceListRepository.Add(CustomerPriceList.Create(
                CustomerPriceListId.New(), command.TenantId, customer.Id, priceListId, now));
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.customer.price_lists_updated",
            customer.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return desiredIds
            .Select(priceListId =>
            {
                var priceList = priceLists[priceListId];
                return new CustomerPriceListDto(
                    priceList.Id, priceList.Name, priceList.Prefix, priceList.IsActive);
            })
            .ToArray();
    }
}
