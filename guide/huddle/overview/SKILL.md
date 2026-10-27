---
name: huddle/overview
title: 8 The huddle (subscenes 72-82)
description: Ch8 as the huddle: smashing through to the end.
chapter: 8
order: 800
subscenes: 72-82
related: [mechanics/huddle, mechanics/water]
---
Status: places, savepoints and mechanisms read in the game code and scene data (game-code audit 2026-09-28, 08_Huddle); [inferred] marks steps assembled from that code. Nothing here was verified in play. The body is the huddle in this chapter (held buttons, speed smashes).

## Savepoints

<!-- savepoints:begin -->
| sub:sp | place | savepoint object | x, y | flags |
|---|---|---|---|---|
| 72:0 | HuddleVentCollapse | Savepoint_1 | 3381, -847 | chapter, huddle |
| 73:0 | huddleCafeteria | Savepoint 1 | 3436, -842 | huddle |
| 74:0 | huddleCeoFallAndDogs | Savepoint Splat | 3531, -880 | chapter, huddle |
| 74:1 | huddleCeoFallAndDogs | Savepoint No Splat | 3531, -880 | huddle, starts off |
| 75 | huddleStickAndLift | (no savepoint: logic or pass-through area) | | |
| 76:0 | huddleGarbageRoom | Savepoint_1 | 3621, -888 | chapter, huddle |
| 77:0 | huddleAndOven | Savepoint_5 | 3656, -912 | chapter, huddle |
| 78:0 | huddleAndPendulum | Savepoint_1 | 3722, -910 | chapter, huddle |
| 79:0 | huddleJetCrateAndHelper | Savepoint | 3782, -908 | chapter, huddle |
| 80:0 | huddleTrap | Savepoint 1 | 3834, -908 | chapter, huddle |
| 80:1 | huddleTrap | Savepoint 2 | 3909, -890 | chapter, huddle |
| 81 | happyHuddle | (no savepoint: logic or pass-through area) | | |
| 82 | huddleNorway | (no savepoint: logic or pass-through area) | | |
<!-- savepoints:end -->

## Hint

### HuddleVentCollapse (72)
The huddle falls down a shaft, tears out a big server, breaks windows and the diorama walls.

### huddleCafeteria (73)
Tables and sofas break when hit fast; the big railing gives way and the huddle falls off the balcony.

### huddleCeoFallAndDogs (74)
The CEO office: smash the wall fast; the CEO runs to the window. Then an industrial room with a ceiling button, a balance beam and a cargo lift; dogs here cannot hurt the huddle.

### huddleGarbageRoom (76)
A worker opens a door for the huddle at a console, but only while the huddle stays back.

### huddleAndOven (77)
A burning crate, water curtains and sprinklers; tear the oven door off; a button under a hood.

### huddleAndPendulum (78)
A scientist with a hook and a pendulum (cleaner pod) that breaks a wall.

### huddleJetCrateAndHelper (79)
A helper and a jet crate; a button on the ceiling opens a gate.

### huddleTrap (80)
A jet crate on a rope teases the huddle into a trap tank; escape through two plates.

### happyHuddle (81), huddleNorway (82)
Boards that collapse, the wall to the outside, the slope down to the shore and the end credits. No savepoint after huddleTrap.

## Steps

### HuddleVentCollapse (72)
- [code] `BigAssServerSuperBakedV2` (`HuddlePullOffAnimatedObject`): grab and pull until `pullFinished`. Furniture: x speed > 2.5 smashes, slower only moves it.

### huddleCafeteria (73)
- [code] Tables: x speed > 3 smash (> 1 moves). Railing: speed >= 2.25 at `Trigger Crash`, or `Trigger ForceCrash` (stick forced right 3 s) drops the huddle from the balcony.
- [code: _PM RailingCrash] During the crash `BoySetInput` holds the stick at (1, 0) for 3 s: `observe.body.stick_forced` is true and `forced_by` names it. For a filmed run rehearse with `input_track` and play it back in realtime (safety/timing).

### huddleCeoFallAndDogs (74)
- [code: HuddleFallTest.cs:62-66] The wall breaks when the huddle enters `_WallPreSmashTrigger` with x speed > 5.5; slower at the door: below 2.8 it only cracks. After the smash `HuddleFallTest` keeps the stick right for 2 s (`observe.body.stick_forced`).
- [code: CEO] After the wall, the CEO stands at the window and flees after 13 s; 4.5 s into his flight he is safe and `Savepoint No Splat` is used; breaking the window before that drags him out (`Savepoint Splat`). Either way the chapter goes on [inferred].
- [code] Industrial room: a box or the huddle on `CeilingButton` opens `Block1/Block2` and lets dogs in (harmless to the huddle). `BallanceBeamPushAssist`: stick up >= 0.65 with action pushes the beam up; the beam's pressure plate runs the cargo lift.

### huddleGarbageRoom (76)
- [code: HuddlePasswordDude.cs:226-356] The worker walks to a console and opens the door after 5 s, but only while the huddle's x is left of 3622.9; closer and he gets scared and flees. [inferred] Wait left of that point until the door opens.

### huddleAndOven (77)
- [code: FireStone_New] The crate catches fire in the flames and goes out in water. Carrying the burning crate into `SpringlerTrigger` starts the sprinklers; into `TriggerFireDoor` sets the fire door off (alarm, gate power).
- [code] `VaultDoorHuddleRipOff` tears off the oven door; `HuddleOvenGrab` aligns the crate for a throw into the oven when the stick is left; `TriggerCount` under the hood counts to open a door.

### huddleAndPendulum (78)
- [code] Grab the pendulum base (`HuddlePendulGrabHack`), swing it; when the hook attaches (`HookAttached`) the scientist pulls and the wall breaks.

### huddleJetCrateAndHelper (79)
- [code] The helper arrives (trigger or 8 s), throws the jet crate back (`HuddleJetCrateThrowBack`); the crate hitting the ceiling button opens the gate; the huddle standing in the gate keeps it from closing.

### huddleTrap (80)
- [code: huddleTeasing.cs:173-191] The hatch opens when the huddle stands at the target (|dx| < 1.25, speed < 1) for 5 s, or holds the stick (> 0.5) for 3 s (the timer jumps to 4.75).
- [code: HuddlePlateEscape plateCount 2] In the tank, two connected plates (`RightSide/HuddlePlateA`, `HuddlePlateC`) free the hatch; the other 12 plates do nothing. Input is limited to about half swim speed inside.

### huddleNorway (82)
- [code] The wall: x speed > 6.5 (after a second hit > 5) breaks it. On the slope the game drives the stick (0.8, then 0.25, then 0). `TriggerCredits` starts the credits; after them a face button on the real controller restarts (agent input cannot press it: use the dev tools or ask the user).

## Stuck spots

- huddleGarbageRoom: rushing to the worker makes him flee; stay back.
- huddleTrap: holding the stick toward the target shortens the tease (3 s instead of 5 s).
