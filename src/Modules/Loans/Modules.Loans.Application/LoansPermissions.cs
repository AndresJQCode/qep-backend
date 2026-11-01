namespace Modules.Loans.Application;

// Tres segmentos, module.resource.action, como los demás permisos. Dos y no más: leer incluye las listas, el detalle y los
// encargados; gestionar incluye crear, editar, eliminar, marcar cuotas como pagadas y registrar abonos a capital.
public static class LoansPermissions
{
    public const string Read = "loans.loan.read";

    public const string Manage = "loans.loan.manage";
}