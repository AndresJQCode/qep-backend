namespace Modules.Support.Domain;

/// <summary>La conversación de Mensajería de un ticket (spec §8.1): su id (<c>messaging.conversations.id</c>, nunca el teléfono) y
/// su canal, por nombre (<c>WhatsApp</c> o <c>Instagram</c>). Support no ve el enum de Mensajería: el adaptador lo traduce.</summary>
public sealed record SupportConversationRef(Guid Id, string Channel)
{
    public const string WhatsApp = "WhatsApp";

    public const string Instagram = "Instagram";

    public const int ChannelMaxLength = 16;

    public static IReadOnlyList<string> Channels { get; } = [WhatsApp, Instagram];

    internal void EnsureValid()
    {
        if (Id == Guid.Empty)
        {
            throw new ArgumentException("The conversation id is required.", nameof(Id));
        }

        if (!Channels.Contains(Channel, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown conversation channel '{Channel}'.", nameof(Channel));
        }
    }
}
