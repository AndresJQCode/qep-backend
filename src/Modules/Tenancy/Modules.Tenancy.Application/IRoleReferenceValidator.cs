namespace Modules.Tenancy.Application;

/// <summary>
/// Valida que las referencias de rol de una membresía apunten a roles conocidos por la
/// capacidad Authorization. Tenancy es dueño de la relación; Authorization es dueño del
/// catálogo de roles.
/// </summary>
public interface IRoleReferenceValidator
{
    bool IsKnownRole(string role);
}
