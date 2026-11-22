namespace Modules.Scheduling.Application;

// Todo es anulable para que el validador devuelva el campo y no un 400 de binding.

/// <summary>Los parámetros de <c>GET availability</c>: las fechas son días del calendario del tenant, ambas inclusive.</summary>
public sealed record AvailabilityRequest(Guid? ServiceId, DateOnly? From, DateOnly? To, Guid? ResourceId);

/// <param name="Name">Obligatorio.</param>
/// <param name="Phone">Opcional: sin él no hay recordatorio por WhatsApp, pero la cita se puede agendar.</param>
public sealed record NewCustomerRequest(string? Name, string? Phone);
