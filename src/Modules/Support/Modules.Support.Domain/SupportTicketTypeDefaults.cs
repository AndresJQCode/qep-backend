namespace Modules.Support.Domain;

/// <summary>Los ocho tipos que se siembran en el primer acceso de un tenant (spec §7.5), en este orden, todos activos y
/// publicados.</summary>
public static class SupportTicketTypeDefaults
{
    /// <summary>Spec 2026-11-22: el nombre del tipo de las tareas internas, que no es uno de los ocho.</summary>
    public const string InternalTaskName = "Tarea interna";

    private static readonly (string Name, int? Term, bool Legal, bool Formal)[] Seed =
    [
        ("Petición general", 15, true, true),
        ("Petición de documentos o información", 10, true, true),
        ("Consulta", 30, true, true),
        ("Queja", 15, true, true),
        ("Reclamo", 15, true, true),
        ("Sugerencia", 15, true, false),
        ("Felicitación", 15, true, false),
        ("Soporte técnico", null, false, false),
    ];

    public static IReadOnlyList<SupportTicketType> For(Guid tenantId, DateTimeOffset now) =>
        Seed
            .Select((seed, index) => SupportTicketType.Create(
                SupportTicketTypeId.New(), tenantId, seed.Name, seed.Term, seed.Legal, seed.Formal, isPublished: true, index + 1, now))
            .ToArray();

    /// <summary>El noveno tipo, el de las tareas internas: <see cref="For"/> no lo incluye, así que los ocho externos conservan sus
    /// posiciones.</summary>
    public static SupportTicketType Internal(Guid tenantId, int position, DateTimeOffset now) =>
        SupportTicketType.CreateInternal(SupportTicketTypeId.New(), tenantId, position, now);
}
