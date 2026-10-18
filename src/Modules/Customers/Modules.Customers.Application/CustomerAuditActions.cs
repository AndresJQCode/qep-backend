namespace Modules.Customers.Application;

/// <summary>Spec 2026-10-10 §6.2 (D-A9): <see cref="ICustomersAuditPublisher.Publish"/> no tiene metadatos, así que
/// el origen va en la acción. Las acciones viejas siguen como literales en sus handlers.</summary>
public static class CustomerAuditActions
{
    /// <summary>La misma acción que el alta por HTTP; el origen lo dice el tipo de actor (<c>Integration</c>).</summary>
    public const string Created = "customers.customer.created";
    public const string CreatedFromMessaging = "customers.customer.created_from_messaging";
    public const string Completed = "customers.customer.completed";

    // Spec 2026-10-11 §4.2. El recurso auditado es la nota o la etiqueta; en los dos últimos, el cliente.
    public const string NoteCreated = "customers.note.created";
    public const string NoteUpdated = "customers.note.updated";
    public const string NoteDeleted = "customers.note.deleted";
    public const string TagCreated = "customers.tag.created";
    public const string TagUpdated = "customers.tag.updated";
    public const string TagDeleted = "customers.tag.deleted";
    public const string TagsChanged = "customers.customer.tags_changed";
    public const string MarketingConsentChanged = "customers.customer.marketing_consent_changed";
}
