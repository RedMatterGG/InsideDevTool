---
name: sewers/overview
title: 4 Sewers and submarine (subscenes 23-33)
description: Ch4 sewers: spotlights, dogs, submarine, girl.
chapter: 4
order: 400
subscenes: 23-33
related: [mechanics/submarine, mechanics/water, mechanics/death, secrets/orbs]
---
Status: places, savepoints and mechanisms read in the game code and scene data (game-code audit 2026-09-28, 04_Sewers); [inferred] marks steps assembled from that code. Nothing here was verified in play. The audit notes that the order of these places in its sources is the audio-zone order, which follows world x.

## Savepoints

<!-- savepoints:begin -->
| sub:sp | place | savepoint object | x, y | flags |
|---|---|---|---|---|
| 23:0 | waterLockDoors | Savepoint 1 | 1157, -296 | chapter |
| 23:1 | waterLockDoors | Savepoint 2 | 1197, -293 |  |
| 24:0 | hideInShadows | Savepoint_1 | 1215, -280 | chapter |
| 24:1 | hideInShadows | Savepoint_2 | 1245, -286 | starts off |
| 24:2 | hideInShadows | Savepoint_3 | 1273, -293 |  |
| 25:0 | sewerPoopWater2 | Savepoint 1 | 1276, -321 |  |
| 25:1 | sewerPoopWater2 | Savepoint 2 | 1325, -330 | chapter |
| 26:0 | hulvesInSewer | Savepoint 10 | 1375, -336 |  |
| 27:0 | dogChaseAndFenceSewers | Savepoint 1 | 1435, -342 |  |
| 28:0 | stealSubmarine | Savepoint 0 | 1469, -344 | chapter |
| 28:1 | stealSubmarine | Savepoint 1 | 1494, -365 | chapter |
| 29:0 | submarineBigRoom | Savepoint 1 | 1525, -439 | chapter |
| 29:1 | submarineBigRoom | Savepoint 2 | 1620, -554 |  |
| 30:0 | submergedChamber | 0_Savepoint_submergedChamber | 1658, -543 | chapter |
| 30:1 | submergedChamber | 1_Savepoint_submergedChamber | 1718, -534 |  |
| 30:2 | submergedChamber | 1_Savepoint_submergedChamber | 1720, -523 |  |
| 30:3 | submergedChamber | 2_Savepoint_submergedChamber | 1760, -512 | chapter, starts off |
| 30:4 | submergedChamber | Save_5 | 1692, -520 |  |
| 30:5 | submergedChamber | Save_6 | 1648, -538 | starts off |
| 30:6 | submergedChamber | 7_Savepoint_submergedChamber | 1731, -548 | chapter |
| 31:0 | SubmarineSecret | 1_Save_AtPuzzle | 1709, -590 | dynamic |
| 31:1 | SubmarineSecret | 2_Save_AtPuzzle | 1739, -581 | dynamic |
| 32:0 | SubmarineAndWaterHeight | Savepoint 0 | 1756, -529 |  |
| 33:0 | SubmarineAndGirl | Savepoint 0 | 1820, -533 | starts off |
| 33:1 | SubmarineAndGirl | Savepoint 1 | 1850, -531 | chapter |
| 33:2 | SubmarineAndGirl | Savepoint 2 | 1908, -496 | starts off |
<!-- savepoints:end -->

## Hint

### waterLockDoors (23)
A water lock: two buttons switch the water between high and low; a door that is blocked by an object forces the water down. A floating box and a vent the boy is pushed through.

### hideInShadows (24)
Three spotlights, a cage on a rope moved with a valve, a hatch, and trains in the background. Being seen 0.5 s is fatal.

### sewerPoopWater2 (25)
Dogs eating in the background; falling too fast onto the floor kills.

### hulvesInSewer (26)
A dog that swims (water hulv) chases the boy.

### dogChaseAndFenceSewers (27)
Three dogs at a climbable fence; the way out is through boards on a doorway, and a cabinet falls behind the boy.

### stealSubmarine (28)
A scientist arrives in the submarine; a guard at the railing. Stay in the shadow. A ladder that breaks.

### submarineBigRoom (29)
Ram a wall and boards with the submarine.

### submergedChamber (30)
Buttons, rolling capsules that kill, a jet crate, a locker and a door to bust with the submarine, a wall that cracks. The girl shows up for the first time.

### SubmarineSecret (31)
A secret: a block to ram open, two hats and a dude (secrets/orbs).

### SubmarineAndWaterHeight (32)
A button raises and lowers the water; a door.

### SubmarineAndGirl (33)
The girl in the water attacks the submarine; boards and a gate to break through.

## Steps

### waterLockDoors (23)
- [code: WaterHeightLogic] Each button press toggles the fill (high / low); `objectStuck` from the door forces the water low. [inferred] Use high water to float the box up, low water to walk under.

### hideInShadows (24)
- [code: SpotLightDetector + KillSpotLightDetector] 0.5 s visible = shot. `SwitchValve` (turn) moves the cage on the rope (`SwitchValveTarget RopeAttachment`); opening the hatch (`hatchOpen`) turns detectors 2 and 3 off.
- [code] On the slope the shot is the "safe" variant (`useSafeKill`): still a death.
- [inferred] Move only while the lights look away; ride or use the cage to reach the hatch, open it.

### hulvesInSewer (26)
- [code: StartKillTrigger, HulvIsNearTrigger] When the dog is near it goes for the kill; `PuzzleStatusCheck` counts the boy safe once the camera is above the surface and he is on the safe side. The chase speeds the boy up (`ChaseBoyTest`).

### dogChaseAndFenceSewers (27)
- [code: Fence, HulvFenceChaseManager] The dogs run to the side of the fence the boy is on (`sendHulvsLeft/Right`).
- [code: PullOffBoards -> TriggerGoThrough] Tear the boards off the doorway, then push the stick right (> 0.1) to go through. If the dogs are close at that moment, they are put in the doorway and the cabinet kills (`DogChaseDistCompare`, `HulvSwapper`); otherwise they are rerouted and the cabinet falls behind the boy.
- [inferred] Lure the dogs to the far side of the fence, climb over, tear the boards while they come around.

### stealSubmarine (28)
- [code: BoyNotInShadowTrigger] Once the scientist's animation played, being outside the shadow alarms him (3 s to get back into shadow).
- [code] Entering the submarine (`SubEnterTrigger`) starts a 6.5 s timer and the sub rises.

### submarineBigRoom (29)
- [code: TriggerSubmarineCharge] The wall breaks on a charged ram (mechanics/submarine).

### submergedChamber (30)
- [code] `ButtonBox` opens the gate (and starts the girl moment); `ButtonNew` releases the capsules, which kill when they run over the boy (`CapsuleRunOverLogic`). The locker and `BustDoor` break on a ram.
- [code] `Save_4` turns on 15 s after its timer starts; `Save_6` only with the boy in the sub inside its zone.

### SubmarineAndGirl (33)
- [code: WaterGirlSubmarineTest] A hard impact of the sub while the girl is awake makes her attack; 7 hits on the glass kill. [inferred] Drive carefully, do not ram near her.
- [code: Audio_CorrectSideOfDoorTrigger] When the boards break, being on the right side of the gate makes the girl leave and turns `Savepoint 2` on.

## Stuck spots

- submergedChamber: after a chapter load (not a death) the sub position and the broken wall reset [code: LevelGlobals].
- hideInShadows: the trains are background only; they do not block the route [code: TrainBackground].
