# Pruebas en dos niveles

## Intención

Implementar una feature obliga a esperar el proyecto de integración completo de cada módulo tocado
antes de mergear a `develop` (Messaging: ~59 min, medido el 2026-10-31). El objetivo es que la espera
por feature sea de minutos y que la corrida completa ocurra sin nadie esperando.

## Objetivos, no objetivos

- **Objetivo:** un comando que corra el nivel rápido de lo que cambió la rama.
- **Objetivo:** la corrida completa de noche o antes de `main`, con un reporte que diga sobre qué commit corrió.
- **No objetivo:** acelerar los harness (host compartido, base plantilla), cambiar la paralelización,
  meter pruebas en Azure DevOps o tocar `qep-frontend`.
- **Anti-objetivo:** que el nivel rápido diga «verde» cuando algo tocado quedó sin probar y sin avisarlo.

## Restricciones

- Windows PowerShell 5.1; Docker Desktop para integración.
- Los user-secrets y `Seed:Enabled` se comportan como hoy.
- Nunca detener procesos ajenos.

## Enfoques

| Enfoque | Gana | Cuesta |
| --- | --- | --- |
| **A. Dos niveles (elegido)** | Espera por feature de minutos, cambio chico | Una regresión puede aparecer en la corrida completa, ya en `develop` |
| B. Acelerar los harness | La corrida completa también baja | Trabajo previo por módulo, riesgo de pruebas dependientes del orden, la espera sigue en decenas de minutos (estimado) |

El owner eligió A el 2026-11-24, aceptando que `develop` puede quedar roto hasta la corrida completa.
B queda como trabajo posterior.

## Diseño

### Nivel rápido: `scripts/test-feature.ps1`

Parámetros: `-Base` (por defecto `develop`), `-DryRun` (imprime el plan sin compilar ni probar) y `-NoBuild`.

Los archivos cambiados son los de `git diff` desde el `merge-base` con `-Base`, más lo que está sin
commitear y sin seguimiento. Un módulo está tocado si cambió algo en `src/Modules/<M>/` o `tests/Modules/<M>/`.

Corre, en este orden:

1. `dotnet build`.
2. `ArchitectureTests`, completo.
3. El proyecto de unitarias de cada módulo tocado.
4. `Bootstrapper.UnitTests`, si cambió `src/Bootstrapper/`, `src/Api/` o `tests/Bootstrapper/`. Levanta contenedores.
5. Integración filtrada a las clases de prueba nuevas o modificadas. El nombre sale de la declaración
   dentro del archivo (cubre las clases `partial`); si cambió una clase base abstracta, corren sus derivadas.

Avisos, al final y en el reporte, sin fallar la corrida:

- Módulo con código de producción cambiado y ninguna clase de integración en el filtro.
- Cambió un harness o un archivo de apoyo de un proyecto de integración: sus clases no se infieren.
- Cambio transversal (`src/BuildingBlocks/`, `src/Bootstrapper/`, `src/Api/`, `Directory.*.props`, o
  migraciones en más de un módulo): conviene correr la completa antes de seguir.

Sale con código distinto de cero si algo falla o si un proyecto no deja `.trx`.

Si hay un `testhost` o `Api.exe` corriendo: una compilación bloqueada por `Api.exe` detiene el script
con el motivo; un `testhost` ajeno sólo se avisa, porque el nivel rápido es una corrida chica.

### Nivel completo: `scripts/run-all-tests.ps1`

Sigue igual, con un agregado: el encabezado de `TestResults/report.md` lleva la rama, el SHA y si el
árbol tenía cambios sin commitear. Se corre de noche o antes de llevar `develop` a `main`. El README
documenta cómo programarlo en el Programador de tareas; registrarlo es un paso del owner.

`main` sólo recibe un SHA con reporte en verde.

### Reglas que cambian

- `CLAUDE.md` del repo: sección nueva con los dos niveles.
- `CLAUDE.md` del workspace (local, sin versionar): la regla «La suite completa corre sólo antes de
  mergear a `develop`» pasa a «antes de `main` o de noche»; mergear a `develop` pide el nivel rápido.
- `README.md` § Verificación: el comando nuevo.

### Pruebas del script

La selección (archivos cambiados → proyectos, filtro y avisos) es una función sin efectos, probada con
un script de PowerShell sin dependencias nuevas. Pester no se agrega.

## Queda a criterio de quien implementa

Estructura interna del script, formato de la salida y reutilización del lector de `.trx` entre los dos scripts.
