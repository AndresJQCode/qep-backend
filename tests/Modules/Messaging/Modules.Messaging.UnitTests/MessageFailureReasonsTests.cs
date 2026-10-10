using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §10.3: la tabla literal; lo desconocido es el texto genérico; sin código no hay motivo.</summary>
public sealed class MessageFailureReasonsTests
{
    [Theory]
    [InlineData(131047, "Pasaron más de 24 horas desde el último mensaje de la persona: WhatsApp sólo acepta plantillas aprobadas.")]
    [InlineData(131026, "WhatsApp no pudo entregar el mensaje: puede que el número no use WhatsApp o tenga una versión desactualizada.")]
    [InlineData(131051, "WhatsApp no admite este tipo de mensaje.")]
    [InlineData(131049, "WhatsApp no entregó el mensaje para cuidar la experiencia de la persona. Espera al menos 24 horas antes de volver a intentarlo.")]
    [InlineData(131050, "La persona dejó de recibir mensajes de marketing de tu organización.")]
    [InlineData(131021, "No puedes enviarle un mensaje al mismo número que lo envía.")]
    [InlineData(131031, "La cuenta de WhatsApp Business está restringida o no pasó una verificación.")]
    [InlineData(131042, "Hay un problema con el método de pago de la cuenta de WhatsApp Business.")]
    [InlineData(131056, "Enviaste demasiados mensajes a esta persona en poco tiempo. Espera un momento y vuelve a intentarlo.")]
    [InlineData(133010, "El número de tu organización no está registrado en WhatsApp Business.")]
    [InlineData(368, "La cuenta de WhatsApp Business está restringida o deshabilitada por incumplir políticas.")]
    [InlineData(131005, "QEP perdió los permisos sobre tu cuenta de WhatsApp. Vuelve a conectar el número.")]
    [InlineData(131016, "WhatsApp no está disponible en este momento. Intenta de nuevo en unos minutos.")]
    [InlineData(131000, "WhatsApp no pudo enviar el mensaje por un error desconocido. Intenta de nuevo.")]
    [InlineData(-1, "No pudimos confirmar el envío con WhatsApp. Intenta de nuevo.")]
    [InlineData(999999, "WhatsApp no pudo entregar el mensaje.")]
    public void EveryKnownCodeHasItsText(int code, string expected) =>
        Assert.Equal(expected, MessageFailureReasons.For(code));

    [Fact]
    public void WithoutACodeThereIsNoReason() => Assert.Null(MessageFailureReasons.For(null));
}
