namespace Modules.Support.Domain;

/// <summary>Por dónde se le responde al solicitante (plan D8, spec §8.3).</summary>
public enum SupportReplyChannel
{
    Thread,
    WhatsAppTemplate,
    Email,
}
