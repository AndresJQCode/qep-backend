using Microsoft.Extensions.Logging.Abstractions;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Fase 4b (H5, owner 2026-11-16, Q-6): el membrete de la respuesta formal es la empresa de la versión del formulario con que se
/// radicó el ticket o, sin ella, la de la versión vigente; sin empresa no hay membrete (null) y la respuesta formal se rechaza. El logo
/// es el del tenant, PNG o JPEG: <c>qcode-pdf</c> no imprime WebP, así que ese logo se omite y el PDF sale sin imagen.
/// </summary>
public sealed class SupportLetterheadTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] LogoBytes = [0x89, 0x50, 0x4E, 0x47];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ItUsesTheTicketFormCompanyFirst()
    {
        var ticketCompany = Company("Verde S.A.S.", "900123456");
        var currentCompany = Company("Azul Ltda.", "800111222");
        var ticketForm = Form(1, ticketCompany.Id);
        var currentForm = Form(2, currentCompany.Id);
        var logo = AvailableLogo("image/png");
        var letterhead = LetterheadWith([ticketForm, currentForm], [ticketCompany, currentCompany], logo);

        var found = await letterhead.FindAsync(TenantId, ticketForm.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal("Verde S.A.S.", found.CompanyName);
        Assert.Equal("NIT", found.DocumentTypeCode);
        Assert.Equal("900123456", found.DocumentNumber);
        Assert.Equal("Calle 10 # 20-30", found.Address);
        Assert.Equal("Medellín", found.CityName);
        Assert.Equal("contacto@verde.co", found.Email);
        Assert.Equal("6041234567", found.Phone);
        Assert.NotNull(found.Logo);
        Assert.Equal("logo.png", found.Logo.FileName);
        Assert.Equal(LogoBytes, found.Logo.Content);
    }

    [Fact]
    public async Task ItFallsBackToTheCurrentFormCompany()
    {
        var currentCompany = Company("Azul Ltda.", "800111222");
        var oldForm = Form(1, organization: null);
        var currentForm = Form(2, currentCompany.Id);
        var letterhead = LetterheadWith([oldForm, currentForm], [currentCompany]);

        // Un ticket sin versión de formulario (alta manual o desde una conversación) y uno cuya versión no eligió empresa.
        var withoutForm = await letterhead.FindAsync(TenantId, null, Ct);
        var withoutCompany = await letterhead.FindAsync(TenantId, oldForm.Id, Ct);

        Assert.Equal("Azul Ltda.", withoutForm?.CompanyName);
        Assert.Equal("Azul Ltda.", withoutCompany?.CompanyName);
        Assert.Null(withoutForm?.Logo);
    }

    [Fact]
    public async Task ItIsNullWithoutACompany()
    {
        var foreignCompany = Company("Ajena S.A.", "600111222");
        var withoutCompany = LetterheadWith([Form(1, organization: null)], []);
        // La empresa de la versión ya no es del tenant (el directorio la filtra por tenant): no hay membrete.
        var foreign = LetterheadWith([Form(1, foreignCompany.Id)], []);
        var withoutForms = LetterheadWith([], [foreignCompany]);

        Assert.Null(await withoutCompany.FindAsync(TenantId, null, Ct));
        Assert.Null(await foreign.FindAsync(TenantId, null, Ct));
        Assert.Null(await withoutForms.FindAsync(TenantId, null, Ct));
    }

    [Fact]
    public async Task ItSkipsAWebpLogo()
    {
        var company = Company("Verde S.A.S.", "900123456");
        var form = Form(1, company.Id);
        var jpeg = await LetterheadWith([form], [company], AvailableLogo("image/jpeg")).FindAsync(TenantId, form.Id, Ct);
        var webp = await LetterheadWith([form], [company], AvailableLogo("image/webp")).FindAsync(TenantId, form.Id, Ct);

        Assert.Equal("logo.jpg", jpeg?.Logo?.FileName);
        Assert.NotNull(webp);
        Assert.Null(webp.Logo);
        Assert.Equal("Verde S.A.S.", webp.CompanyName);
    }

    [Fact]
    public async Task AnUnreadableLogoLeavesTheLetterheadWithoutImage()
    {
        var company = Company("Verde S.A.S.", "900123456");
        var form = Form(1, company.Id);
        var logo = AvailableLogo("image/png");
        var letterhead = new SupportLetterhead(
            new FakeForms([form]),
            new FakeOrganizations([company]),
            new FakeTenants(logo.Id.Value),
            new InMemoryFileResourceRepository(logo),
            new LogoStorage(failure: new InvalidOperationException("R2 down")),
            NullLogger<SupportLetterhead>.Instance);

        var found = await letterhead.FindAsync(TenantId, form.Id, Ct);

        Assert.NotNull(found);
        Assert.Null(found.Logo);
    }

    private static SupportLetterhead LetterheadWith(
        IReadOnlyList<SupportFormVersion> forms, IReadOnlyList<SupportOrganizationRef> companies, FileResource? logo = null) =>
        new(
            new FakeForms(forms),
            new FakeOrganizations(companies),
            new FakeTenants(logo?.Id.Value),
            logo is null ? new InMemoryFileResourceRepository() : new InMemoryFileResourceRepository(logo),
            new LogoStorage(),
            NullLogger<SupportLetterhead>.Instance);

    private static SupportOrganizationRef Company(string name, string taxId) =>
        new(Guid.CreateVersion7(), name, "NIT", taxId, true, "Calle 10 # 20-30", "contacto@verde.co", "6041234567", "Medellín");

    private static SupportFormVersion Form(int version, Guid? organization) =>
        SupportFormVersion.Create(
            Guid.CreateVersion7(), TenantId, version, true, true, true, true, true, false, "#00B4FF", "Cuéntanos tu caso.",
            "Recibimos tu caso.", Guid.CreateVersion7(), Now, organization);

    private static FileResource AvailableLogo(string mimeType)
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, TenantId, FileOwnerType.Tenant, "logo", mimeType, 1024,
            $"files/tenants/{TenantId:N}/logo", Now);
        file.CompleteUpload("checksum", 1024, Now);
        file.MarkClean(Now);
        return file;
    }

    private sealed class FakeForms(IReadOnlyList<SupportFormVersion> forms) : ISupportFormRepository
    {
        public Task<SupportFormVersion?> CurrentAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(forms.Where(form => form.TenantId == tenantId).MaxBy(form => form.Version));

        public Task<SupportFormVersion?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(forms.FirstOrDefault(form => form.TenantId == tenantId && form.Id == id));

        public void Add(SupportFormVersion form) => throw new NotSupportedException();
    }

    private sealed class FakeOrganizations(IReadOnlyList<SupportOrganizationRef> companies) : ISupportOrganizationDirectory
    {
        public Task<IReadOnlyList<SupportOrganizationRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SupportOrganizationRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
            Task.FromResult(companies.FirstOrDefault(company => company.Id == companyId));
    }

    private sealed class FakeTenants(Guid? logoFileId) : ITenantDirectory
    {
        public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => Task.FromResult(logoFileId);

        public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class LogoStorage(Exception? failure = null) : IObjectStorage
    {
        public Task<Uri> CreatePresignedUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(string key, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PromoteAsync(string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) =>
            failure is null ? Task.FromResult(LogoBytes.ToArray()) : Task.FromException<byte[]>(failure);

        public Task UploadAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UploadAsync(
            string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
