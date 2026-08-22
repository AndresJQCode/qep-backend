namespace Modules.Geography.Infrastructure.Seed;

// Formas de deserialización del JSON crudo del DANE (claves en minúscula: "code", "name",
// "department", y un "municipality" adicional en las entradas de centro poblado que no nos
// interesa y que System.Text.Json ignora por no estar declarado acá).

internal sealed record DivipolaJsonDepartment(string? Code, string? Name);

internal sealed record DivipolaJsonDepartmentRef(string? Code, string? Name);

internal sealed record DivipolaJsonCity(
    string? Code, string? Name, DivipolaJsonDepartmentRef? Department);
