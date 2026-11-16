using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Modules.Support.Domain;

/// <summary>
/// Plan N18 (owner O4): the Storage owner of an internal note's upload. Storage does not record who uploaded a file; the pair
/// <c>(owner_type, owner_id)</c> is all its member-less upload checks on every operation. Deriving <c>owner_id</c> from the ticket and
/// the member makes "uploaded by the same member, for this ticket" hold without Storage knowing either: another member, another
/// ticket or a public form upload (a random v4 key) never matches. It is not a secret against a colleague with
/// <c>support.ticket.manage</c> on the same ticket, who can write the note anyway.
/// </summary>
public static class SupportNoteUploads
{
    private const string Purpose = "support-note";

    /// <summary>The UUID version a note's key carries (RFC 9562 v8, "custom"). A public form upload's key is <c>Guid.NewGuid()</c>,
    /// always v4, so the version alone tells the two apart in <c>storage.file_resources.owner_id</c>: the public form's
    /// <c>attachments_busy</c> cap counts only v4 keys (<c>SupportAttachmentStorage.CountPendingAsync</c>), and a note upload a member
    /// opened and never attached does not starve it (Block B review).</summary>
    public const int OwnerKeyVersion = 8;

    /// <summary>The first 16 bytes of SHA-256 over <c>support-note|{ticketId:D}|{memberId:D}</c>, read in RFC byte order, with the
    /// version set to <see cref="OwnerKeyVersion"/> and the RFC variant. Deterministic and stable: it is what Storage stored as the
    /// owner, so changing it orphans every upload that is not in a note yet.</summary>
    public static Guid OwnerKey(SupportTicketId ticketId, Guid memberId)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture, $"{Purpose}|{ticketId.Value:D}|{memberId:D}");
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        var key = hash[..16];
        key[6] = (byte)((key[6] & 0x0F) | (OwnerKeyVersion << 4));
        key[8] = (byte)((key[8] & 0x3F) | 0x80);
        return new Guid(key, bigEndian: true);
    }
}

/// <summary>
/// Final review S2: the public form's <c>uploadKey</c> (the Storage <c>owner_id</c> of a visitor's upload) is a random v4 UUID by
/// contract. It is the version that tells it apart from a note's key (<see cref="SupportNoteUploads.OwnerKeyVersion"/>, v8): the
/// <c>attachments_busy</c> cap counts only v4 owners, and completing or filing with a key of any other version is
/// <c>support.public.attachment_invalid</c>, the same as an unknown one.
/// </summary>
public static class SupportPublicUploads
{
    /// <summary>The UUID version of every public upload key: <see cref="Guid.NewGuid"/>'s, RFC 9562 v4.</summary>
    public const int KeyVersion = 4;

    /// <summary>A fresh public upload key: random, so it cannot be guessed from the time like a v7 Storage id.</summary>
    public static Guid NewKey() => Guid.NewGuid();

    /// <summary>Whether <paramref name="key"/> can be a public upload key: not empty and v4.</summary>
    public static bool IsKey(Guid key) => key != Guid.Empty && key.Version == KeyVersion;
}
