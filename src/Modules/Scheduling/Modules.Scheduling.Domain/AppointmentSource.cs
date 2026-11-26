namespace Modules.Scheduling.Domain;

/// <summary>Quién la creó: una persona del equipo desde el back office, o el cliente por un canal (el agente de WhatsApp).</summary>
public enum AppointmentSource
{
    BackOffice,
    Channel,
}
