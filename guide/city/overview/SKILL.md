---
name: city/overview
title: 3 City (subscenes 12-22)
description: Ch3 city: hats, sentries, lift, lineup march.
chapter: 3
order: 300
subscenes: 12-22
related: [mechanics/mind_hats, safety/timing, mechanics/death, secrets/orbs]
---
Status: places, savepoints and mechanisms read in the game code and scene data (game-code audit 2026-09-28, 03_City); [inferred] marks steps assembled from that code. Nothing here was verified in play.

## Savepoints

<!-- savepoints:begin -->
| sub:sp | place | savepoint object | x, y | flags |
|---|---|---|---|---|
| 12:0 | factoryWithCorpses | Savepoint | 556, -224 | chapter |
| 13:0 | 3D_crane | Savepoint 0 | 598, -224 |  |
| 13:1 | 3D_crane | Savepoint 1 | 597, -220 |  |
| 14:0 | pushDownSafe | Savepoint | 579, -215 | chapter |
| 15:0 | mindhatBasement | Savepoint_1 | 573, -232 | chapter |
| 15:1 | mindhatBasement | Savepoint_2 | 614, -234 | starts off |
| 16 | Ruined_Structure | (no savepoint: logic or pass-through area) | | |
| 17:0 | rooftops2New | Savepoint 1 | 680, -229 | chapter |
| 17:1 | rooftops2New | Savepoint 2 | 730, -231 |  |
| 17:2 | rooftops2New | Savepoint 3 | 745, -242 |  |
| 18:0 | Old_Sentry_Room | Savepoint0 | 777, -261 | chapter, starts off |
| 18:1 | Old_Sentry_Room | Savepoint1 | 811, -266 | chapter |
| 18:2 | Old_Sentry_Room | Savepoint2 | 814, -266 | starts off |
| 18:3 | Old_Sentry_Room | Savepoint3 | 854, -258 | chapter |
| 19:0 | lineup | lineup_savepoint_01 | 876, -264 | checkpoint-only |
| 19:1 | lineup | lineup_savepoint_02 | 898, -264 | checkpoint-only |
| 19:2 | lineup | lineup_savepoint_03 | 939, -264 | checkpoint-only |
| 20:0 | lineupHulvChase | lineup_savepoint_04 | 984, -273 |  |
| 21:0 | mindHatWagon | Savepoint01 | 1027, -272 | chapter |
| 21:1 | mindHatWagon | Savepoint02 | 1040, -271 |  |
| 21:2 | mindHatWagon | Savepoint03 | 1084, -271 |  |
| 21:3 | mindHatWagon | Savepoint04 | 1112, -271 |  |
| 22:0 | mindHatJumpIntoWater | Savepoint | 1121, -300 | chapter, starts off |
<!-- savepoints:end -->

## Hint

### factoryWithCorpses (12)
A window that opens; nothing blocks the way. Waiting 7 s behind the wall with the window open brings a bird to the sill (a small reward, no progress).

### 3D_crane (13)
A crane moved with an analog lever. Falling from it kills.

### pushDownSafe (14)
A safe hangs on a rope above a floor that breaks on impact.

### mindhatBasement (15)
The first mind-control hat. Two pressure plates open a gate once both are pressed. A secret orb sits behind a vent nearby (secrets/orbs).

### Ruined_Structure (16)
No savepoint; a pass-through area.

### rooftops2New (17)
Rooftops: a shopping cart that kills the boy if he walks into its trap, a fire escape, a bending board that breaks. A secret orb and a tape recorder with a code (secrets/orbs).

### Old_Sentry_Room (18)
An alley with boards to tear off, then a hall with sentry guns and a spotlight, and a lift. Power is the key: the lever that powers the lift also wakes the guns.

### lineup (19)
The boy joins a column of marching figures. Moving out of time with them is spotted. The march and the drops through the floors follow the music: play this in realtime (safety/timing).

### lineupHulvChase (20)
A dog chase that ends at a window.

