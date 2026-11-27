---
paths:
  - "src/Modules/Hr/**"
  - "src/Bootstrapper/HrDocumentReferences.cs"
  - "tests/Modules/Hr/**"
  - "src/Modules/Storage/Modules.Storage.Domain/FileResource.cs"
  - "src/Modules/Storage/Modules.Storage.Infrastructure/HrDocuments/**"
---

# RRHH (empleados, contratos y sus PDF)

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **Una tabla de Hr que guarde un `document_file_id` entra en `IHrDocumentUsage` y en `HrDocumentReferencesTests`, o Storage
  purga un PDF que todavía se usa.** Al eliminar un registro de cargo y lugar, Hr publica `hr.employee.documents-released.v1` y
  Storage (`storage.hr-document-release`) borra el archivo de R2 si `IHrDocumentReferences` dice que nadie lo nombra. Esa respuesta
  sale de `HrDocumentUsage` (`Modules.Hr.Infrastructure`), que hoy mira cinco tablas: `employment_contracts`, `salary_records`,
  `assignment_records`, `contract_renewals` y `medical_exams`. Una sexta tabla que no se sume ahí compila y pasa todas las
  pruebas de su feature, y el PDF que guarda se purga en cuanto otro registro suelte el mismo `fileId`. La purga es inmediata y
  no tiene gracia: lo borrado no se recupera. `HrDocumentReferencesTests` (composición, con un PDF en cada tabla) tiene que
  sumar la tabla nueva. Spec [`2026-11-26-hr-editar-eliminar-cargo-lugar-design.md`](../../docs/superpowers/specs/2026-11-26-hr-editar-eliminar-cargo-lugar-design.md),
  «Referencias».
- **Un `HrDocument` sólo pasa a `Deleted` por la marca de `HrDocumentPurge`; `FileResource.SoftDelete` lo rechaza
  (`storage.file.invalid_state`).** Para un `HrDocument`, `Deleted` significa «marcado para purgar»: la purga lo confirma antes
  de la segunda pregunta y de R2, y un reintento que lo encuentra así retoma la purga. Por eso un camino nuevo que suelte un PDF
  de Hr (reemplazarlo, quitarlo, borrar un examen médico; <https://trello.com/c/HguYDih9>) no lo borra por su cuenta: publica
  `hr.employee.documents-released.v1` con `{ tenantId, fileIds }` en la misma transacción (`IHrDocumentReleases`) y deja que
  Storage decida. Hoy sólo lo publica `RemoveAssignment`; los demás caminos dejan el archivo en R2.
- **Si la referencia aparece después de la marca y el objeto ya no está en R2, el archivo queda `Purged` con un `Error` en el
  log** (decisión del owner): devolverlo a `Available` dejaría una fila sin objeto. Es la ventana residual del spec; ese `Error`
  («used by Hr but its object was already deleted from R2») es la única señal de un registro que nombra un PDF purgado.
