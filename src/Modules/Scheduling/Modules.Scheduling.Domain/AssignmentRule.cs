namespace Modules.Scheduling.Domain;

/// <summary>A quién se asigna la cita cuando el cliente no elige con quién. Hoy sólo el primero libre, en el orden de los recursos de la franja.</summary>
public enum AssignmentRule
{
    FirstFree,
}
