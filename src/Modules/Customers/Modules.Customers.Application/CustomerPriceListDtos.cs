namespace Modules.Customers.Application;

public sealed record CustomerPriceListDto(Guid Id, string Name, string Prefix, bool IsActive);

public sealed record CustomerPriceListsResponse(IReadOnlyCollection<CustomerPriceListDto> Items);

public sealed record SetCustomerPriceListsRequest(IReadOnlyCollection<Guid> PriceListIds);
