# WMS: owner decisions on the task engine (Trello snLjp0i1)

Branch `feature/wms-task-engine-decisions`, from `develop` `e1c576dfb`. Spec: `docs/superpowers/specs/2026-11-26-wms-tareas-y-operaciones-design.md` §13.1 and §14.

Owner answers (2026-11-27, Trello snLjp0i1):

1. **M3.** `claim` by id only accepts a task assigned to the caller, or the caller's own claim that expired. New work only arrives through «Siguiente tarea». DA-09 is enforced on the server.
2. **B4.** No automatic cap. The board marks a task as «sin avance hace X minutos»; the supervisor decides whether to release or reassign it.
3. **B5.** The `TSK-yyyyMMdd-NNNN` daily number uses the tenant's time zone. Numbers already issued do not change.
4. **Two devices, option A.** Claiming (or «Siguiente tarea») from another device moves the task to that device with a new lease token. The first device gets 409.
5. **Clock tolerance.** 5 minutes approved (already implemented in T1 fixes; only verify and document).

## Task 1. M3: claim by id only for offered tasks

- `ClaimTaskHandler`: after loading and locking the task, and before `task.Claim`, reject unless
  - `task.Status == Assigned && task.AssignedMemberId == member`, or
  - `task.ClaimedByMemberId == member && task.LeaseToken is not null` (own claim swept by the lease sweeper, `Ready` or `Assigned`).
- The "already in progress and mine" path (renew) stays.
- A `Ready` task in the pool, or one whose expired claim belongs to someone else, is rejected with a new error code `wms.task.not_offered`, mapped to the same HTTP status family as `wms.task.assigned_to_other` (check `ApiExceptionHandler`/`WmsErrorCodes`).
- `NextTask` is not affected: it keeps claiming pool tasks through `LockNextCandidateAsync`.
- `GET /tasks` stays readable (read-only); not part of this decision.

## Task 2. Two devices, option A

- Domain: `RenewClaim(memberId, deviceId, now)`. Same device: as today (same token, no event, no version). Different device: `ClaimedDeviceId = new device`, new `LeaseToken`, renew lease, `Version++`, record a `Claimed` event with the new device. No new `TaskEventKind` (persisted by name; avoid a schema change).
- `ClaimTaskHandler` and `NextTaskHandler` pass the device.
- The first device then gets 409 `lease_lost` with `resumable: false` on confirm/short/count/heartbeat/unclaim: its token no longer matches. Verify `CheckLease` returns `Lost`, not `Resumable`, for the old token.
- Replace the T1 test that documented the old behavior.

## Task 3. B5: daily numbers in the tenant's time zone

- All callers of `IWarehouseTaskRepository.NextNumberAsync` compute `today` from `ITenantClock` (Tenancy.Application, already referenced by Wms.Application), for the same instant `now` the handler already uses.
- Approved by the owner (2026-11-27): apply the same to `ADJ-` (`IAdjustmentRepository.NextNumberAsync`) and `INC-` (`IIncidentRepository.NextNumberAsync`). The card said "como los ajustes", but in the code adjustments and incidents also cut the day in UTC.
- One helper in Wms.Application, not ten copies.

## Task 4. B4: «sin avance» on the board

- `TaskOperatorDto` gets `LastProgressAt` (latest `Claimed`, `LeaseResumed`, `LineConfirmed`, `LineShort` or `LineCancelled` event of the task) and `Stalled` (bool, computed on the server).
- `LastActivityAt` is not enough: the heartbeat renews it.
- `Stalled` is true when `now - LastProgressAt >= 15 minutes`. The threshold was approved by the owner (2026-11-27).
- One query for all tasks in progress, not one per task.
- Additive BFF fields. Document the reason in the DTO.

## Execution

- One implementer, TDD, one commit per task.
- RED by build failure needs no containers.
- Integration filtered by test name while iterating.
- Then `test-feature.ps1` and one final review.
