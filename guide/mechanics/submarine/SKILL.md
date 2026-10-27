---
name: mechanics/submarine
title: The submarine
description: Submarine: enter, drive, boost-ram, exit.
order: 25
related: [sewers/overview, mechanics/water]
---
Status: from the game code ([code], `Submarine`, `SubmarineBoyInteraction`, `SubmarineChargeBoost`, `SubmarineChargeTrigger`); not verified in play.

## Rules

- **Enter** [code: SubmarineBoyInteraction.cs:380-466, SubmarineClimbController.cs:465-481]: stand on top of the submarine within 0.7 m of the open hatch, not jumping, and hold the stick straight down (`interact {verb:"sub_enter"}`). The boy's mode becomes InsideSub and the stick now drives the submarine. A hat dude climbs in after about 0.2 s of real time.
- **Drive** [code: Submarine.cs:1012-1206]: the stick gives thrust and aim in any direction, including up and down. The lamp in front lights the way.
- **Boost / charge** [code: SubmarineChargeBoost.cs:279]: hold jump to charge and release to fire a dash; with the keyboard (controller Digital) action is used instead. `interact {verb:"sub_boost", dir:"right"}`.
- **Breaking things** [code: SubmarineChargeTrigger]: walls, scaffolds, boards and lockers marked as charge targets break only at speed: the forest-to-mines scaffold needs 5.5 m/s (`requiredVelocity`), so build up speed and boost into it. An auto-aim zone often helps.
- **Exit** [code: SubmarineBoyInteraction.cs:565-602]: at the surface, upright and slow: action, or a quick flick of the stick up (> 0.85). `interact {verb:"sub_exit"}`.
- **The girl attacks it** [code: WaterGirlSubmarineTest.cs:277-311]: in the sewer girl room a hard impact wakes her into an attack; 7 hits on the glass kill the boy.
- **Save-room memory** [code: LevelGlobals]: in the submerged chamber the sub's position and broken wall pieces are kept in memory after a death but not after a chapter load.

## Hint

- If a wall will not break, back off further and boost earlier; speed at the moment of impact is what counts.

## Steps

- Ram right: `act {inputs:[{stick:[-1,0], steps:40}, {stick:[1,0], steps:60}, {stick:[1,0], jump:true, steps:30}, {stick:[1,0], steps:30}]}` (hold jump to charge, the release fires).
