namespace Modules.Geography.Application;

public sealed record DepartmentDto(Guid Id, string DivipolaCode, string Name);

public sealed record CityDto(Guid Id, string DivipolaCode, string Name, Guid DepartmentId);
