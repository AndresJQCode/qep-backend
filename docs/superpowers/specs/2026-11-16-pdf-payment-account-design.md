# PDF: show or hide the payment account — backend design

Date: 2026-11-16. Companion: `qep-frontend/docs/superpowers/specs/2026-11-16-pdf-payment-account-design.md`.
Builds on `2026-11-16-pdf-content-design.md`.

## Intent

Not every tenant wants its quotation or order PDF to say where to pay. Today, whenever a document has a
billing account, `quotation.typ` always prints «Cuenta para el pago: {company}», the bank and
«Número: {account} ({currency})». The tenant must be able to turn that off, per document type, from
`/settings/pdf-content`.

## Goals

- A per-tenant setting `PaymentAccount { Enabled, InQuotation, InOrder }`, read and written through the same
  `GET`/`PUT` of the PDF content, with the same semantics as a section's flags: it prints on a document when
  `Enabled && (isOrder ? InOrder : InQuotation)`.
- When it does not print, the PDF omits exactly the three account lines (company, bank, number).
- The «Aviso de pago» section stops depending on the billing account (owner, 2026-11-16).

## Non-goals

- Hiding the issuer header (company name, tax id, address, phone). It also comes from the billing account,
  but it identifies who issues the document, not where to pay. `hay-emisor` stays as it is.
- Hiding «Forma de pago» or «Contraentrega».
- Any text attached to the account block.

## Decisions

1. **Default is off** (owner, 2026-11-16: "todos los bloques inician apagados para todos los tenants menos
   Origen Botánico"). A tenant without a row reads `Enabled = false, InQuotation = true, InOrder = true`, like
   `PdfSection.Empty`. **Visible effect at deploy:** every tenant except Origen Botánico stops printing the
   account until it turns it on.
2. **Origen Botánico** gets a seeded row `enabled = true, in_quotation = true, in_order = true` (slug
   `origen-botanico`, same guard as `AddPdfSections`).
3. **Storage: own table** `quotations.pdf_payment_account` (`tenant_id uuid PK`, `enabled`, `in_quotation`,
   `in_order` booleans, `updated_at timestamptz`). Rejected: a fourth key in `pdf_sections` (breaks "an
   enabled section needs text" and the empty-row default) and columns in `quotations/settings` (different
   endpoint from the page).
4. **Payment notice is independent.** `QuotationPdfSectionsBuilder` drops the `BillingAccount is null ? null`
   guard (`QuotationPdfSections.cs:44`): the notice prints by its own flags only. In `quotation.typ` it moves
   out of the payment box `#if`, same position: under the box when there is one, alone otherwise.
   Visible effect: a tenant with the notice on now sees it on documents without an account too.

## Changes

- **Domain** — `PdfPaymentAccount(bool Enabled, bool InQuotation, bool InOrder)` with `PrintsOn(bool isOrder)`
  and `Empty` (off). `PdfContent` gains `PaymentAccount`; `PdfContent.Empty` and `Create` carry it.
- **Application** — DTO and validator: `paymentAccount` is required in the `PUT` body (`{ enabled, inQuotation,
  inOrder }`) and returned by `GET`/`PUT`. The PDF data gains a boolean `showPaymentAccount` computed with
  `PrintsOn`. `QuotationPdfSectionsBuilder.Fingerprint` includes the flag that applies to the document kind, so
  a cached quotation PDF regenerates when it changes.
- **Infrastructure** — table + migration with seed; `PdfContentStore` reads the row (absent → `Empty`) and
  upserts it with `INSERT … ON CONFLICT (tenant_id)` inside the handler transaction, like the sections.
  `quotation.typ`: the three account lines print only with `data.showPaymentAccount`; the box condition becomes
  `paymentMethod != none or cashOnDelivery or (billingAccount != none and showPaymentAccount)`.
- **Audit** — the existing PDF content audit event covers the new field (it serializes the saved content).

## Tests (TDD)

- Domain: `PrintsOn` matrix; `Empty` is off.
- Builder: notice prints without billing account; fingerprint changes when the account flag for that kind
  changes and not when the other kind's flag changes.
- Store/integration: no row → off; save then read round-trip; migration seeds Origen Botánico only.
- Endpoint: `PUT` without `paymentAccount` → 422; `GET` returns it.
- PDF data: `showPaymentAccount` true/false per kind.

## Deploy

Backend first. The frontend reads an absent `paymentAccount` (old backend) as off.

## Left to the builder

Exact DTO/validator names, migration name, and test file placement, following the existing PDF content code.
