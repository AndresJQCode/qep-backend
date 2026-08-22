namespace Modules.Storage.Domain;

public sealed class FileVariant
{
    private FileVariant()
    {
    }

    internal FileVariant(
        FileResourceId fileResourceId,
        string name,
        string storageKey,
        string mimeType,
        int width,
        int height,
        long sizeBytes)
    {
        FileResourceId = fileResourceId;
        Name = name;
        StorageKey = storageKey;
        MimeType = mimeType;
        Width = width;
        Height = height;
        SizeBytes = sizeBytes;
    }

    public FileResourceId FileResourceId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string StorageKey { get; private set; } = string.Empty;

    public string MimeType { get; private set; } = string.Empty;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public long SizeBytes { get; private set; }
}
