using Microsoft.Extensions.Options;
using Modules.Shopify.Application;

namespace Modules.Shopify.Infrastructure;

/// <summary>Spec 2026-10-15 §5.1: un cero o un negativo dejaría el webhook respondiendo 413 a todo y a Shopify reintentando 48
/// horas hasta retirar la suscripción. Se valida al arrancar, como <c>MessagingWebhookOptionsValidator</c>.</summary>
internal sealed class ShopifyWebhookOptionsValidator : IValidateOptions<ShopifyWebhookOptions>
{
    public ValidateOptionsResult Validate(string? name, ShopifyWebhookOptions options)
    {
        var failures = new List<string>();
        if (options.MaxBodyBytes <= 0)
        {
            failures.Add($"{ShopifyWebhookOptions.SectionName}:MaxBodyBytes must be greater than zero.");
        }

        if (options.DeliveryPollSeconds <= 0)
        {
            failures.Add($"{ShopifyWebhookOptions.SectionName}:DeliveryPollSeconds must be greater than zero.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
