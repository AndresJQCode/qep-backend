namespace Modules.Customers.Application;

/// <summary>
/// Spec 2026-10-10 §8.2 y §9.4: otro proceso puso el mismo BSUID primero (<c>IX_customers_tenant_whatsapp_user_id</c>).
/// La lanza <c>CustomersUnitOfWork</c> (P3) y la atrapa <see cref="CustomerWhatsAppDirectory"/>, que relee. Nunca
/// llega a HTTP: ningún endpoint escribe el BSUID.
/// </summary>
public sealed class WhatsAppUserIdTakenException : Exception
{
    public WhatsAppUserIdTakenException()
        : base("Another customer of this tenant already has that WhatsApp user id.")
    {
    }

    public WhatsAppUserIdTakenException(Exception innerException)
        : base("Another customer of this tenant already has that WhatsApp user id.", innerException)
    {
    }

    public WhatsAppUserIdTakenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WhatsAppUserIdTakenException(string message)
        : base(message)
    {
    }
}
