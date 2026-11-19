# Distribuidor: país y tipo de documento (backend)

Fecha: 2026-11-18. Cambio chico, un módulo (Wholesale) más un adaptador en Bootstrapper.
Spec par del frontend: `qep-frontend/docs/superpowers/specs/2026-11-18-distribuidor-pais-documento-design.md`.
**Se despliega primero el backend.**

## Intención

Al invitar a un distribuidor, el mayorista elige el país del distribuidor y, según ese país, el
tipo de su documento. Hoy Wholesale sólo guarda `IdentificationNumber` y el tipo está fijo en
`"NIT"` (`WholesaleCustomerProvisioning.IdentificationType`), así que un distribuidor de otro país
no se puede crear bien.

## Metas

- `Distributor` guarda `Country` e `IdentificationType`.
- El invite los recibe y valida el tipo contra el país con `DocumentTypesByCountry.IsAllowed`.
- El cliente que se crea para el distribuidor usa el tipo recibido, no `"NIT"`.
- `GET …/distributors/options` devuelve `defaultCountry`: el país de la configuración regional del
  tenant (`CO` si no hay fila), para que el navegador no lo deduzca.

## No metas

- No se amplía lo que admite un **proveedor**. Esa restricción (`Eligible`, NIT/CC/CE/PASAPORTE) es
  de la empresa facturadora del mayorista, no del distribuidor invitado.
- País y tipo no se editan después de invitar (igual que el número hoy).
- Precargar país y tipo cuando el distribuidor crea su propia empresa al aceptar queda fuera:
  se revisa aparte.

## Diseño

- **Dominio:** `Distributor.Country` (código ISO, mayúsculas, recortado) y
  `Distributor.IdentificationType` (código de cable de los siete de `DocumentTypesByCountry.All`).
- **Persistencia:** migración con las dos columnas. Existentes: `CO` y `NIT`. El índice único pasa
  de `(TenantId, IdentificationNumber)` a `(TenantId, IdentificationType, IdentificationNumber)`:
  una CC y un NIT pueden compartir dígitos. `IdentificationExistsAsync` recibe el tipo.
- **Invite:** `InviteDistributor` y el request de `WholesaleEndpoints` suman `Country` y
  `IdentificationType`. `DistributorWriteRules` exige ambos y que el tipo sea válido para el país;
  si no, 422 por validador, con el mapa `errors` (`Country`, `IdentificationType`). Un país vacío no
  cae en un default.
- **Respuestas:** `DistributorSummary` y el detalle devuelven `country` e `identificationType`.
- **Provisioning:** `WholesaleCustomerProvisioning` recibe el tipo y lo parsea con el parser de
  Clientes (un valor desconocido falla, no cae en NIT). La rama `IdentificationTaken` compara por
  el tipo recibido.
- **Opciones:** `DistributorOptionsDto.DefaultCountry`, de `TenantRegionalSettings.Country`.

## Aproximaciones consideradas

1. **Guardar país y tipo en `Distributor` (elegida):** el selector de tipo y la tabla son fieles al
   dato, y el cliente nace con el tipo correcto.
2. Guardar sólo el tipo: al mostrar o editar no se sabe qué lista corresponde. Descartada.
3. No guardar nada, sólo filtrar en pantalla: dato decorativo, el cliente seguiría naciendo NIT.
   Descartada.

## Pruebas (TDD, RED primero)

- Unitarias de `DistributorWriteRules`: tipo inválido para el país → 422; país vacío → 422.
- Unitarias de `InviteDistributor`: la unicidad es por `(tipo, número)`; mismo número con otro tipo
  no choca.
- Provisioning: el cliente se crea con el tipo recibido; un tipo desconocido falla.
- Opciones: `defaultCountry` sale de la configuración regional y cae en `CO` sin fila.
- Migración: las filas existentes quedan en `CO`/`NIT`.
- RED de compilación sin contenedores; la integración corre en GREEN y filtrada por nombre.

## Para el implementador

- `qep-backend/docs/estado.md` se actualiza en `develop`, al mergear, no en la rama de feature.
- Hay cambios ajenos sin commitear en el checkout (`ClaudeModelClient.cs`, `SiigoApiClientTests.cs`):
  trabajar en un worktree y no tocarlos.
- Los códigos de error existentes (`wholesale.distributor.identification_taken`) no cambian.
