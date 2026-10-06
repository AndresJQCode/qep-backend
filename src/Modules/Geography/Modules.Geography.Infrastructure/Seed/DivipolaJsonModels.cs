namespace Modules.Geography.Infrastructure.Seed;

// Forma del JSON curado en Seed/Data (claves en minúscula: "code", "name", "departmentCode",
// planas — no el JSON crudo del DANE, que trae "department" anidado).

internal sealed record DivipolaJsonDepartment(string? Code, string? Name);

internal sealed record DivipolaJsonCity(string? Code, string? Name, string? DepartmentCode);

// El snapshot de Coordinadora (Seed/Data/coordinadora-cities.json): "code" es el código DIVIPOLA
// del municipio, de 5 dígitos, y "name" el nombre como lo escribe Coordinadora.
internal sealed record CoordinadoraJsonCity(string? Code, string? Name);

// Los nombres comunes (Seed/Data/common-names.json): "code" es el código DIVIPOLA del municipio,
// de 5 dígitos, y "name" el nombre con el que la gente lo conoce ("CALI").
internal sealed record CommonNameJsonCity(string? Code, string? Name);
