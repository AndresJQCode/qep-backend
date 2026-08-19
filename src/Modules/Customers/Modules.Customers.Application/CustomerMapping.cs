using Modules.Customers.Domain;

namespace Modules.Customers.Application;

internal static class CustomerMapping
{
    public static CustomerDto ToDto(this Customer customer) => new(
        customer.Id.Value,
        customer.Cuc,
        customer.Name,
        customer.IdentificationType.ToWireValue(),
        customer.IdentificationNumber,
        customer.Phone,
        customer.Email,
        customer.Address,
        customer.Department,
        customer.City,
        customer.Classification?.ToWireValue(),
        customer.PriceListId,
        customer.WithRetention,
        customer.IsActive,
        customer.CreatedAt,
        customer.UpdatedAt);

    /// <summary>
    /// Del contrato HTTP al dominio. Los <c>Parse</c> son los que rechazan un tipo de documento o
    /// una clasificacion desconocidos; no se traducen a un valor por defecto, porque elegir NIT en
    /// silencio le cambia el documento al cliente sin que nadie se entere.
    /// </summary>
    public static CustomerIdentification ToIdentification(
        string identificationType,
        string identificationNumber) =>
        new()
        {
            Type = IdentificationTypeParser.Parse(identificationType),
            Number = identificationNumber
        };

    public static CustomerCommercialInfo ToCommercialInfo(
        string? classification,
        Guid? priceListId,
        bool withRetention) =>
        new()
        {
            Classification = CustomerClassificationParser.Parse(classification),
            PriceListId = priceListId,
            WithRetention = withRetention
        };
}
