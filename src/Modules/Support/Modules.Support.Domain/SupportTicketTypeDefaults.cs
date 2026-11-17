namespace Modules.Support.Domain;

/// <summary>Los ocho tipos que se siembran en el primer acceso de un tenant (spec §7.5), en este orden, todos activos y
/// publicados.</summary>
public static class SupportTicketTypeDefaults
{
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
}
