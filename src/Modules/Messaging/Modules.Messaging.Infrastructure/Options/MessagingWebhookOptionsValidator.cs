using Microsoft.Extensions.Options;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Options;

/// <summary>Spec 2026-10-09 §9: un cero o un negativo dejaría el webhook rechazando todo (413 con el tope,
/// 429 con la concurrencia o la cola) y a Meta reintentando por días. Se valida al arrancar.</summary>
internal sealed class MessagingWebhookOptionsValidator : IValidateOptions<MessagingWebhookOptions>
{
    public ValidateOptionsResult Validate(string? name, MessagingWebhookOptions options)
    {
        var failures = new List<string>();
        if (options.MaxBodyBytes <= 0)
        {
            failures.Add($"{MessagingWebhookOptions.SectionName}:MaxBodyBytes must be greater than zero.");
        }

        if (options.ConcurrencyLimit <= 0)
        {
            failures.Add($"{MessagingWebhookOptions.SectionName}:ConcurrencyLimit must be greater than zero.");
        }

        if (options.QueueLimit <= 0)
        {
            failures.Add($"{MessagingWebhookOptions.SectionName}:QueueLimit must be greater than zero.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
