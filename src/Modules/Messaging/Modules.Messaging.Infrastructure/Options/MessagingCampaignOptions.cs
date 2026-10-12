using Microsoft.Extensions.Options;

namespace Modules.Messaging.Infrastructure.Options;

/// <summary>Spec 2026-10-11 decisión 14 y §10: <c>Messaging:Campaigns:MessagesPerSecond</c>, opcional. 20 por defecto: un cuarto del mínimo de
/// Meta (80 mps por número), para dejar aire a la bandeja en el mismo número.</summary>
public sealed class MessagingCampaignOptions
{
    public const string SectionName = "Messaging:Campaigns";

    /// <summary>El mínimo que Meta documenta por número.</summary>
    public const int MetaMinimumPerSecond = 80;

    public int MessagesPerSecond { get; set; } = 20;
}

/// <summary>Un cero dejaría el cubo sin tokens para siempre y uno por encima de lo que Meta da por número provocaría 130429 propios.</summary>
internal sealed class MessagingCampaignOptionsValidator : IValidateOptions<MessagingCampaignOptions>
{
    public ValidateOptionsResult Validate(string? name, MessagingCampaignOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.MessagesPerSecond is >= 1 and <= MessagingCampaignOptions.MetaMinimumPerSecond
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{MessagingCampaignOptions.SectionName}:MessagesPerSecond must be between 1 and {MessagingCampaignOptions.MetaMinimumPerSecond}.");
    }
}
