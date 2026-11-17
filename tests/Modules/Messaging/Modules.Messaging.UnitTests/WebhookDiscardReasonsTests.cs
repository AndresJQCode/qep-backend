using Modules.Messaging.Infrastructure.Webhook;

namespace Modules.Messaging.UnitTests;

/// <summary>Los motivos de <c>qep_webhook_discarded_total</c> son contrato: la alerta <c>QepWebhookDiscarding</c> de
/// <c>migracion-k8s</c> los lee por nombre. Renombrar uno compila y deja la alerta muda; esta prueba lo pone en rojo.</summary>
public sealed class WebhookDiscardReasonsTests
{
    [Fact]
    public void TheReasonsAreTheContractTheAlertsRead() =>
        Assert.Equal(
            ["unknown_route", "without_user_id", "paused", "module_off", "foreign_status", "unhandled_field", "foreign_event", "ignored"],
            WebhookDiscardReasons.All);

    [Fact]
    public void TheProvidersAreTheContractTheAlertsRead()
    {
        Assert.Equal("whatsapp", WebhookDiscardProviders.WhatsApp);
        Assert.Equal("instagram", WebhookDiscardProviders.Instagram);
    }
}
