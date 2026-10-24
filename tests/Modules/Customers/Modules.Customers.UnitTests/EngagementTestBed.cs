using BuildingBlocks.Application;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>Dobles en memoria para las notas, las etiquetas y el consentimiento (spec 2026-10-11 §4): sin base, con
/// lo mínimo que los casos de uso le piden a cada puerto.</summary>
internal static class EngagementTestBed
{
    public static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    public static readonly Guid OtherTenantId = Guid.Parse("01900000-0000-7000-8000-0000000000a2");
    public static readonly Guid UserA = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    public static readonly Guid UserB = Guid.Parse("01900000-0000-7000-8000-0000000000b2");
    public static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    public static string[] Manager => [CustomersPermissions.CustomerRead, CustomersPermissions.CustomerManage];

    public static string[] Moderator =>
        [CustomersPermissions.CustomerRead, CustomersPermissions.CustomerManage, CustomersPermissions.NoteModerate];

    public static string[] TagAdmin =>
        [CustomersPermissions.CustomerRead, CustomersPermissions.CustomerManage, CustomersPermissions.TagManage];

    public sealed class Context(Guid tenantId, Guid subjectId, params string[] permissions) : IExecutionContext
    {
        public Guid SubjectId => subjectId;

        public TenantId TenantId => new(tenantId);

        public bool HasPermission(string permission) => permissions.Contains(permission);
    }

    public sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    public sealed class RecordingAudit : ICustomersAuditPublisher
    {
        public List<(Guid Tenant, Guid Actor, string Action, string ResourceId)> Events { get; } = [];

        public void Publish(Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt, string? source = null) =>
            Events.Add((tenantId, actorId, action, resourceId));

        public void PublishByIntegration(Guid tenantId, string action, string resourceId, string outcome, DateTimeOffset occurredAt) =>
            Events.Add((tenantId, Guid.Empty, action, resourceId));
    }

    public sealed class CountingUnitOfWork : ICustomersUnitOfWork
    {
        public int Saves { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            return Task.FromResult(1);
        }

        public Task<ICustomersTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ICustomersTransaction>(CustomerWriteDoubles.NoTransaction.Instance);
    }

    public sealed class FakeAuthorNames(Dictionary<Guid, string> names) : ICustomerAuthorNames
    {
        public Task<IReadOnlyDictionary<Guid, string>> FindAsync(
            Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                names.Where(pair => userIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    public sealed class FakeNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) => phone;

        public bool IsKnownRegion(string regionCode) => true;

        public string? RegionOf(string? e164) => null;
    }

    public static Customer NewCustomer(Guid? tenantId = null) =>
        Customer.CreateIncomplete(
            CustomerId.New(), tenantId ?? TenantId, "Laura Pérez", "+573001234567", "CO", "CO." + Guid.NewGuid().ToString("N"), Now, new FakeNormalizer());

    public sealed class FakeCustomers : ICustomerRepository
    {
        public List<Customer> Items { get; } = [];

        public Task<Customer?> FindAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.SingleOrDefault(item => item.TenantId == tenantId && item.Id == customerId));

        public void Add(Customer customer) => Items.Add(customer);

