using System.Globalization;
using System.Security.Cryptography;

namespace Modules.Support.Domain;

/// <summary>
/// La clave de consulta del ticket (spec §9.4): seis dígitos de un aleatorio criptográfico (precedente
/// <c>CompleteWhatsAppSignup</c>). Se muestra o se envía una vez y se guarda sólo su hash
/// (<see cref="SupportTicketAccess.PinHash"/>): nunca va a logs, auditoría ni a la traza del ticket.
/// </summary>
public static class SupportPin
{
    public const int Length = 6;

    public static string Generate() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
}
