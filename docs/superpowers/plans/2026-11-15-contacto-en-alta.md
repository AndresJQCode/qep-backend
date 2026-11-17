# «Contacto de» en el alta de cliente — plan (backend)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `POST /api/v1/tenants/{tenantId}/customers` acepta `companyId` opcional y crea el cliente ya vinculado como contacto, de forma atómica.

**Architecture:** Un campo nuevo al final de `CreateCustomerRequest` y `CreateCustomerCommand`. El handler, sólo con `CompanyId`, abre transacción, bloquea la fila de la empresa (`LockForCompanyLinkAsync`), la carga y aplica `Customer.LinkToCompany`, la misma regla del dominio que usa `SetCustomerCompanyHandler`. Sin `CompanyId` el camino es idéntico al de hoy.

**Tech Stack:** .NET 10, EF Core + Npgsql, xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-15-contacto-en-alta-design.md`

## Global Constraints

- Worktree: `qep-backend-worktrees/contacto-en-alta`, rama `feature/contacto-en-alta`. Merge a `develop`, nunca a `main`.
- Códigos, sin inventar otros: `customers.customer.company_not_found`, `customers.customer.company_inactive`, `customers.customer.company_is_contact`; todos 422, como responde hoy `PUT …/company`.
- Auditoría: un solo evento `customers.customer.created`; ningún `CustomerAuditActions.CompanyChanged` en el alta.
- `PUT /customers/{id}` no lee ni escribe `CompanyId` (sin cambios).
- Pruebas de integración filtradas por nombre mientras se itera; nunca dos corridas de integración a la vez en la máquina (mirar `Get-Process testhost, dotnet` antes).
- Commits convencionales sin atribución de IA.

## Review Focus

1. Documento repetido **con** `companyId`: debe seguir respondiendo 422 `identification_taken` con `existing`, no un 500 por transacción abortada. → prueba en Task 1.
2. Empresa inválida: el cliente **no** debe quedar creado (ni a medias sin empresa). → prueba en Task 1 (theory).
3. `companyId` de otro tenant: se lee como `company_not_found`, igual que uno inexistente. → cubierto por `FindAsync` con `TenantId`; caso `not_found` del theory.
4. Alta sin `companyId` (todos los llamadores actuales, incluido `CustomerProvisioning`): sin cambios. → prueba en Task 1 y suite del módulo al final.
5. Empresa fusionada: `LinkToCompany` responde `company_not_found`. Cubierto por la prueba de dominio existente (`CustomerCompanyTests`); no se duplica.

---

### Task 1: `companyId` en el alta

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomersDtos.cs` (`CreateCustomerRequest`, ~línea 281)
- Modify: `src/Modules/Customers/Modules.Customers.Application/CreateCustomer.cs` (comando y handler)
- Modify: `src/Modules/Customers/Modules.Customers.Api/CustomerEndpoints.cs:353-369` (pasa `request.CompanyId`)
- Modify: `src/Modules/Customers/Modules.Customers.Domain/Customer.cs:328-329` (comentario de `CompanyId`)
- Create: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerCreateWithCompanyApiTests.cs`

**Interfaces:**
- Produces (contrato HTTP que consume el frontend): cuerpo de `POST …/customers` con `companyId?: string (uuid) | null`; respuesta 201 con `company: { id, name, isComplete, isActive }` resuelto.
- `CreateCustomerRequest(..., IReadOnlyList<WithholdingAgentCityRequest>? WithholdingAgentCities = null, Guid? CompanyId = null)`
- `CreateCustomerCommand(..., IReadOnlyList<WithholdingAgentCityRequest>? WithholdingAgentCities = null, Guid? CompanyId = null)` — último parámetro, con default, para que `CustomerProvisioning.cs:417` compile sin tocarlo.

- [ ] **Step 1: Escribir las pruebas que fallan**

Archivo nuevo, mismo estilo que `CustomerCompanyApiTests` (un Postgres por prueba, `CustomersApiHarness`). El cuerpo es `NewCustomerBody(...)` más `companyId`; como `NewCustomerBody` devuelve un anónimo, armar el cuerpo con un helper local que copie sus campos y agregue `companyId` (o un `Dictionary<string, object?>`).

```csharp
[Fact] CreatingWithACompanyReturnsTheContactLinked
// company = CreateAsync("Verde Esencial", 2); POST con companyId = company
// Assert 201; body.company.id == company; body.company.name == "Verde Esencial";
// SELECT company_id FROM customers.customers WHERE id = <nuevo> == company

