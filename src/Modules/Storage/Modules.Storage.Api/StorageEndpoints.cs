using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using BuildingBlocks.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.Api;

public static class StorageEndpoints
{
    public static IEndpointRouteBuilder MapStorageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/files")
            .WithTags("Storage");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(StoragePermissions.FileRead)
            .Produces<PagedFilesResponse>();

        group.MapPost("/", CreateUploadSessionAsync)
            .RequireAuthorization(StoragePermissions.FileUpload)
            .Accepts<CreateFileRequest>("application/json")
            .Produces<UploadSessionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{fileId:guid}/complete", CompleteUploadAsync)
            .RequireAuthorization(StoragePermissions.FileUpload)
            .Produces<FileResourceResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/{fileId:guid}/upload", CancelUploadAsync)
            .RequireAuthorization(StoragePermissions.FileUpload)
            .Produces<CancelUploadResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPatch("/{fileId:guid}/metadata", UpdateMetadataAsync)
            .RequireAuthorization(StoragePermissions.FileUpload)
            .Accepts<UpdateFileMetadataRequest>("application/json")
            .Produces<FileResourceResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{fileId:guid}/download-url", IssueDownloadUrlAsync)
            .RequireAuthorization(StoragePermissions.FileRead)
            .Produces<DownloadUrlResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{fileId:guid}/publication", PublishAsync)
            .RequireAuthorization(StoragePermissions.FilePublish)
            .Produces<FileResourceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{fileId:guid}/publication", UnpublishAsync)
            .RequireAuthorization(StoragePermissions.FilePublish)
            .Produces<FileResourceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{fileId:guid}", SoftDeleteAsync)
            .RequireAuthorization(StoragePermissions.FileDelete)
            .Produces<SoftDeleteResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> CreateUploadSessionAsync(
        Guid tenantId,
        CreateFileRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        // CAT-05: esto caía en silencio a FileOwnerType.User cuando el string no parseaba, así
        // que un ownerType inválido no fallaba — se convertía en otro, devolvía 201, y el archivo
        // quedaba mal clasificado sin que nadie se enterara. Un error es mejor que un dato falso.
        //
        // Enum.TryParse acepta además el número crudo ("4"), que no es contrato: se descarta
        // exigiendo que el valor esté definido y no sea dígitos.
        if (!Enum.TryParse<FileOwnerType>(request.OwnerType, ignoreCase: true, out var ownerType) ||
            !Enum.IsDefined(ownerType) ||
            char.IsDigit(request.OwnerType.Trim().FirstOrDefault()))
        {
            throw new StorageDomainException(
                "storage.file.owner_type_invalid",
                "The owner type is not one of the supported values.");
        }

        var session = await dispatcher.SendAsync(
            new CreateUploadSessionCommand(
                tenantId,
                request.OwnerId,
                ownerType,
                request.Name,
                request.MimeType,
                request.SizeBytes),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/files/{session.FileResourceId}",
            new UploadSessionResponse(session.FileResourceId, session.UploadUrl, session.StorageKey));
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null,
        string? status = null,
        string? kind = null,
        string? category = null,
        string? tag = null,
        Guid? ownerId = null,
        string? ownerType = null,
        int page = 1,
        int pageSize = 20)
    {
        // DEUDA DECLARADA (CAT-09): este parseo del status cae en null cuando el string no
        // parsea, así que ?status=Basura devuelve la lista SIN FILTRAR, como si no se hubiera
        // pedido nada. Es el mismo fallback silencioso que CAT-05 corrigió en el POST. No se
        // corrige acá porque es un cambio de contrato de un filtro preexistente que ningún
        // criterio de CAT-09 necesita — pero el filtro que CAT-09 agrega no lo repite.
        var parsedStatus = Enum.TryParse<FileResourceStatus>(status, ignoreCase: true, out var value)
            ? value
            : (FileResourceStatus?)null;
        var owner = FileOwnerFilter.Resolve(ownerId, ownerType);
        var result = await dispatcher.QueryAsync(
            new ListFilesQuery(
                tenantId, search, parsedStatus, kind, category, tag, owner, page, pageSize),
            cancellationToken);
        return Results.Ok(new PagedFilesResponse(
            result.Items.Select(ToResponse).ToArray(),
            result.TotalCount,
            result.Page,
            result.PageSize));
    }

