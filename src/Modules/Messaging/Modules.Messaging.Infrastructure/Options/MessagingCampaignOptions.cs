using Microsoft.Extensions.Options;

namespace Modules.Messaging.Infrastructure.Options;

/// <summary>Spec 2026-10-11 decisión 14 y §10, con la decisión del owner 2026-10-12 (P3): <c>Messaging:Campaigns:MessagesPerSecond</c> (20 por
/// defecto) es el ritmo inicial y objetivo por conexión —un cuarto del throughput por defecto de Meta, para dejar aire a la bandeja del mismo
/// número—; <c>MaxMessagesPerSecond</c> (80, el throughput por defecto por número) es el techo. El ritmo real es adaptativo: baja con cada
/// 130429 y sube de vuelta hasta el objetivo (<see cref="Campaigns.ConnectionSendBudget"/>).</summary>
public sealed class MessagingCampaignOptions
{
    public const string SectionName = "Messaging:Campaigns";

    /// <summary>El throughput por defecto que Meta da por número.</summary>
    public const int MetaDefaultPerSecond = 80;

    /// <summary>El tope que se acepta configurar: Meta sube el throughput hasta 1.000 mps a números con calidad y volumen altos.</summary>
    public const int AbsoluteCeilingPerSecond = 1000;

    public int MessagesPerSecond { get; set; } = 20;

    public int MaxMessagesPerSecond { get; set; } = MetaDefaultPerSecond;
}

/// <summary>Un cero dejaría el cubo sin tokens para siempre; un objetivo por encima del techo no se puede cumplir, y un techo por encima de lo
/// que Meta da como máximo provocaría 130429 propios. Vale 1 ≤ objetivo ≤ techo ≤ 1000.</summary>
internal sealed class MessagingCampaignOptionsValidator : IValidateOptions<MessagingCampaignOptions>
{
    public ValidateOptionsResult Validate(string? name, MessagingCampaignOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.MessagesPerSecond >= 1
            && options.MessagesPerSecond <= options.MaxMessagesPerSecond
            && options.MaxMessagesPerSecond <= MessagingCampaignOptions.AbsoluteCeilingPerSecond
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{MessagingCampaignOptions.SectionName}: 1 <= MessagesPerSecond <= MaxMessagesPerSecond <= {MessagingCampaignOptions.AbsoluteCeilingPerSecond} is required.");
    }
}
