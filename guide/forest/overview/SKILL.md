---
name: forest/overview
title: 1 Forest (subscenes 0-5)
description: Ch1 forest: guards, river dog, chases, lake.
chapter: 1
order: 100
subscenes: 0-5
related: [mechanics/controls, mechanics/death, mechanics/water, secrets/orbs]
---
Status: places, savepoints and mechanisms read in the game code and scene data (game-code audit 2026-09-28, 01_Forest); [inferred] marks steps assembled from that code. Nothing here was verified in play.

The boy goes right through the forest. Subscene order follows world x (left to right). Sections below are named after the places (the checkpoint names).

## Savepoints

<!-- savepoints:begin -->
| sub:sp | place | savepoint object | x, y | flags |
|---|---|---|---|---|
| 0:0 | gamestart | Savepoint 1 | -723, -153 | chapter |
| 1:0 | forestReveal | Savepoint 2 | -581, -166 | chapter |
| 2:0 | riverWithDog | Savepoint 7 | -453, -195 | chapter |
| 3:0 | SentryTruckChase | Save Point 1 | -345, -193 | chapter |
| 4:0 | finalChase | Savepoint 4 | -252, -199 | chapter |
| 5:0 | lake | Savepoint 1 | -71, -219 | chapter |
| 5:1 | lake | Savepoint 2 | -29, -219 |  |
<!-- savepoints:end -->

## Hint

### gamestart (0)
The title screen waits for any button on the real controller [code: StartSequence.cs:120-138]; the agent input does not reach it. Ask the user to press a button (or use the menu tools), then walk right.

### forestReveal (1)
Guards with flashlights and a truck. Being seen inside their kill volume during the guard moment gets the boy caught; the truck leaves on its own after about 9.5 s. Further on: a wall to climb over, a slope to slide down, a fridge that falls.

### riverWithDog (2)
A van with a searchlight drives off after about 6 s. Then a dog in the river: once it starts, do not stop. A climbable cliff follows.

### SentryTruckChase (3)
A truck with a dart gunner sweeps the area. Cover is what saves the boy; staying safe long enough ends the chase.

### finalChase (4)
Several chases in a row (men, a car, a slide, dogs at a cliff jump). Speed decides most of them.

### lake (5)
Deep water with a van and a gunner at the shore. Swimming back the wrong way is fatal. A secret orb is near (secrets/orbs).

## Steps

### forestReveal (1)
- [code] `_Moment2b_Test`: after the guards start (`StartSneak`), a kill volume is armed for 12.22 s (10 s for the upper one). Stay out of it while it is armed: hide and wait, then go.
- [code] `Wall/Trigger WallTop` is a climb-over; `SlideDown Helper` pushes the stick right on the slope.

### riverWithDog (2)
- [code] `StartHulvTrigger` starts the dog; `SlowdownBoyTrigger` caps the stick at 0.8 in the water.
- [code] The dog stops at the river edge when the boy jumps over the gap (`HulvSoundTrigger boyJumpOver`). [inferred] Keep running right and jump the gap, then climb the cliff (`RockClimb/Cliff_top`, a ClimbController: stick moves along it, jump + direction jumps off).

### SentryTruckChase (3)
- [code: ForestTruckChase.cs:202-277] The boy counts as in cover also when almost in cover with stick x < -0.5. Leaving cover under fire is detected at stick x < -0.3. After 7 s safe the chase completes. [inferred] Get behind the cover the truck light cannot reach, hold the stick slightly left, wait.

### finalChase (4)
- [code: DogsInForestSpeedHack.cs:52-95] At the dog cliff jump the real chase starts when more than 4 s passed and the stick is under 0.65 to the right, or the boy passes the pivot fast; 4.8 s later the dogs kill. [inferred] Hold the stick fully right through the whole chase and jump at the cliff.
- [code] `_Chase_03_HideAndShoot`: a man in the back of a moving car compares the boy's x to a reference point; exposed = shot. [inferred] Keep the car between you and him.
- [code] `_Chase_04_Slide`: shots while sliding; a hang detector pulls the boy down if he hangs too long.

### lake (5)
- [code: LakeKillShot] `Trigger Kill` near the van: 0.5 s after entering, a shot kills.
- [code: DartWater] In the dart zone, stick x < -0.3 (swimming back left) kills. First contact with the water costs 2 s of air.
- [inferred] Swim right and down away from the shore; never press left there.

## Stuck spots

- Title screen: needs a real button press (see gamestart).
- River dog: stopping in the water or turning back gets the boy caught; reload and run without stopping.
