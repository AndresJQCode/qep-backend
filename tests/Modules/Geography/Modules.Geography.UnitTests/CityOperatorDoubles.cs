using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Geography.UnitTests;

/// <summary>Dobles en memoria de los puertos que leen los handlers de operador de ciudades.</summary>
internal sealed class CityOperatorDoubles(TenantId operatorTenant)
{
    public InMemoryDepartments Departments { get; } = new();

    public InMemoryCities Cities { get; } = new();

    public InMemoryUnresolvedCities UnresolvedCities { get; } = new();

    public FixedInsights Insights { get; } = new();

    public IOperatorTenant OperatorTenant { get; } = new FixedOperatorTenant(operatorTenant);

    public DepartmentId AddDepartment(string code, string name)
    {
        var department = Department.Create(DepartmentId.New(), code, name);
        Departments.Items.Add(department);
        return department.Id;
    }

    public City AddCity(string code, string divipolaName, string? commonName, DepartmentId department)
    {
        var city = City.Create(CityId.New(), code, divipolaName, department);
        city.SetCommonName(commonName);
        Cities.Items.Add(city);
        return city;
    }

    public ListUnresolvedCitiesHandler ListHandler(TenantId tenantId, params string[] permissions) =>
        new(UnresolvedCities, Departments, Cities, new Context(tenantId, permissions), OperatorTenant, new ListUnresolvedCitiesValidator());

    internal sealed class Context(TenantId tenantId, params string[] permissions) : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.CreateVersion7();

        public TenantId TenantId => tenantId;

        public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
    }

    private sealed class FixedOperatorTenant(TenantId operatorTenantId) : IOperatorTenant
    {
        public bool IsOperator(Guid tenantId) => operatorTenantId.Value == tenantId;
    }

    internal sealed class InMemoryDepartments : IDepartmentRepository
    {
        public List<Department> Items { get; } = [];

        public Task<IReadOnlyList<Department>> ListAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Department>>(Items);

        public Task<Department?> FindAsync(DepartmentId departmentId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.SingleOrDefault(item => item.Id == departmentId));

        public Task<IReadOnlyList<Department>> ListByIdsAsync(
            IReadOnlyCollection<DepartmentId> departmentIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Department>>(Items.Where(item => departmentIds.Contains(item.Id)).ToList());

        public Task<Department?> FindByNameAsync(string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    internal sealed class InMemoryCities : ICityRepository
    {
        public List<City> Items { get; } = [];

        public Task<IReadOnlyList<City>> ListByDepartmentAsync(DepartmentId departmentId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<City>>(Items.Where(item => item.DepartmentId == departmentId).ToList());

        public Task<City?> FindAsync(CityId cityId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.SingleOrDefault(item => item.Id == cityId));

        public Task<IReadOnlyList<City>> ListByIdsAsync(IReadOnlyCollection<CityId> cityIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<City>>(Items.Where(item => cityIds.Contains(item.Id)).ToList());

        public Task<City?> FindByNameAsync(DepartmentId departmentId, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CityNameMatch?> MatchByNameAsync(DepartmentId departmentId, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<City>> ListByDepartmentsAsync(
            IReadOnlyCollection<DepartmentId> departmentIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<City>>(Items.Where(item => departmentIds.Contains(item.DepartmentId)).ToList());
    }

    internal sealed class InMemoryUnresolvedCities : IUnresolvedCityRepository
    {
        private readonly List<UnresolvedCityRow> rows = [];

        /// <summary>El estado con el que se pidió cada página: la prueba confirma que la validación cortó antes.</summary>
        public List<(UnresolvedCityStatus Status, int Page)> Listed { get; } = [];

        public void Add(UnresolvedCity city, int orderCount, int tenantCount) =>
            rows.Add(new UnresolvedCityRow(city, orderCount, tenantCount));

        public Task<UnresolvedCity?> FindAsync(UnresolvedCityId id, CancellationToken cancellationToken) =>
            Task.FromResult(rows.Select(row => row.City).SingleOrDefault(city => city.Id == id));

        public Task<(IReadOnlyList<UnresolvedCityRow> Rows, int Total)> ListAsync(
            UnresolvedCityStatus status, DepartmentId? departmentId, string? normalizedSearch, int page, int pageSize,
            CancellationToken cancellationToken)
        {
            Listed.Add((status, page));
            var matching = rows.Where(row => row.City.Status == status).ToList();
            return Task.FromResult<(IReadOnlyList<UnresolvedCityRow>, int)>((matching, matching.Count));
        }

        public Task<IReadOnlyList<UnresolvedCityOccurrence>> ListOccurrencesAsync(
            UnresolvedCityId id, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UnresolvedCityOccurrence>>([]);

        public Task<CitySummaryCounts> CountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CitySummaryCounts(0, 0, 0, 0));

        public Task<(UnresolvedCityId Id, bool OccurrenceInserted)> UpsertWithOccurrenceAsync(
            UnresolvedCityReport report, string normalizedText, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    internal sealed class FixedInsights : ICityCorrectionInsights
    {
        public Dictionary<Guid, string> TenantNames { get; } = [];

        public Task<IReadOnlyList<OrderInsight>> ReadOrdersAsync(
            IReadOnlyCollection<TenantOrderKey> keys, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderInsight>>([]);

        public Task<IReadOnlyList<CustomerInsight>> ReadCustomersAsync(
            IReadOnlyCollection<TenantCustomerKey> keys, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomerInsight>>([]);

        public Task<IReadOnlyList<CorrectionInsight>> ReadCorrectionsAsync(CitySynonymId synonymId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CorrectionInsight>>([]);

        public Task<IReadOnlyDictionary<Guid, SynonymCorrectionCounts>> CountCorrectionsAsync(
            IReadOnlyCollection<CitySynonymId> synonymIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, SynonymCorrectionCounts>>(
                synonymIds.ToDictionary(id => id.Value, _ => new SynonymCorrectionCounts(0, 0, 0, 0)));

        public Task<IReadOnlyDictionary<Guid, string>> ReadTenantNamesAsync(
            IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                TenantNames.Where(pair => tenantIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));

        public Task<IReadOnlyDictionary<Guid, string>> ReadUserEmailsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }
}
