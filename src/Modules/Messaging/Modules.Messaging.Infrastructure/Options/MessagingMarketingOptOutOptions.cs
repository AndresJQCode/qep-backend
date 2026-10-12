namespace Modules.Messaging.Infrastructure.Options;

/// <summary>Decisión del owner 2026-10-12 (P1): <c>Messaging:MarketingOptOut:ButtonTexts</c>, opcional. Los textos (o payloads) del botón de
/// respuesta rápida de una plantilla que apagan el marketing del cliente. Sin configurar valen <see cref="DefaultButtonTexts"/>; si se
/// configura, la lista reemplaza a la de fábrica (no se suma: un arreglo del binder se mezclaría con el default).</summary>
public sealed class MessagingMarketingOptOutOptions
{
    public const string SectionName = "Messaging:MarketingOptOut";

    public static readonly IReadOnlyList<string> DefaultButtonTexts = ["Detener promociones", "Stop promotions"];

    public string[]? ButtonTexts { get; set; }
}
