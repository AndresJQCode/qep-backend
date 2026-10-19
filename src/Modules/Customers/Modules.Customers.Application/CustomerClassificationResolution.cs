using Modules.Customers.Domain;

namespace Modules.Customers.Application;

internal static class CustomerClassificationResolution
{
    /// <summary>
    /// La clasificacion que pide el cuerpo de un alta o de un PUT, ya resuelta: hace falta su prefijo
    /// para el CUC. <c>null</c> si el cuerpo no trae ninguna, que es un cliente sin clasificacion
    /// (decision del owner, 2026-10-19); una que no existe en el tenant es un 422 con codigo de
    /// dominio, no un cliente sin clasificacion.
    ///
    /// Un solo lugar porque la consumen el alta, la edicion, completar un incompleto y el alta por
    /// integracion, y las cuatro tienen que decir lo mismo.
    /// </summary>
    public static async Task<ClientClassification?> ResolveForCustomerAsync(
        this IClientClassificationRepository classificationRepository,
        Guid tenantId,
        Guid? classificationId,
        CancellationToken cancellationToken)
    {
        if (classificationId is not { } id)
        {
            return null;
        }

        return await classificationRepository.FindAsync(
            tenantId, new ClientClassificationId(id), cancellationToken)
            ?? throw new CustomersDomainException(
                "customers.customer.classification_not_found",
                "The client classification was not found in this tenant.");
    }
}
