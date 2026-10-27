---
name: lab/overview
title: 6 Lab and the girl (subscenes 49-63)
description: Ch6 lab: falling lift, girl, flush tunnel, guard.
chapter: 6
order: 600
subscenes: 49-63
related: [mechanics/water, mechanics/objects, safety/timing, secrets/orbs]
---
Status: places, savepoints and mechanisms read in the game code and scene data (game-code audit 2026-09-28, 06_Lab); [inferred] marks steps assembled from that code. Nothing here was verified in play. The lift ride and the flush tunnel follow the music clock (safety/timing).

## Savepoints

<!-- savepoints:begin -->
| sub:sp | place | savepoint object | x, y | flags |
|---|---|---|---|---|
| 49 | flushTunnel | (no savepoint: logic or pass-through area) | | |
| 50:0 | ElevatorRide | Savepoint 1 | 2480, -522 | chapter |
| 50:1 | ElevatorRide | Savepoint 2 | 2486, -527 |  |
| 51:0 | LabEntrance | Savepoint 1 | 2499, -669 | chapter |
| 52:0 | EntranceLanding | Savepoint | 2543, -666 |  |
| 53:0 | fixedJetCratesAndHatch | Savepoint 1 | 2566, -665 | chapter |
| 53:1 | fixedJetCratesAndHatch | Savepoint 2 | 2579, -654 | chapter |
| 54:0 | GirlInWater | Savepoint 1 | 2613, -648 |  |
| 54:1 | GirlInWater | Savepoint 2 | 2650, -655 | chapter |
| 55:0 | GirlExplore | Savepoint1 | 2715, -653 | chapter |
| 55:1 | GirlExplore | Savepoint2 | 2764, -647 |  |
| 56:0 | GirlInWaterPart2 | Savepoint 1 | 2792, -649 | chapter |
| 57:0 | diveToLab | Savepoint 1 | 2850, -651 | chapter |
| 58:0 | labEntranceFlush2 | Savepoint 2 | 2851, -760 | chapter |
| 58:1 | labEntranceFlush2 | Savepoint 7 | 2966, -758 | chapter |
| 59:0 | rotateTheShadow | Savepoint 0 | 3004, -755 | chapter |
| 59:1 | rotateTheShadow | Savepoint 1 | 3035, -755 |  |
| 59:2 | rotateTheShadow | Savepoint 2 | 3044, -753 |  |
| 59:3 | rotateTheShadow | Savepoint 3 | 3088, -747 | chapter |
| 60 | bigJumpInWater | (no savepoint: logic or pass-through area) | | |
| 61:0 | GetJetCrateFree | Savepoint 0 | 3132, -761 | chapter |
| 61:1 | GetJetCrateFree | Savepoint1BeforeJetIsFree | 3175, -773 |  |
| 61:2 | GetJetCrateFree | Savepoint2AfterJetIsFree | 3178, -773 | starts off |
| 61:3 | GetJetCrateFree | Savepoint3 | 3144, -767 | starts off |
| 61:4 | GetJetCrateFree | Savepoint 4 | 3137, -761 | starts off |
| 62:0 | JetCrateToCeilingWater | Savepoint1 | 3142, -743 | chapter |
| 62:1 | JetCrateToCeilingWater | Savepoint2 | 3167, -731 |  |
| 63:0 | CargoHall | Savepoint_1 | 3197, -725 | chapter |
| 63:1 | CargoHall | Savepoint_2_box | 3179, -725 | starts off |
| 63:2 | CargoHall | Savepoint_3_box | 3207, -725 | starts off |
<!-- savepoints:end -->

## Hint

### ElevatorRide (50)
A lift cage: a button inside starts the ride, which falls into water. Underwater doors to lift, extra-air zones.

### LabEntrance (51)
A gate with a return lever; the gate crushes whoever stands under it while it closes.

### EntranceLanding (52)
A door that closes 25 s after you trigger it (a small joke); no puzzle.

### fixedJetCratesAndHatch (53)
A button moves a hatch out / in; a jet crate can block it.

### GirlInWater (54)
The girl wakes and chases underwater. A button-driven chain conveyor; a gate that stops her.

### GirlExplore (55)
A vault door, falling platforms on ropes. A secret orb behind a vent (secrets/orbs).

### GirlInWaterPart2 (56)
The girl attacks again; a gate with a button stops her.

### diveToLab (57)
A falling platform; the girl drags the boy down into the lab (a scripted struggle). A secret orb underwater (secrets/orbs).

