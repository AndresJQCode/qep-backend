# Withholdings from the billing company alone (preview) — backend design

Date: 2026-11-16. Frontend counterpart: `qep-frontend/docs/superpowers/specs/2026-11-16-withholdings-by-company-design.md`.

## Intent

When creating or editing a quotation or an order, the withholding cards (breakdown and «Retención de ICA» with
«Municipio de la operación») only appear after a bank account is picked. Withholdings depend on the billing
company (its country and municipality), never on the bank account. The owner wants them visible as soon as the
company is chosen.

Root cause: the previews only learn the company through `QuotationBillingAccountRequest`, which is all-or-nothing
(`CompanyId`, `BankName`, `AccountNumber`, `Currency`) and is verified against the company's accounts. The
frontend therefore sends `billingAccount: null` while the account is pending, and every withholding derivation
(`MunicipalContexts`, `OrderCustomerWithholdings`, `MunicipalWithholdingExplanation`, the composers) reads the
company from the billing account, so nothing is computed.

## Goals

- The four previews accept an optional `billingCompanyId` (Guid) and, when `billingAccount` is absent/null and
  `billingCompanyId` is present, compute withholdings exactly as if that company were billing:
  - `POST /quotations/preview` (`PreviewNewQuotationQuery`)
  - `POST /quotations/{id}/preview` (`PreviewQuotationQuery`)
  - `POST /orders/preview` (`PreviewOrderQuery`)
  - `POST /orders/{orderId}/preview` (`PreviewOrderEditsQuery`)
- The response paints what it paints with an account — withholding breakdown, municipal rows, `operationCity`,
  `billingCompanyCity` — but `billingAccount` stays `null`.
- Pricing currency is the one used today without an account (tenant default currency). Choosing only a company
  never changes the document currency.

## Non-goals

- Persisting a company without an account. Create, save-all, send, convert and order edits keep requiring the
  full account exactly as today; `billingCompanyId` is not added to any write command.
- Any change to `QuotationBillingAccountRequest`, the domain value object or the database.

## Anti-goals

- A preview that shows an account that was never chosen (an empty-number account in the response).
- Withholdings that differ from what the same document gets once the account is picked (same company ⇒ same rates).

## Rules

- `billingAccount` present ⇒ `billingCompanyId` is ignored (the account already names the company).
- `billingCompanyId` of a company that does not exist or belongs to another tenant ⇒ 422
  `quotation.billing.company_not_found`, the same code and message as an account with an unknown company.
- Both absent ⇒ today's behaviour, byte for byte.
- Explicit `operationCity` and withholding percentages / «Sin retención» in the body are honoured with a
  company-only preview the same way they are with an account.
- `PreviewOrderEdits`: absent `billingAccount` means «unchanged» today. With `billingCompanyId` it means «this
  company, account pending»: the preview resolves the order's withholdings against that company.

## Approaches

- **A — preview-only `billingCompanyId` (chosen by the owner).** No migration, no write path touched; covers the
  request because creating/saving still requires the account.
- B — company and account as separate document fields, persistable without an account. Rejected: migration,
  nullable columns, PDF/conversion changes, for documents that are blocked from being created without an
  account anyway.

## Decided while planning (2026-11-16)

The domain reads the company's country and municipality off `QuotationBillingAccount` in 20+ places
(`Quotation.cs`, `Order.cs`, `WithholdingCandidates`, `MunicipalContexts`, `OrderEdits`, the composers). Threading a
separate company context through all of them is a refactor the owner did not ask for. So a company-only preview
builds a **pending** billing account in memory: `QuotationBillingAccount { IsPending = true }` with the company's
`CompanyId`, `Country`, `CityId`, the tenant default `Currency`, and empty `BankName` / `AccountNumber`.

- It exists only inside a preview handler; previews never save, and no write command accepts `billingCompanyId`.
- It is never persisted (`IsPending` is ignored by EF) and never answered: the billing block of the response is
  `null` when the account is pending.
- Readiness rules treat it as absent: `CanBeConvertedToOrder` (and any other «has an account» check the response
  exposes) answers what it answers with `billingAccount: null`.

## Left to the builder

- The exact threading of `billingCompanyId` from the request to the resolver in each of the four paths.
- Tests: TDD, RED first. Integration tests filtered by name while iterating (workspace rules).
