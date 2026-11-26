# Etiquetas en la importación de clientes — plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (native, un implementador; las reglas de presupuesto del
> workspace agrupan las tareas en bloques). Pasos con `- [ ]`.

**Goal:** una columna opcional `Etiquetas` en el Excel de clientes que suma etiquetas al cliente (creándolas si no existen).

**Architecture:** la columna entra por `CustomerImportColumns.Optional` como «Empresa»; el parseo del texto (`;`, recortes,
repetidos) y el formato tipo oración son funciones puras; `ImportCustomersHandler` decide las etiquetas por fila antes de escribir
(como `PlanCompanyLinksAsync`), crea las faltantes y agrega asignaciones dentro del mismo `SaveChanges`. Plantilla y exportación
escriben la columna.

**Tech Stack:** .NET 10, FluentValidation, ClosedXML, EF Core, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-17-etiquetas-en-importacion-de-clientes-design.md`

## Global Constraints

- Cabecera `Etiquetas` sin tilde; separador `;`; `Tag.NameMaxLength = 40`; `CustomerTagAssignment.MaxPerCustomer = 20`;
  `Tag.MaxPerTenant = 100`; color de las creadas `TagColor.Gray`.
- Códigos: `customers.import.row.tag_name_too_long`, `customers.import.row.tag_limit_per_customer`,
  `customers.import.row.tag_limit_reached`. Campo del error: `"Etiquetas"`.
- Agrega, nunca quita. Tipo oración sólo para las creadas. Casar sin distinguir mayúsculas (`OrdinalIgnoreCase`).
- TDD con evidencia; commits convencionales sin atribución; comentarios en español, tuteando.

## Review Focus

1. Celda `; ;` o sólo espacios → sin etiquetas, sin error (Tarea 1).
2. `vip; VIP` en la misma celda → una sola (Tarea 1).
3. Dos filas del archivo crean la misma etiqueta nueva → una sola etiqueta, sin `name_taken` (Tarea 3).
4. Fila con error de etiqueta y otra fila válida → sólo la primera queda fuera; la etiqueta nueva que sólo ella pedía no se crea
   (Tarea 3).
5. Cliente con 19 etiquetas + celda con 2 nuevas → `tag_limit_per_customer`, sin asignar ninguna (Tarea 3).

---

### Task 1: Parseo de la celda y formato tipo oración

**Files:**
- Create: `src/Modules/Customers/Modules.Customers.Domain/TagNameFormat.cs`
- Create: `src/Modules/Customers/Modules.Customers.Application/ExcelTagCell.cs`
- Modify: `src/Modules/Customers/Modules.Customers.Application/IExcelCustomerImporter.cs` (`Tags = "Etiquetas"`,
  `Optional = [Company, Tags]`, `ExcelCustomerRow.Tags = null` como último parámetro)
- Test: `tests/Modules/Customers/Modules.Customers.UnitTests/TagNameFormatTests.cs`, `…/ExcelTagCellTests.cs`

**Interfaces:**
- `public static class TagNameFormat { public static string SentenceCase(string name); }` — `"evento MACEL"` → `"Evento macel"`;
  recorta; respeta tildes (`"ñAPA"` → `"Ñapa"`); vacío → vacío.
- `public static class ExcelTagCell { public const char Separator = ';'; public static IReadOnlyList<string> Parse(string? cell); }` —
  recortados, sin vacíos, sin repetidos (OrdinalIgnoreCase, conserva el primero), en orden de aparición.

- [ ] Prueba RED: `SentenceCaseLowersTheRestAndUppersTheFirst` (`"evento MACEL"`→`"Evento macel"`, `"  vip "`→`"Vip"`, `"ñAPA"`→`"Ñapa"`, `""`→`""`).
- [ ] Prueba RED: `ParseSplitsTrimsAndDropsEmptyAndRepeated` (`"Evento macel; VIP ;; vip"` → `["Evento macel","VIP"]`; `null`/`"; ;"` → `[]`).
- [ ] `dotnet test …UnitTests --filter "FullyQualifiedName~TagNameFormatTests|FullyQualifiedName~ExcelTagCellTests"` → falla por compilación.
- [ ] Implementar las dos clases y los cambios de `IExcelCustomerImporter.cs`.
- [ ] Mismo comando → verde. `dotnet build` en verde (el parámetro nuevo tiene default).
- [ ] Commit: `feat(customers): parse the Etiquetas cell of the import`.

### Task 2: Leer la columna y validar la fila; plantilla y exportación la escriben

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Infrastructure/Excel/ClosedXmlCustomerImporter.cs:84-90` (leer `Tags` igual que `Company`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs` (`ExcelCustomerRowRules`: regla
  `tag_name_too_long` sobre `Tags` cuando algún nombre de `ExcelTagCell.Parse` pasa 40; regla `tag_limit_per_customer` cuando la celda
  trae más de 20)
- Modify: `…/Excel/ClosedXmlCustomerImportTemplateBuilder.cs:140` (`Write(CustomerImportColumns.Tags, row.Tags)`)
- Modify: `src/Modules/Customers/Modules.Customers.Application/ICustomerExportBuilder.cs` y `ExportCustomers.cs`,
  `…/Excel/ClosedXmlCustomerExportBuilder.cs`: parámetro nuevo `IReadOnlyDictionary<Guid, IReadOnlyList<string>> tagNamesByCustomer`,
  celda = `string.Join("; ", nombres)`.
- Modify: `src/Modules/Customers/Modules.Customers.Application/CustomerEngagementPorts.cs` (`ICustomerTagRepository`):
  `Task<IReadOnlyDictionary<CustomerId, IReadOnlyList<Tag>>> ListForCustomersAsync(Guid tenantId, IReadOnlyCollection<CustomerId> ids, CancellationToken ct)`;
  implementar en `…/Persistence/CustomerTagRepository.cs` (join asignaciones→tags, ordenado por nombre).
- Test: `ExcelCustomerRowRulesTests` (dos pruebas), `Excel/ClosedXmlCustomerImportTemplateBuilderTests` (la cabecera termina en
  `Empresa, Etiquetas`), `Excel/ClosedXmlCustomerExportBuilderTests` (celda `"Evento macel; VIP"`), y dobles de pruebas que
  implementen `ICustomerTagRepository`.

- [ ] Pruebas RED: `ATagNameLongerThanTheLimitFails`, `MoreThanTwentyTagsInACellFail`, la de cabecera y la de exportación.
- [ ] Correr filtradas → rojas.
- [ ] Implementar; `dotnet build`; corregir dobles.
- [ ] `dotnet test tests/Modules/Customers/Modules.Customers.UnitTests` → verde.
- [ ] Commit: `feat(customers): read, validate and write the Etiquetas column`.

### Task 3: Asignar y crear etiquetas en la importación

**Files:**
- Modify: `src/Modules/Customers/Modules.Customers.Application/ImportCustomers.cs`:
  - `CandidateRow.TagNames => ExcelTagCell.Parse(SourceRow.Tags)`.
  - `PlanTagsAsync(tenantId, writable, loaded, errors, ct)` después de `PlanCompanyLinksAsync` y antes de
    `CreateOrUpdateCustomersAsync`: devuelve las filas que siguen escribibles y un plan `(RowNumber, TagId[] existentes a agregar,
    string[] nombres a crear)`. Algoritmo: (1) sin celdas → salir; (2) `tags.ListAsync(tenantId)` → diccionario por nombre
    (OrdinalIgnoreCase); (3) `tags.ListForCustomersAsync` de las filas con `TargetId`; (4) por fila: resolver cada nombre al existente
    o a «por crear» (diccionario local por nombre, compartido entre filas); si `actuales ∪ pedidas` > 20 → `tag_limit_per_customer`
    y la fila sale de las escribibles; (5) `tags.CountAsync` + distintas por crear que alguna fila escribible usa; si pasa 100, las
    filas que necesitan una por crear fallan con `tag_limit_reached` en orden de fila hasta que entre.
  - `ApplyTags(plan, customersByRow, now)` después de `CreateOrUpdateCustomersAsync`: crea con
    `Tag.Create(TagId.New(), tenantId, TagNameFormat.SentenceCase(nombre), TagColor.Gray, now)`, `tags.Add`, audita
    `CustomerAuditActions.TagCreated`; `tags.AddAssignment(CustomerTagAssignment.Create(customerId, tagId, tenantId, subject, now))`
    sólo para las que el cliente no tiene.
- Test: `tests/Modules/Customers/Modules.Customers.IntegrationTests/CustomerStatusAndImportApiTests.cs` (+ `ExcelRowInput.Tags`,
  `BuildExcelUpload` escribe la columna cuando algún input la trae), con host propio (`StartDatabaseAsync`).

- [ ] Prueba RED `ImportCreatesMissingTagsInSentenceCaseAndKeepsExistingOnes`: etiqueta `VIP` creada por API; dos filas con
  `Tags: "evento MACEL; vip"`; tras importar, `GET /tags` del tenant tiene exactamente `Evento macel` y `VIP`; cada cliente tiene las dos.
- [ ] Prueba RED `ReimportingWithAnotherTagAddsWithoutRemoving`: fila con Cuc del cliente anterior y `Tags: "Mayorista"` → tres.
- [ ] Prueba RED `ARowThatWouldExceedTwentyTagsIsRejectedAndWritesNothing` (Review Focus 5): cliente con 19 etiquetas vía
  `PUT /{id}/tags`; fila de actualización con `"Nueva uno; Nueva dos"` → error `tag_limit_per_customer`, cliente sigue con 19,
  `GET /tags` no tiene `Nueva uno`.
- [ ] `dotnet build` de IntegrationTests → compila; correr las tres filtradas → rojas.
- [ ] Implementar; `dotnet test …UnitTests` verde; las tres de integración filtradas → verdes.
- [ ] Commit: `feat(customers): assign and create tags from the Excel import`.

### Task 4: Documentación y cierre

- [ ] README § importación de clientes: columna `Etiquetas`, separador, creación en tipo oración, «agrega, nunca quita», códigos.
- [ ] `CLAUDE.md` del repo, Gotchas: «la importación crea etiquetas en tipo oración y nunca las quita; un camino nuevo que escriba
  etiquetas desde un archivo pasa por `ExcelTagCell`/`TagNameFormat`».
- [ ] Clase completa `CustomerStatusAndImportApiTests` una vez (antes, comprobar que no haya otro `testhost`).
- [ ] Commit: `docs(customers): document the Etiquetas import column`.