### labEntranceFlush2 (58)
The flush tunnel: a water stream that kills runs on a music-timed cycle; a hatch to open while the stream flows. Fish.

### rotateTheShadow (59)
A spotlight with a gun; a 12-turn valve rotates a blocker to make shadow. A secret orb (secrets/orbs).

### GetJetCrateFree (61)
Free a jet crate with a hatch button; some savepoints only turn on while the crate flies.

### JetCrateToCeilingWater (62)
Water on the ceiling; two underwater coils open a vault door. A secret lift and orb (secrets/orbs).

### CargoHall (63)
A guard watches while the boy moves a box across a hall with a child; then a water puzzle with an analog lever.

## Steps

### ElevatorRide (50)
- [code: ElevatorFall.cs:442-470] The button closes the gate; the fall starts only on the next shockwave blow (`blow` from the ForcePushReceiver) while the gate is closing. If the wave came first, nothing happens until the next one (6 s cycle).
- [code] `TriggerExtraAir 1`: +3.5 s air, 15.5 s dives; after the crash `TriggerDepleteAir 1` takes 5 s. Doors underwater: `OpenElevatorDoors` = stick up + action held.

### LabEntrance (51)
- [code: Lever + GateAvoid] `left` opens the gate, `right` closes it; a boy under the closing gate is crushed. [inferred] Open, pass, never stand under it while it closes.

### fixedJetCratesAndHatch (53)
- [code: Hatch FSM] `ButtonNew on` moves the hatch out, `off` moves it in; the crate in `Trigger 1` blocks it.

### GirlInWater (54)
- [code] The girl's speed is scaled down in zones (0.935 to 0.955); after an escape she pauses 0.8 s. `ButtonNew off` starts the chain (`ControlRope`); when it returns the button works again.
- [code: WaterGirlFishingCheat] With the button pushed, the girl inside `GirlCheatTrigger` and the boy jumping into the water in `GirlCheatJumpInWaterTrigger`, she is moved to `Target`. [inferred] The gate `3aGirlFishing_Gate` (`GateCrush`) stops her and gives +6 s of air: swim through it and let it close.

### GirlInWaterPart2 (56)
- [code] `StartGirlTrigger`: she attacks; for a moment the stick is limited (it grows back from 0 to 1). `GateCrush` + `ButtonNew` close the gate; `TriggerGirlStopHerself` makes her stop (+5 s air).

### diveToLab (57)
- [code: GirlDrownLogic] The struggle is driven by the stick (`limitInput`) until `boyAwake`; then the platform completes.

### labEntranceFlush2 (58)
- [code: AudioManagerFlushTunnel.cs:83-89] The stream opens at (music + 2) mod 16 s and closes 7 s later; `WaterFlushKiller` kills in the stream; `WaterStreamGrabHook` lets the boy hold on.
- [code: hatchSystem/Hatch] Grabbing the hatch handle while the stream flows pulls it open (-10 force). [inferred] Hold on during the stream, move when it stops; use realtime.

### rotateTheShadow (59)
- [code] Seen by the moving searchlight 0.5 s = shot (`RopeGunShootAndKillMainCharacter`).
- [code: SwitchValve 12 turns] The valve rotates `WheelBlocker` and the turbines by its percentage. [inferred] Turn the valve to put the blocker's shadow over your route, then move inside the shadow.

### GetJetCrateFree (61)
- [code] `ButtonNewPushHatch on / off` moves the push hatch; `jetCratePushed` breaks the crate free. `Savepoint3` and `Savepoint 4` only turn on while the jet crate is in their zones. Floors are fall-safe until `TriggerEnableFallDamage`.

### JetCrateToCeilingWater (62)
- [code: _PM VaultDoor] Both coils (`CoilLeft`, `CoilRight`, `UnderwaterCoil pulledOff`) are needed; then the vault opens and the stream starts. After being in the water, a fall into the `DelayedKiller` zone kills on landing.
- [code: DoubleJetCheat] A double jet boost helper exists (window 0.68 to 2 s).

### CargoHall (63)
- [code: GuardDetectionLogic] After the guard enters (3.5 s): 2 s grace, then the boy must be in cover, and the box's total horizontal speed above 0.7 alerts him. Alert = strangled. Without alarm the moment ends after 14.8 + 3 s.
- [inferred] Move the box only while the guard looks away and stay behind cover; then solve the water part with `LeverAnalogWater` (blocked while `TriggerCovered`).

## Stuck spots

- ElevatorRide: pressed the button and nothing moves: wait for the next wave (up to 6 s, realtime).
- labEntranceFlush2: in step mode the stream timing drifts; use realtime.
