using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>
/// The allowlist of the legal documents (spec 2026-11-15 §6.2, plan L2 and L3). <b>Every legal HTML that is stored or served goes
/// through this port</b>: when publishing (the output is what is stored), when serving <c>GET /form</c> (what was read is sanitized
/// again) and when writing the snapshot of an acceptance. The implementation lives in Infrastructure, on <c>HtmlSanitizer</c>:
/// Application does not reference the library (<c>SupportLayerTests</c>).
/// <para>
/// The output is a fixed point: sanitizing it again gives the same bytes. Markup too large, too deeply nested or that does not settle
/// throws <c>FluentValidation.ValidationException</c> on <c>ContentHtml</c> (<c>422 validation.failed</c>). What was stored already
/// went through it, so serving or snapshotting a stored document never throws.
/// </para>
/// </summary>
public interface ISupportHtmlSanitizer
{
    SanitizedHtml Sanitize(string html);
}
