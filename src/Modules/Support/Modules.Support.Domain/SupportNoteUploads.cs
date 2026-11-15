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

    /// <summary>The first 16 bytes of SHA-256 over <c>support-note|{ticketId:D}|{memberId:D}</c>. Deterministic and stable: it is
    /// what Storage stored as the owner, so changing it orphans every upload that is not in a note yet.</summary>
    public static Guid OwnerKey(SupportTicketId ticketId, Guid memberId)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture, $"{Purpose}|{ticketId.Value:D}|{memberId:D}");
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        return new Guid(hash[..16]);
    }
}
