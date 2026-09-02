using Modules.Customers.Domain;

namespace Modules.Customers.Application;

internal static class CustomerMapping
{
    /// <summary>
    /// Del agregado al DTO, con la ciudad, el departamento y la clasificacion ya resueltos por el
    /// llamador. No los resuelve esta funcion: cada handler decide como (una consulta puntual en
    /// Get/Create/Update/Activate/Deactivate, un lote en List) y esto solo ensambla.
    /// </summary>
    public static CustomerDto ToDto(
        this Customer customer, CustomerCityRef city, ClientClassification classification) => new(
        customer.Id.Value,
        customer.Cuc,
        customer.Name,
        customer.IdentificationType.ToWireValue(),
        customer.IdentificationNumber,
        customer.Phone,
        customer.Email,
        customer.Address,
        new CustomerCityDto(city.CityId, city.CityDivipolaCode, city.CityName),
        new CustomerDepartmentDto(
            city.DepartmentId, city.DepartmentDivipolaCode, city.DepartmentName),
        classification.ToDto(),
        customer.WithRetention,
        customer.VatSurplus,
        customer.IsActive,
        customer.CreatedAt,
        customer.UpdatedAt);

    /// <summary>
    /// La version de un solo cliente: resuelve su ciudad y su clasificacion y arma el DTO. Para
    /// <c>GetCustomerHandler</c>, <c>DeactivateCustomerHandler</c> y <c>ActivateCustomerHandler</c>,
    /// que ya tienen el <c>Customer</c> en mano y no necesitan resolver nada mas antes.
    ///
    /// La FK de base garantiza que las dos referencias existan, asi que un miss aca es corrupcion
    /// de datos y no una entrada de usuario invalida — por eso lanza <see cref="InvalidOperationException"/>
    /// (500) y no un <see cref="CustomersDomainException"/> (422): no hay ningun campo del request
    /// que el llamador pueda corregir.
    /// </summary>
    public static async Task<CustomerDto> ToDtoAsync(
        this Customer customer,
        ICustomerGeographyLookup geographyLookup,
        IClientClassificationRepository classificationRepository,
        CancellationToken cancellationToken)
    {
        var city = await geographyLookup.FindCityAsync(customer.CityId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"City '{customer.CityId}' referenced by customer '{customer.Id}' " +
                "was not found.");
        var classification = await classificationRepository.FindAsync(
            customer.TenantId, customer.ClassificationId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Classification '{customer.ClassificationId}' referenced by customer " +
                $"'{customer.Id}' was not found.");

        return customer.ToDto(city, classification);
    }

    /// <summary>
    /// Del contrato HTTP al dominio. <c>IdentificationTypeParser.Parse</c> es el que rechaza un
    /// tipo de documento desconocido; no se traduce a un valor por defecto, porque elegir NIT en
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
        Guid classificationId,
        bool withRetention,
        bool vatSurplus) =>
        new()
        {
            ClassificationId = new ClientClassificationId(classificationId),
            WithRetention = withRetention,
            VatSurplus = vatSurplus
        };
}
