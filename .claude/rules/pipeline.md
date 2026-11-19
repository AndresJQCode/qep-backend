---
paths:
  - "src/Modules/Pipeline/**"
  - "tests/Modules/Pipeline/**"
---

# Pipeline (embudo de ventas)

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **El embudo de un tenant nace en su primer acceso, no al prender el módulo.** Tenancy no publica nada al
  activar una capacidad; `PipelineProvisioner` crea el embudo con `INSERT … ON CONFLICT (tenant_id) DO NOTHING`.
  Una consulta directa a `pipeline.stages` de un tenant que nunca abrió el tablero devuelve cero filas: no es
  un error.
- **Dueño de un negocio: la API exige membresía activa, el evento no.** Crear un negocio por la API con un
  responsable que no es miembro activo da 422 `pipeline.deal.owner_not_member`; el negocio que crea un evento
  de cotización conserva al asesor de la cotización aunque ya esté inactivo: la cotización existe y su
  asesor es un hecho, no una elección del usuario. No unifiques las dos reglas.
- **El orden del historial lo da el ordinal del dominio, no `occurred_at`.** `DealStageHistoryEntry.Ordinal` lo asigna
  el agregado (único por `(tenant_id, deal_id)`); dos cambios en el mismo instante se ordenan por él. Y la carrera
  del triage se cierra con el índice único `IX_triage_decisions_conversation_inbound`, que
  `PipelineUnitOfWork` traduce a `pipeline.triage.already_decided` **por nombre de índice**: no la muevas a una
  comprobación previa en el handler, que dejaría dos negocios.
