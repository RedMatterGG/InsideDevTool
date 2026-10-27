---
name: mechanics/climbing
title: Ledges, ladders, ropes, fences and cliffs
description: Ledges, ladders, ropes, fences, cliffs.
order: 21
related: [mechanics/controls, mechanics/death]
---
Status: state transitions read in the game code ([code], `BoyRunState`, `BoyJumpState`, `BoyGrabPlateauState`, `BoyLadderState`, `BoyRopeState`, `ClimbController`, `Fence`); not verified in play.

## Rules

- **Ledges grab themselves** [code: BoyJumpState GetGrabBasedLanding]: jump at a ledge and the boy catches it (state GrabPlateau). From the hang: stick up (y > 0.5) or jump climbs up; stick away (x < -0.5) + jump jumps off backwards; stick straight down lets go.
- **Low walls and boxes**: keep the stick toward an obstruction that has a top and the boy climbs onto it (state ReachPlateau).
- **Climb down** [code: BoyRunState:484-497]: at an edge, stick down (state ClimbDown, then a hang). `interact {verb:"climb_down"}`.
- **Ladders** [code: BoyLadderState]: stick up (y > 0.5, magnitude > 0.9) near a ladder starts climbing; a jump onto a ladder catches it. Stick y climbs; jump with |x| > 0.5 jumps off sideways; drop (jump + down) lets go. `interact {verb:"ladder_up" | "ladder_down" | "ladder_off"}`.
- **Ropes** [code: BoyRopeState]: caught only while jumping. Stick x swings; jump releases forward; drop lets go. `interact {verb:"rope_jump", dir:"right"}`.
- **Fences** [code: Fence.cs:221-354]: climbable wire fences (farm worm swamp, sewer dog fence) work like ladders with a top edge.
- **Cliff climbs** [code: ClimbController.cs]: a special mode (`observe.body.special` ClimbingCliff) in the forest river, the mines and the albino tank: the stick moves along the cliff, jump + a direction away jumps off. With the keyboard as the device, letting go is action + down.
- **Falls kill above a speed limit** [code: BoyJumpState.cs:364-376] (`properties.death.fallSpeedLimit`) unless the ground is marked safe (`BoyGround` fall-kill mode Invincible, used on some lab floors and lifts). The safe height is not known from the code alone [unverified]; when unsure, climb down instead of jumping down.

## Hint

- Ledge out of reach: look for a box to push under it (mechanics/objects) or a rope or ladder nearby (observe.objects kinds ladder, rope, ledge).

## Steps

- Climb a ledge: `move_to {target:"<ledge id>", stop:"adjacent"}`, then `interact {verb:"climb", target:"<ledge id>"}` (jump + up).
- Get down safely: `interact {verb:"climb_down"}` at the edge, then `interact {verb:"drop"}` from the hang.
