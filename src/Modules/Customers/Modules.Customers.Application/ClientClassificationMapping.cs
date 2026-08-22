using Modules.Customers.Domain;

namespace Modules.Customers.Application;

internal static class ClientClassificationMapping
{
    public static ClientClassificationDto ToDto(this ClientClassification classification) => new(
        classification.Id.Value,
        classification.Name,
        classification.Prefix,
        classification.IsActive,
        classification.CreatedAt,
        classification.UpdatedAt);
}
