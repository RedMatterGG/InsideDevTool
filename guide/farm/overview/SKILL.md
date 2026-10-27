---
name: farm/overview
title: 2 Farm (subscenes 6-11)
description: Ch2 farm: chicks, pigs, worm pig, hat door.
chapter: 2
order: 200
subscenes: 6-11
related: [mechanics/objects, mechanics/mind_hats, secrets/orbs]
---
Status: places, savepoints and mechanisms read in the game code and scene data (game-code audit 2026-09-28, 02_Farm); [inferred] marks steps assembled from that code. Nothing here was verified in play.

## Savepoints

<!-- savepoints:begin -->
| sub:sp | place | savepoint object | x, y | flags |
|---|---|---|---|---|
| 6:0 | fields | Savepoint | 50, -221 | chapter |
| 7:0 | chickenGun | Savepoint 1 | 191, -223 | chapter |
| 8:0 | pigJump | Savepoint 1 | 269, -212 | chapter |
| 9:0 | wormSwamp | Savepoint 2 | 309, -228 | chapter |
| 10:0 | pigFight | Savepoint 1 | 351, -228 | chapter |
| 10:1 | pigFight | Savepoint 2 | 390, -227 |  |
| 11:0 | noMansLand | Savepoint 1 | 454, -230 | chapter |
| 11:1 | noMansLand | Savepoint 2 | 518, -230 |  |
<!-- savepoints:end -->

## Hint

### fields (6)
A corn field, then a hatch with a ladder down to a basement (a secret orb is down there, secrets/orbs). A joystick device in the right basement room, in front of a gate, belongs to the secret ending; it accepts nothing until the big orb is taken (secrets/orbs). A fence near the field pushes the boy left until its gate opens.

### chickenGun (7)
Chicks follow the boy. A boarded door, a suction machine started with a pull lever, and a hay bale. The lever is blocked while the bale covers the hatch.

### pigJump (8)
Pigs in pens and a pig wagon. Landing hard from too high kills (fall speed below -8 m/s); landing in the wagon is fine. A secret orb is behind a breakable board (secrets/orbs).

### wormSwamp (9)
Mud slows the boy; a climbable wire fence.

### pigFight (10)
A barn with an aggressive pig that carries a worm (parasite). Charging into a wall stuns it. Further on: a door that needs four dudes under one mind-control hat.

### noMansLand (11)
A field with marching figures, birds, a rope, water. In a trial copy of the game the trial ends here (not on Steam).

## Steps

### chickenGun (7)
- [code: ChickenGunLogic.cs:477-500] The suction machine starts when the starter lever is pulled at the right moment of its animation (`jogA` / `jogB` window); it may take several pulls.
- [code] `LeverReturn left / center` switches suction on / off. While the boy is at the lever and the bale is at the suction pipe, the bale is held by the pipe. `TriggerBoxCover` blocks the lever while the bale covers the hatch.
- [code] One chick dies in the machine per session (5 to 7 chicks through the fall trigger, random).

### pigFight (10)
- [code: FarmPigFight] The pig charges (charge 1 s); hitting a wall stuns it (`wallStunnedTime` 15 s, `getUpTime` 3 s). Pulling its worm off fires `wormPulledOff`, which (among 13 receivers) enables the exit collider `ColliderPigExit`.
- [inferred] Let the pig charge into a wall, then grab and pull the worm during the stun.
- [code: DoubleMindhatLiftDoorLogic boysNeeded 4] The sliding door later needs 4 hat-controlled dudes lifting together: wear the hat, bring all four under the door, lift (stick up with action held).

### noMansLand (11)
- [code] The rope has its own camera (`Camera Blend Rope`); grab it while jumping (mechanics/climbing).

## Stuck spots

- chickenGun: if the machine does not start, pull the starter lever again (timing window).
- pigFight: the gate of the barn never closes again once open [code], so the way back stays free.
