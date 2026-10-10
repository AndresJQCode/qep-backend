namespace Modules.Customers.Application;

/// <summary>Spec 2026-10-10 §6.2 (D-A9): <see cref="ICustomersAuditPublisher.Publish"/> no tiene metadatos, así que
/// el origen va en la acción. Las acciones viejas siguen como literales en sus handlers.</summary>
public static class CustomerAuditActions
{
    public const string CreatedFromMessaging = "customers.customer.created_from_messaging";
    public const string Completed = "customers.customer.completed";
}
