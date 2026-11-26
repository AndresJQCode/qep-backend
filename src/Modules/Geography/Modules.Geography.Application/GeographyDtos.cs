namespace Modules.Geography.Application;

public sealed record DepartmentDto(Guid Id, string DivipolaCode, string Name);

public sealed record CityDto(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);

/// <summary>
/// Un país del catálogo mundial. <c>RegionKind</c> es null si el país no tiene catálogo de regiones y ciudades; si lo tiene,
/// viaja con su nombre de enum (<c>Department</c>, <c>Province</c>): el diccionario para pintar "Departamento" o
/// "Provincia" lo tiene el frontend, y con el null la pantalla decide con una sola regla si ofrece los selectores en
/// cascada o cae al texto libre.
/// </summary>
public sealed record CountryDto(string Code, string Name, string? RegionKind);

/// <summary>
/// Una región elegible de un país, con la misma forma para cualquiera. Un país sin catálogo devuelve
/// la lista vacía y no un error: así la pantalla cae al texto libre con una sola regla, sin importar el país.
/// En Colombia <c>Id</c> y <c>Code</c> son los del departamento DIVIPOLA.
/// </summary>
public sealed record RegionOptionDto(Guid Id, string Code, string Name);

/// <summary>
/// Una ciudad elegible de una región. En Colombia <c>Id</c> es el <c>CityId</c> que el cliente guarda; fuera de
/// Colombia el cliente guarda el <c>Name</c> como texto y este id no se persiste en ningún lado.
/// </summary>
public sealed record RegionCityOptionDto(Guid Id, string Code, string Name);
