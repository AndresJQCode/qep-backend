using Modules.Messaging.Application;
using Modules.Support.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Fase 4b, Tarea 12 (H18, V-1): Meta devuelve el toque de un botón con su texto, y Soporte lo casa con
/// <see cref="SupportSurveyRatings.ButtonLabels"/>, una copia de los botones de la plantilla de Mensajería (Soporte no ve Mensajería). Este
/// proyecto ve los dos módulos: si cambian los botones de un lado, los toques dejan de calificar sin ningún error.
/// </summary>
public sealed class SupportSurveyParityTests
{
    [Fact]
    public void TheButtonLabelsMatchTheMessagingTemplate()
    {
        Assert.Equal(SupportSurveyTemplateDefinition.ButtonLabels, SupportSurveyRatings.ButtonLabels.Select(item => item.Label));
        Assert.Equal(
            [SupportSurveyRating.Good, SupportSurveyRating.Regular, SupportSurveyRating.Bad],
            SupportSurveyTemplateDefinition.ButtonLabels.Select(label => SupportSurveyRatings.FromButtonText(label)!.Value));
    }
}
