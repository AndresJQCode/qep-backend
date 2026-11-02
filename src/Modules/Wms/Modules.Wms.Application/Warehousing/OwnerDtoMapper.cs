using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

internal static class OwnerDtoMapper
{
    public static OwnerDto ToDto(Owner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return new OwnerDto(
            owner.Id.Value,
            owner.Kind.ToString(),
            owner.Name,
            owner.Identification,
            owner.ContactEmail,
            owner.IsActive,
            owner.Version);
    }
}
