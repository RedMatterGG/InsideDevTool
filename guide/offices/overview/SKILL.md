---
name: offices/overview
title: 7 Offices before the huddle (subscenes 64-71)
description: Ch7 offices: water guard, albino lift, huddle chamber.
chapter: 7
order: 700
subscenes: 64-71
related: [mechanics/mind_hats, mechanics/water, mechanics/huddle, secrets/orbs]
---
Status: places, savepoints and mechanisms read in the game code and scene data (game-code audit 2026-09-28, 07_PreHuddle); [inferred] marks steps assembled from that code. Nothing here was verified in play. Some of these places are crossed twice: first by the boy, later by the huddle on its way back.

## Savepoints

<!-- savepoints:begin -->
| sub:sp | place | savepoint object | x, y | flags |
|---|---|---|---|---|
| 64:0 | WaterGuardStorage | Savepoint1 | 3256, -743 | chapter |
| 65:0 | AlbinosOnElevator | Savepoint 1 | 3375, -757 | chapter |
| 65:1 | AlbinosOnElevator | SavepointHuddle_1 | 3396, -780 | huddle |
| 66:0 | HuddleApproach | Savepoint1 | 3447, -785 | chapter |
| 66:1 | HuddleApproach | SavepointHuddle_1 | 3494, -776 | huddle |
| 67:0 | Courtyard_Exterior | Savepoint | 3505, -778 |  |
| 68:0 | EducationRooms | SavepointBoy_1 | 3598, -771 | chapter |
| 68:1 | EducationRooms | SavepointHuddle_1 | 3598, -771 | huddle |
| 69:0 | waterOnCeilingAndSwing | Savepoint0 | 3677, -758 | chapter |
| 69:1 | waterOnCeilingAndSwing | Savepoint1 | 3676, -771 |  |
| 69:2 | waterOnCeilingAndSwing | Savepoint2 | 3654, -803 | chapter |
| 70 | HeadExplodeSecret | (no savepoint: logic or pass-through area) | | |
| 71:0 | HuddleChamber | Savepoint 01 | 3615, -770 | chapter |
<!-- savepoints:end -->

## Hint

### WaterGuardStorage (64)
A tall switch pulled with a group of hat workers changes the water level; a guard patrols and must not notice the boy below him. A button opens the gate to the albino lift.

### AlbinosOnElevator (65)
Albinos wake and follow; a lift with a button; at the bottom enough albinos must be present, then a gate lifted together. A secret orb on the ceiling (secrets/orbs). On the huddle's way back the lift falls.

### HuddleApproach (66)
Office workers flee; the huddle later smashes a slope and a chair here.

### Courtyard_Exterior (67)
A lever opens a glass security door to the courtyard. Later a guard closes it before the huddle, which tears it off.

### EducationRooms (68)
Classrooms with test subjects and a printer; a pipe to get off, a vent hatch to pull. The huddle breaks the glass here later.

### waterOnCeilingAndSwing (69)
A cleaner pod with a boom steered by the stick, a pool with two underwater locks, a suction lever and gate that can cut the boy in half.

### HeadExplodeSecret (70)
The room of the big orb: opens only with all 13 orbs (secrets/orbs).

### HuddleChamber (71)
The boy enters the huddle's sphere and frees it; from here on he controls the huddle (mechanics/huddle).

## Steps

### WaterGuardStorage (64)
- [code: SwitchTallRod] `pushedUp` empties / raises the water (1 m/s); `pulledDown` reverses it.
- [code: WaterGuardAlerter.cs:45-79] While the boy is in the guard's volume: if he is less than 15 m to the right of the guard, the allowed height above the guard grows from 2 m to 15 m; lower than that and closer to the guard than to `rightPos` (x1.2) raises the alarm once, and the guard strangles. [inferred] Stay high above him, or far to the right.
- [code] `ButtonNew on` opens the gate to the albino lift.

### AlbinosOnElevator (65)
- [code] `Elevator/ButtonNew pressed` moves the lift (speed 3); at the bottom `AlbinoCountTrigger` needs enough albinos (`enoughAlbinos`). The gate to the huddle is `OpenElevatorDoors2` (lift with the albinos).
- [code: AlbinosOnElevatorDeadlockFix] If the boy leaves more than 7 m right of the gate for 17.5 s (or 19 m), the albinos left behind are teleported to the gate.

### Courtyard_Exterior (67)
- [code: LeverBreaker] `right` opens the security door (lamps on); `left` closes it.

### EducationRooms (68)
- [code: AutoCrawlOffPipe] In the pipe zone, stick up > 0.25 makes the game climb the boy off the pipe by itself.
- [code: HuddleChamberVentHatch] A vent hatch to pull (1.1 s).

### waterOnCeilingAndSwing (69)
- [code: CleanerPodLogicControllerNEWEST] The stick steers the pod's boom; landing limits input 1 s.
- [code] `LeverBreaker right` = power off (fan off, lights out, water drips from the ceiling); `left` = power on. Two `UnderwaterLock success` unlock the giant hatch.
- [code: SuctionGate.cs:248-265] Touching the suction gate while it is 10 to 40 % open (and not fully opening) cuts the boy in half. [inferred] Pass it only when it is fully open.

### HuddleChamber (71)
- [code: HuddleFoetus.cs:534-580] The huddle hangs on 4 points (`UnderwaterHatch`); grab a point within 0.6 m with action and pull it off (2 jerks each in practice; `jerksToBreakFree` 4 is not read). After the third point the huddle pulls the boy in and the swap to the huddle happens.

## Stuck spots

- AlbinosOnElevator: albinos stuck above the gate get teleported after 17.5 s if you walk on (deadlock fix).