### mindHatWagon (21)
A hat on a wagon; two gates opened by two pressure plates, one for the boy, one for a dude.

### mindHatJumpIntoWater (22)
Dudes in the hat pull a lever that opens a water gate. Do not stay in the hat: a guard fetches a sentry gun. A raft and a hatch underwater follow; the closing water gate crushes.

## Steps

### mindhatBasement (15)
- [code: !SensorLogic] The gate opens after 2 presses on the plates (`MindHatReceiverPressurePlate` x2, a counter). [inferred] Put a hat dude on one plate and keep him there while the second plate is pressed.

### rooftops2New (17)
- [code: _DeathByShoppingCart] Entering `Trigger DeathByShoppingCart` plays one of three cart deaths depending on facing and position. [inferred] Do not walk into the cart's zone; `Trigger ShakeAnyway` only shakes it.
- [code] `FreezeInputTrigger` freezes input for 1 s and `Trigger Go Left` forces the stick left for 2.33 s: scripted, wait it out.

### Old_Sentry_Room (18)
- [code: LeverBreaker] Pulling the lever down (`left`) powers the hall: both sentry guns wake, lights flicker, the cover theme starts; pushing it back (`right`) turns everything off again.
- [code: SpotLightDetector, RopeGun] The guns shoot when the boy is visible and not in cover; `InCoverNotInCover` tracks the cover walls.
- [code: MovementAndSoundLogic] The lift works when power is on; if the lift door was already open when power came on, the solved music plays and `Savepoint2` turns on. Then the powered button `ButtonNewPushON` starts the lift and stops gun 2.
- [inferred] Open the lift door first (grab and lift it: `OpenElevatorDoors`), get to cover, pull the power lever, move cover to cover to the lift, press the button.

### lineup (19)
- [code: _SleepWalkerMarchLogic] The column starts and stops on music times (3.9, 7.9, 12.8, 8.2, 4.3, 12.5 s). `_BoyZombieLogic` scales the boy's stick to the march (left only 0.37).
- [code: ZombieMonitorDetection] Detection: warning within 0.85 m out of line, kill at 1.6 m, 0.5 s delay, warnings last up to 4 s; moving while the column stands is penalised. `_JumpAndTurnEvents` jumps and turns on music times (8.5, 4.3, 5.6 s).
- [inferred] In realtime: move only while the column moves, stop when it stops, jump and turn when the figures do.
- [code: FallThroughTriggerAndLogic] At the falling floors the boy hangs and drops when the music loop is between 11 and 14 s (no timeout).
- [code: GuardDetectionLogic] In the last room with the guard: during the first second only a jump or facing left counts as moving; after it, stick x > 0.35 too. Moving starts the dog chase (the chase stick is boosted to x1.15).

### lineupHulvChase (20)
- [code] The chase first slows the boy (stick 0.75 to 1.0 over 1 s). The window breaks on impact (`VelocityBreakable`) and then forces the stick right for 2 s.

### mindHatWagon (21)
- [code] Plate 1 counts the boy (`TriggerCount`, target 1) and opens Gate 1; plate 2 is pressed by a hat dude and opens Gate 2 (`Gate` component). The hat wagon reads the stick of the body that grabs it (`MindHatWagon`).

### mindHatJumpIntoWater (22)
- [code: Lever + MindHatReceiverDevice] A dude pulls `LeverReturn` right: the water gate opens and the guards react.
- [code: JumpToWaterDontShoot] About 30 s after that (plus the fetch animation) a sentry shoots the boy if he is still in the hat. Leaving the hat cancels the shot.
- [code] Pulling the underwater hatch (`UnderwaterHatch pulledOff`) releases the raft and gives 1 s of air.
- [code: CityGateCrush, BackUpKillTrigger] The closing water gate kills a boy at gate level in a jump or ragdoll; staying 0.29 s in the backup kill zone kills.

## Stuck spots

- lineup: in step mode the march and the drop can desync; switch to realtime.
- lineup drop: if the boy hangs and does not fall, the music loop is outside 11 to 14 s; wait (realtime).