    private static async Task<IResult> CompleteUploadAsync(
        Guid tenantId,
        Guid fileId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var dto = await dispatcher.SendAsync(
            new CompleteUploadCommand(tenantId, fileId), cancellationToken);
        return Results.Ok(ToResponse(dto));
    }

    private static async Task<IResult> CancelUploadAsync(
        Guid tenantId,
        Guid fileId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new CancelUploadCommand(tenantId, fileId), cancellationToken);
        return Results.Ok(new CancelUploadResponse(result.Cancelled));
    }

    private static async Task<IResult> UpdateMetadataAsync(
        Guid tenantId,
        Guid fileId,
        UpdateFileMetadataRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var dto = await dispatcher.SendAsync(
            new UpdateFileMetadataCommand(
                tenantId,
                fileId,
                request.Category,
                request.Tags ?? []),
            cancellationToken);
        return Results.Ok(ToResponse(dto));
    }

    private static async Task<IResult> IssueDownloadUrlAsync(
        Guid tenantId,
        Guid fileId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? variant = null)
    {
        var dto = await dispatcher.SendAsync(
            new IssueDownloadUrlCommand(tenantId, fileId, variant), cancellationToken);
        return Results.Ok(new DownloadUrlResponse(dto.Url));
    }

    private static async Task<IResult> SoftDeleteAsync(
        Guid tenantId,
        Guid fileId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new SoftDeleteFileCommand(tenantId, fileId), cancellationToken);
        return Results.Ok(new SoftDeleteResponse(result.Deleted));
    }

    private static async Task<IResult> PublishAsync(
        Guid tenantId, Guid fileId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var dto = await dispatcher.SendAsync(new PublishFileCommand(tenantId, fileId), cancellationToken);
        return Results.Ok(ToResponse(dto));
    }

    private static async Task<IResult> UnpublishAsync(
        Guid tenantId, Guid fileId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var dto = await dispatcher.SendAsync(new UnpublishFileCommand(tenantId, fileId), cancellationToken);
        return Results.Ok(ToResponse(dto));
    }

    private static FileResourceResponse ToResponse(FileResourceDto dto) =>
        new(
            dto.Id,
            dto.TenantId,
            dto.OwnerId,
            dto.OwnerType,
            dto.Name,
            dto.MimeType,
            dto.SizeBytes,
            dto.Status,
            dto.Category,
            dto.Tags,
            dto.IsPublic,
            dto.PublicUrl,
            dto.Variants.Select(variant => new FileVariantResponse(
                variant.Name,
                variant.MimeType,
                variant.Width,
                variant.Height,
                variant.SizeBytes,
                variant.PublicUrl)).ToArray(),
            dto.CreatedAt);
}

public sealed record CreateFileRequest(
    Guid OwnerId,
    string OwnerType,
    string Name,
    string MimeType,
    long SizeBytes);

public sealed record UploadSessionResponse(Guid FileResourceId, string UploadUrl, string StorageKey);

public sealed record FileResourceResponse(
    Guid Id,
    Guid TenantId,
    Guid OwnerId,
    string OwnerType,
    string Name,
    string MimeType,
    long SizeBytes,
    string Status,
    string? Category,
    IReadOnlyList<string> Tags,
    bool IsPublic,
    string? PublicUrl,
    IReadOnlyList<FileVariantResponse> Variants,
    DateTimeOffset CreatedAt);

public sealed record FileVariantResponse(
    string Name,
    string MimeType,
    int Width,
    int Height,
    long SizeBytes,
    string? PublicUrl);

public sealed record UpdateFileMetadataRequest(
    string? Category,
    IReadOnlyList<string>? Tags);

public sealed record PagedFilesResponse(
    IReadOnlyList<FileResourceResponse> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record DownloadUrlResponse(string Url);

public sealed record SoftDeleteResponse(bool Deleted);

public sealed record CancelUploadResponse(bool Cancelled);
