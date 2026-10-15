namespace Modules.Customers.Application;

/// <summary>
/// Spec 2026-10-14 decisión 10: otro proceso puso el mismo IGSID primero (<c>IX_customers_tenant_instagram_user_id</c>). La lanza
/// <c>CustomersUnitOfWork</c> y la atrapa <see cref="CustomerInstagramDirectory"/>, que relee. Nunca llega a HTTP.
/// </summary>
public sealed class InstagramUserIdTakenException : Exception
{
    public InstagramUserIdTakenException()
        : base("Another customer of this tenant already has that Instagram user id.")
    {
    }

    public InstagramUserIdTakenException(Exception innerException)
        : base("Another customer of this tenant already has that Instagram user id.", innerException)
    {
    }

    public InstagramUserIdTakenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public InstagramUserIdTakenException(string message)
        : base(message)
    {
    }
}
