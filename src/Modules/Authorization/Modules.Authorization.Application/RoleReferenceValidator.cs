using Modules.Tenancy.Application;

namespace Modules.Authorization.Application;

public sealed class RoleReferenceValidator(IRoleCatalog roleCatalog) : IRoleReferenceValidator
{
    public bool IsKnownRole(string role) => roleCatalog.ContainsRole(role);
}
