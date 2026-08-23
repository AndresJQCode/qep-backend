namespace Modules.Geography.Infrastructure.Seed;

// Forma del JSON curado en Seed/Data (claves en minúscula: "code", "name", "departmentCode",
// planas — no el JSON crudo del DANE, que trae "department" anidado).

internal sealed record DivipolaJsonDepartment(string? Code, string? Name);

internal sealed record DivipolaJsonCity(string? Code, string? Name, string? DepartmentCode);
