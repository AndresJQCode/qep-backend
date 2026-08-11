# `sdd/` de `qep-backend`

Specs y ledger de los slices que **se ejecutan en este repositorio**. Existe para que el dev
de backend trabaje a su ritmo sin commitear en el repo del frontend por cada cambio de
estado.

Establecido por `SDD-ADR-08` (2026-08-10).

## Qué es autoridad acá

| Qué | Dónde |
|---|---|
| Ledger de backend — **de acá arranca cada sesión de este repo** | [`02-plan/plan-maestro.md`](./02-plan/plan-maestro.md) |
| Specs de slices de backend | `03-modulos/<modulo>/slices/` |

**Un slice a la vez en este repositorio.** No en todo el proyecto: el frontend corre su
propio ciclo en paralelo, con su propio ledger.

## Qué se lee del otro repo, y no se copia

Todo lo que gobierna el producto vive en **`qep-frontend/sdd/`**:

| Qué | Dónde |
|---|---|
| Reglas de ejecución | `qep-frontend/sdd/AGENTS.md` |
| Método: plantillas, DoD, convenciones de ID | `qep-frontend/sdd/00-metodo/` |
| Registro de módulos, ADRs, estado de la base | `qep-frontend/sdd/01-contexto/` |
| Fichas y gates de módulo | `qep-frontend/sdd/03-modulos/<modulo>/` |
| Requisitos de negocio | `qep-frontend/sdd/04-requisitos/` |
| Ledger de frontend | `qep-frontend/sdd/02-plan/plan-maestro.md` |

Cambian poco, y por eso no se reparten: el método no tiene dos versiones y los requisitos son
del negocio, no de un repo. Commitear sobre `qep-frontend` desde este lado pasa **sólo** al
cerrar un gate, mover el estado de un módulo o registrar un ADR.

## La regla que sostiene todo esto

**Una fila de slice vive en exactamente un ledger: el de su repo.** Nada se duplica. Es la
única defensa contra dos estados que se contradigan — el costo que este proyecto ya pagó con
`SDD-CT-18`, y eso fue con un solo ledger.

Si un trabajo cruza los dos repos, **se parte en dos slices** con IDs propios desde el
planteo. No se escribe un spec que viva en un repo y ejecute en dos. `CAT-01` (frontend) y
`CAT-02` (backend) son ese patrón.

## Cómo se cita

Los enlaces relativos hacia `qep-frontend/` **no resuelven**: son dos repositorios
independientes con remoto propio. Se cita por ruta desde la raíz del workspace —
`qep-frontend/sdd/03-modulos/catalog/README.md`— igual que ya se citan los archivos de código
del otro repo.

## Qué NO va acá

- Specs de frontend.
- Copias del método, de los requisitos, del registro de módulos o de las fichas y gates. Si
  hay que corregir uno, se corrige en `qep-frontend/sdd/`, que es el original.