        public Task<CustomerId?> FindIdByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<Customer> Items, int Total)> SearchAsync(Guid tenantId, string? search, string? name, string? identificationNumber, string? cuc, IReadOnlyCollection<Guid>? cityIds, bool? isComplete, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Customer>> SearchSelectableAsync(Guid tenantId, string? search, int take, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Customer>> ListForExportAsync(Guid tenantId, string? search, string? name, string? identificationNumber, string? cuc, int skip, int take, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> AnyWithClassificationAsync(Guid tenantId, ClientClassificationId classificationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<(IdentificationType Type, string Number), CustomerId>> FindExistingIdentificationsAsync(Guid tenantId, IReadOnlyCollection<(IdentificationType Type, string Number)> identifications, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, CustomerId>> FindIdsByCucSuffixAsync(Guid tenantId, IReadOnlyCollection<string> suffixes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<Guid>> SearchIdsByIdentificationNumberAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<Guid>> SearchIdsByCucAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<CustomerId, string>> FindNamesByIdsAsync(Guid tenantId, IReadOnlyCollection<CustomerId> customerIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<CustomerId, Customer>> FindManyAsync(Guid tenantId, IReadOnlyCollection<CustomerId> customerIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Customer?> FindByWhatsAppUserIdAsync(Guid tenantId, string whatsAppUserId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Customer?> FindByInstagramUserIdAsync(Guid tenantId, string instagramUserId, CancellationToken cancellationToken) =>
            Task.FromResult<Customer?>(null);

        public Task<Customer?> FindOldestByPhoneE164Async(Guid tenantId, string phoneE164, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryAttachWhatsAppUserIdAsync(Guid tenantId, CustomerId customerId, string whatsAppUserId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CustomerWhatsAppRef>> FindWhatsAppRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    public sealed class FakeNotes : ICustomerNoteRepository
    {
        public List<CustomerNote> Items { get; } = [];

        public Task<IReadOnlyList<CustomerNote>> ListByCustomerAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomerNote>>(
                Items.Where(item => item.TenantId == tenantId && item.CustomerId == customerId)
                    .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id.Value).ToList());

        public Task<CustomerNote?> FindAsync(Guid tenantId, CustomerId customerId, CustomerNoteId noteId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.SingleOrDefault(item =>
                item.TenantId == tenantId && item.CustomerId == customerId && item.Id == noteId));

        public void Add(CustomerNote note) => Items.Add(note);

        public void Remove(CustomerNote note) => Items.Remove(note);
    }

    public sealed class FakeTags : ICustomerTagRepository
    {
        public List<Tag> Tags { get; } = [];

        public List<CustomerTagAssignment> Assignments { get; } = [];

        private TagWithCount WithCount(Tag tag) =>
            new(tag, Assignments.Count(assignment => assignment.TagId == tag.Id));

        public Task<IReadOnlyList<CustomerId>> ListCustomerIdsAsync(Guid tenantId, TagId tagId, int cap, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomerId>>(
                Assignments.Where(assignment => assignment.TenantId == tenantId && assignment.TagId == tagId)
                    .Select(assignment => assignment.CustomerId).Take(cap).ToList());

        public Task<IReadOnlyList<(CustomerId Customer, Tag Tag)>> ListForCustomersAsync(
            Guid tenantId, IReadOnlyCollection<CustomerId> customerIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<(CustomerId Customer, Tag Tag)>>(
                [.. Assignments.Where(assignment => assignment.TenantId == tenantId && customerIds.Contains(assignment.CustomerId))
                    .Join(Tags, assignment => assignment.TagId, tag => tag.Id, (assignment, tag) => (assignment.CustomerId, tag))]);

        public Task<IReadOnlyList<TagWithCount>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TagWithCount>>(
                Tags.Where(tag => tag.TenantId == tenantId)
                    .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase).Select(WithCount).ToList());

        public Task<IReadOnlyList<TagWithCount>> ListForCustomerAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken)
        {
            var ids = Assignments.Where(assignment => assignment.CustomerId == customerId).Select(assignment => assignment.TagId).ToHashSet();
            return Task.FromResult<IReadOnlyList<TagWithCount>>(
                Tags.Where(tag => tag.TenantId == tenantId && ids.Contains(tag.Id))
                    .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase).Select(WithCount).ToList());
        }

        public Task<Tag?> FindAsync(Guid tenantId, TagId tagId, CancellationToken cancellationToken) =>
            Task.FromResult(Tags.SingleOrDefault(tag => tag.TenantId == tenantId && tag.Id == tagId));

        public Task<IReadOnlyList<Tag>> ListByIdsAsync(Guid tenantId, IReadOnlyCollection<TagId> tagIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Tag>>(Tags.Where(tag => tag.TenantId == tenantId && tagIds.Contains(tag.Id)).ToList());

        public Task<int> CountAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(Tags.Count(tag => tag.TenantId == tenantId));

        public Task<int> CountCustomersAsync(Guid tenantId, TagId tagId, CancellationToken cancellationToken) =>
            Task.FromResult(Assignments.Count(assignment => assignment.TagId == tagId));

        public Task<IReadOnlyList<CustomerTagAssignment>> ListAssignmentsAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomerTagAssignment>>(
                Assignments.Where(assignment => assignment.TenantId == tenantId && assignment.CustomerId == customerId).ToList());

        public void Add(Tag tag) => Tags.Add(tag);

        public void Remove(Tag tag)
        {
            Tags.Remove(tag);
            Assignments.RemoveAll(assignment => assignment.TagId == tag.Id);
        }

        public void AddAssignment(CustomerTagAssignment assignment) => Assignments.Add(assignment);

        public void RemoveAssignment(CustomerTagAssignment assignment) => Assignments.Remove(assignment);
    }
}
