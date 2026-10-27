---
name: mines/overview
title: 5 Mines and shockwave (subscenes 34-48)
description: Ch5 mines: albinos, cargo lift, shockwave rooms.
chapter: 5
order: 500
subscenes: 34-48
related: [mechanics/shockwave, safety/timing, mechanics/mind_hats, secrets/orbs]
---
Status: places, savepoints and mechanisms read in the game code and scene data (game-code audit 2026-09-28, 05_Mines); [inferred] marks steps assembled from that code. Nothing here was verified in play. The shockwave rooms follow the music clock: use realtime there (mechanics/shockwave).

## Savepoints

<!-- savepoints:begin -->
| sub:sp | place | savepoint object | x, y | flags |
|---|---|---|---|---|
| 34 | forcePush | (no savepoint: logic or pass-through area) | | |
| 35:0 | beachToMines | Savepoint 1 | 2013, -476 | chapter |
| 36 | cliffOpening | (no savepoint: logic or pass-through area) | | |
| 37:0 | albinosInMineWagon | Savepoint 1 Static | 2030, -500 |  |
| 37:1 | albinosInMineWagon | Savepoint 3 Dynamic | 2009, -499 | dynamic |
| 37:2 | albinosInMineWagon | Savepoint 2 Dynamic | 2035, -500 | dynamic |
| 38:0 | cargoLift | Savepoint 1 | 2085, -476 | dynamic |
| 38:1 | cargoLift | Savepoint 2 | 2094, -502 | dynamic |
| 38:2 | cargoLift | Savepoint 3 | 2117, -476 | dynamic |
| 38:3 | cargoLift | Savepoint 4 | 2146, -472 | dynamic |
| 38:4 | cargoLift | Savepoint 5 | 2124, -504 | chapter |
| 38:5 | cargoLift | Savepoint 6 - Static | 2133, -504 | starts off |
| 39:0 | mindhatDetach | Savepoint 1 CargoLift | 2118, -553 | dynamic |
| 39:1 | mindhatDetach | Savepoint 2 Elevator | 2136, -558 | dynamic |
| 39:2 | mindhatDetach | Savepoint 3 Hat | 2165, -564 | dynamic |
| 39:3 | mindhatDetach | Savepoint 4 Cave | 2224, -567 | dynamic |
| 39:4 | mindhatDetach | Savepoint 5 Before Hat, Chapter | 2178, -559 | chapter |
| 40:0 | albinoPushBlock | Savepoint2 | 2047, -547 | dynamic |
| 40:1 | albinoPushBlock | Savepoint1 | 2086, -553 | dynamic |
| 41:0 | AlbinosInTank | Savepoint | 1997, -541 | dynamic |
| 41:1 | AlbinosInTank | SavepointToSecret | 2033, -543 | dynamic |
| 41:2 | AlbinosInTank | SavepointToSecret2 | 2040, -547 | dynamic |
| 42 | hulvSecretLadder | (no savepoint: logic or pass-through area) | | |
| 43:0 | HulvesAndTorchSecret | Savepoint 10 | 2011, -564 | dynamic |
| 44 | mineExit | (no savepoint: logic or pass-through area) | | |
| 45 | shockwaveEntrance | (no savepoint: logic or pass-through area) | | |
| 46:0 | forcePushGetWagon | Savepoint1 | 2173, -501 | chapter |
| 47:0 | forcePushRotator | Savepoint1 | 2264, -501 | chapter |
| 47:1 | forcePushRotator | Savepoint2 | 2309, -496 |  |
| 48:0 | forcePushMovingCover | Savepoint 1 | 2330, -491 | chapter |
| 48:1 | forcePushMovingCover | Savepoint 2 | 2340, -491 | starts off |
| 48:2 | forcePushMovingCover | Savepoint 3 | 2393, -492 | starts off |
<!-- savepoints:end -->

## Hint

### beachToMines (35)
The submarine is stuck on the beach; the mine gate is pushed open. A scaffold to ram from the water hides an orb (secrets/orbs).

