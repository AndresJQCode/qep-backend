namespace Modules.Hr.Domain;

/// <summary>Una caja de compensación familiar. El departamento y el NIT no salen por la API: quedan para la fase de PILA.</summary>
public sealed record CompensationFund(string Name, string Department, string Nit);

/// <summary>
/// Catálogo fijo de cajas de compensación que una sede puede elegir (spec RRHH fase 1, delta 2026-11-21). Sale del listado de
/// la UGPP del 14-01-2026: primero las 30 sin medida especial y luego las 12 con intervención administrativa. Verificar contra
/// SuperSubsidio antes de la fase de nómina (puede faltar una y el NIT importa para PILA). Corregirla es un cambio de código,
/// sin migración: la sede guarda el nombre.
/// </summary>
public static class HrCompensationFunds
{
    public static IReadOnlyList<CompensationFund> All { get; } =
    [
        new("CAFAMAZ", "Amazonas", "800003122"),
        new("COMFAMA", "Antioquia", "890900841"),
        new("CAJASAI", "San Andrés y Providencia", "892400320"),
        new("COMBARRANQUILLA", "Atlántico", "890102002"),
        new("CAJACOPI Atlántico", "Atlántico", "890102044"),
        new("COMFAMILIAR Atlántico", "Atlántico", "890101994"),
        new("COMFENALCO Cartagena", "Bolívar", "890480023"),
        new("COMFABOY", "Boyacá", "891800213"),
        new("CONFA", "Caldas", "890806490"),
        new("COMFACASANARE", "Casanare", "844003392"),
        new("COMFACAUCA", "Cauca", "891500182"),
        new("COMFACESAR", "Cesar", "892399989"),
        new("COMFACHOCÓ", "Chocó", "891600091"),
        new("COLSUBSIDIO", "Bogotá y Cundinamarca", "860007336"),
        new("CAFAM", "Bogotá y Cundinamarca", "860013570"),
        new("COMPENSAR", "Bogotá y Cundinamarca", "860066942"),
        new("COMFACUNDI", "Cundinamarca", "860045904"),
        new("CAJAMAG", "Magdalena", "891780093"),
        new("COFREM", "Meta", "892000146"),
        new("COMFAORIENTE", "Norte de Santander", "890500675"),
        new("COMFAMILIAR Putumayo", "Putumayo", "891200337"),
        new("COMFENALCO Quindío", "Quindío", "890000381"),
        new("COMFENALCO Santander", "Santander", "890201578"),
        new("CAJASAN", "Santander", "890200106"),
        new("COMFASUCRE", "Sucre", "892200015"),
        new("CAFASUR", "Tolima", "890704737"),
        new("COMFENALCO Tolima", "Tolima", "890700148"),
        new("COMFATOLIMA", "Tolima", "800211025"),
        new("COMFENALCO Valle", "Valle del Cauca", "890303093"),
        new("COMFANDI", "Valle del Cauca", "890303208"),
        // Con intervención administrativa de la Superintendencia del Subsidio Familiar: siguen operando.
        new("COMFENALCO Antioquia", "Antioquia", "890900842"),
        new("COMFIAR", "Arauca", "800219488"),
        new("COMFAMILIAR Cartagena y Bolívar", "Bolívar", "890480110"),
        new("COMFACA", "Caquetá", "891190047"),
        new("COMFACOR", "Córdoba", "891080005"),
        new("COMCAJA", "Cundinamarca", "800231969"),
        new("COMFAGUAJIRA", "La Guajira", "892115006"),
        new("COMFAMILIAR Huila", "Huila", "891180008"),
        new("COMFAMILIAR Nariño", "Nariño", "891200208"),
        new("CAFABA", "Norte de Santander", "890270275"),
        new("COMFANORTE", "Norte de Santander", "890500516"),
        new("COMFAMILIAR Risaralda", "Risaralda", "891480000"),
    ];

    /// <summary>Los nombres en orden alfabético (el catálogo conserva el orden de la UGPP): es lo que <c>GET /hr/settings</c> devuelve como <c>compensationFunds</c>.</summary>
    public static IReadOnlyList<string> Names { get; } =
        All.Select(fund => fund.Name).OrderBy(name => name, StringComparer.InvariantCultureIgnoreCase).ToList();

    private static readonly HashSet<string> NameSet = new(Names, StringComparer.Ordinal);

    /// <summary>Coincidencia exacta, sensible a mayúsculas: la sede guarda el nombre tal cual figura en la lista.</summary>
    public static bool Contains(string? name) => name is not null && NameSet.Contains(name);
}
