namespace Modules.Messaging.Infrastructure.Options;

/// <summary>Spec 2026-10-09 §7.3: el <c>statement_timeout</c> de la búsqueda. En producción queda en su
/// default de 2000 ms; la clave existe para que una prueba pueda provocar el timeout.</summary>
public sealed class MessagingSearchOptions
{
    public const string SectionName = "Messaging:Search";

    public int StatementTimeoutMs { get; set; } = 2000;
}