### cliffOpening (36)
A slide down a slope; no savepoint.

### albinosInMineWagon (37)
A mine wagon with albinos on a tilting rail (a lever tilts it), then a big lift door that needs helpers.

### cargoLift (38)
The hub: a lift powered by a lever, a counter plate that needs 20, a cage with albinos, a dead albino. The shockwave blows the boy's hat off at the exit.

### mindhatDetach (39)
The hat goes onto the boy's head and the albinos wake and follow him. Spikes kill; a platform moved with a valve.

### albinoPushBlock (40)
A heavy wagon that only moves with at least two albinos pushing; it crushes.

### AlbinosInTank (41)
A tank with albinos inside; the boy breaks it open. Boards that need helpers lead to a secret (secrets/orbs).

### HulvesAndTorchSecret (43)
A dark cave with dogs that fear fire (secret route, secrets/orbs).

### forcePushGetWagon (46), forcePushRotator (47), forcePushMovingCover (48)
The shockwave rooms: shields, a turning cover, a moving cover. Cover at every blow.

## Steps

### beachToMines (35)
- [code: MinesGate] The gate opens by pushing it.

### albinosInMineWagon (37)
- [code: LeverLight -> MinesTrackTilt] The lever tilts the rail up / down; the albinos leave the wagon at the bottom.
- [code: OpenElevatorDoors2 helpersNeededToInteract 2] The big lift door: action held + stick up with at least 2 albinos lifting with the boy.

### cargoLift (38)
- [code: LeverBreaker -> Elevator] The power lever turns the lift on; the cabin lever (`LeverElevator` left / center / right) picks bottom / center / top floor. Albinos in the cabin slow the lever.
- [code: SwitchBoxAlbinoCounter countTarget 20] The plate counts albinos, the boy and the dead albino as weight; the display shows 20 - N. At 20 the vault doors (exit) open and the lift gets stuck.
- [inferred] Bring the whole crowd and the dead albino onto the plate and stand on it too.
- [code: MindHatTakeOffSimple] At the exit the next shockwave warning blows the hat off when the boy is in its trigger.

### mindhatDetach (39)
- [code] `SpikeKill` kills on contact. `SwitchValve` moves the platform (`movablePlatform`). `AlbinoPlatformClimb` lets the albinos climb once enough are on the platform.

### albinoPushBlock (40)
- [code: AlbinoPushBlockWeightCheat targetCount 2] With 2 or more albinos in the push volume the wagon can be pushed; `WagonCrush` kills a boy under it.

### AlbinosInTank (41)
- [code: _PM Tank] Grabbing the tank low or high and swinging breaks it; three albinos spawn and follow after about 9.5 s.

### forcePushGetWagon (46)
- [code: MinesFlipFlop] The lever flips the sonic shields (alternating). `HeadlessKillTrigger`: at a blow, a head outside the shield's cover kills.
- [code: CrateShockwave] Holding the crate with the stick pushed (> 0.4) at a blow makes the boy stumble.

### forcePushRotator (47)
- [code: MinesRotater] The rotating cover turns on the music cycle once its lever (`startRotator`) is pulled. [inferred] Stay on the side of the rotator away from the blow.
- [code] Standing 15 s in `Trigger Barrel` after the savepoint drops a barrel at the beam (anti-stuck).

### forcePushMovingCover (48)
- [code: LeverOneTime -> BlastShield_Logic] The one-time lever opens the shutters (the deadly zone switches on). The sliding cover (`ForcePushMovingCover`) is pushed by each blow. [inferred] Push the cover forward between blows and stay behind it at every blow.

## Stuck spots

- No wave at all: the cycle reads the music position; without music the wave may not come [inferred].
- forcePushMovingCover: after a reload on savepoints 2 / 3 some objects of the second wave are back [code: missing load link]; push them again.
- forcePushRotator: standing still 15 s after the savepoint spawns a helper barrel.