[Fact] CreatingWithoutACompanyLeavesItNull
// POST sin companyId → 201, body.company.ValueKind == Null

[Theory]
[InlineData("not_found", "customers.customer.company_not_found")]
[InlineData("inactive", "customers.customer.company_inactive")]
[InlineData("contact", "customers.customer.company_is_contact")]
EachRuleRejectsTheCreation(string scenario, string code)
// not_found: Guid.CreateVersion7(); inactive: POST …/{company}/deactivate; contact: company vinculada a un tercero por PUT …/company
// Assert 422 y body.code == code;
// SELECT count(*) FROM customers.customers WHERE identification_number = <el del POST> == 0

[Fact] ATakenDocumentStillNamesItsOwnerWithACompany
// crear "Laura Pérez" con documento X; POST otro cliente con el mismo documento X y companyId válido
// Assert 422, body.code == "customers.customer.identification_taken", body tiene "existing" con el id de Laura

[Fact] CreatingWithACompanyAuditsOnlyTheCreation
// outbox (platform.outbox_messages, como AuditRowsAsync de CustomerCompanyApiTests):
// action 'customers.customer.created' para el nuevo id == 1; action CustomerAuditActions.CompanyChanged == 0
```

- [ ] **Step 2: Correr y ver el RED**

Run: `dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerCreateWithCompanyApiTests" -- xUnit.MaxParallelThreads=4`
Expected: compila (el cuerpo es JSON); FALLAN `CreatingWithACompanyReturnsTheContactLinked` (company null), `EachRuleRejectsTheCreation` (201 en vez de 422). Pasan `CreatingWithoutACompanyLeavesItNull`, `ATakenDocument…` y `…AuditsOnlyTheCreation` (hoy el campo se ignora). Guardar la salida literal como evidencia.

- [ ] **Step 3: Implementar**

- Agregar `Guid? CompanyId = null` a request y comando; el endpoint pasa `request.CompanyId` por nombre.
- En `CreateCustomerHandler.HandleAsync`, después de las validaciones y **antes** de `cucGenerator.NextAsync` (para no quemar un número del consecutivo con una empresa inexistente, mismo criterio que clasificación y ciudad):
  - si `command.CompanyId is { } companyId`: `transaction = await unitOfWork.BeginTransactionAsync(ct)`; `await repository.LockForCompanyLinkAsync(command.TenantId, [companyId], ct)`; `company = await repository.FindAsync(command.TenantId, new CustomerId(companyId), ct) ?? throw new CustomersDomainException(CustomersErrorCodes.CompanyNotFound, "The company does not exist.")`.
  - Variable `ICustomersTransaction? transaction = null`, con `await using` o `try/finally` con `DisposeAsync` (patrón de `ImportCustomers.cs:280`).
- Después de `Customer.Create(...)` y de las retenciones, si hay empresa: `customer.LinkToCompany(company, hasContacts: false, now)`.
- Auditoría sin cambios (un `customers.customer.created`).
- En el `catch (CustomersDomainException e) when (e.Code == CustomerIdentificationTaken.Code)`: si hay transacción, `await transaction.DisposeAsync()` (deshace) y anularla **antes** de `CustomerIdentificationTaken.ResolveAsync`.
- Tras `SaveChangesAsync`, si hay transacción: `await transaction.CommitAsync(ct)`.
- Comentario de `Customer.CompanyId`: «Lo escriben el alta (opcional), `LinkToCompany`, `UnlinkFromCompany` y la fusión; editar un cliente nunca lo toca.»

- [ ] **Step 4: Correr y ver el GREEN**

Run: el mismo comando del Step 2.
Expected: 7 pruebas en verde (el theory cuenta 3). Guardar la salida literal.

- [ ] **Step 5: Regresión del módulo**

Run (uno a la vez, sin otra corrida de integración viva):
`dotnet test tests/Modules/Customers/Modules.Customers.IntegrationTests --filter "FullyQualifiedName~CustomerCompanyApiTests|FullyQualifiedName~CustomerWriteApiTests" -- xUnit.MaxParallelThreads=4`
`dotnet test tests/Modules/Customers/Modules.Customers.UnitTests`
`dotnet test tests/ArchitectureTests`
Expected: todo en verde. La suite completa del módulo corre una sola vez, antes del merge a `develop`.

- [ ] **Step 6: Commit**

```bash
git add src/Modules/Customers tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerCreateWithCompanyApiTests.cs
git commit -m "feat(customers): link the company when creating a customer"
```
