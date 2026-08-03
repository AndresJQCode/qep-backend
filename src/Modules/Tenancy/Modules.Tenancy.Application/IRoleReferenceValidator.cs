namespace Modules.Tenancy.Application;

/// <summary>
/// Validates that membership role references point to roles known by the
/// Authorization capability. Tenancy owns the relation; Authorization owns the
/// role catalog.
/// </summary>
public interface IRoleReferenceValidator
{
    bool IsKnownRole(string role);
}
