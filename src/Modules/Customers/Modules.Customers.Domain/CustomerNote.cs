namespace Modules.Customers.Domain;

public readonly record struct CustomerNoteId(Guid Value)
{
    public static CustomerNoteId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Spec 2026-10-11 §4.1: un comentario interno sobre un cliente, que sólo ve el equipo. Es su propio agregado y
/// no una colección dentro de <see cref="Customer"/>: así una nota no le sube la <c>Version</c> al cliente, y
/// quien edita la ficha no recibe un 412 por una nota que escribió otro.
///
/// Quién puede editarla o borrarla (el autor, o un moderador) es política del caso de uso, no invariante de
/// esta clase; acá sólo vive la pregunta <see cref="IsAuthoredBy"/>.
/// </summary>
public sealed class CustomerNote
{
    // Espeja el ancho de customers.customer_notes.body: un cuerpo largo falla como 422 con código de dominio y
    // no como 500 de PostgreSQL.
    public const int BodyMaxLength = 2000;

    // EF Core materializa por acá; Create es el único punto de entrada.
    private CustomerNote()
    {
        Body = string.Empty;
    }

    private CustomerNote(
        CustomerNoteId id, Guid tenantId, CustomerId customerId, string body, Guid authorUserId, DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        CustomerId = customerId;
        Body = body;
        AuthorUserId = authorUserId;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public CustomerNoteId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public CustomerId CustomerId { get; private set; }

    public string Body { get; private set; }

    /// <summary>El usuario (no la membresía) que la escribió. Si el usuario deja de existir la nota se conserva y
    /// la pantalla lo muestra como «Usuario eliminado».</summary>
    public Guid AuthorUserId { get; private set; }

    /// <summary>Token de concurrencia optimista propio de la nota; el cliente lo manda como <c>expectedVersion</c>.</summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static CustomerNote Create(
        CustomerNoteId id, Guid tenantId, CustomerId customerId, string body, Guid authorUserId, DateTimeOffset occurredAt) =>
        new(id, tenantId, customerId, NormalizeBody(body), authorUserId, occurredAt);

    public void Edit(string body, DateTimeOffset occurredAt)
    {
        Body = NormalizeBody(body);
        Version++;
        UpdatedAt = occurredAt;
    }

    public bool IsAuthoredBy(Guid userId) => AuthorUserId == userId;

    private static string NormalizeBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new CustomersDomainException("customers.note.body_required", "The note body is required.");
        }

        var trimmed = body.Trim();
        return trimmed.Length > BodyMaxLength
            ? throw new CustomersDomainException(
                "customers.note.body_too_long", $"The note body cannot exceed {BodyMaxLength} characters.")
            : trimmed;
    }
}
