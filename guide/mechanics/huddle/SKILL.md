---
name: mechanics/huddle
title: The huddle
description: Huddle controls, pulling, speed smashes.
order: 26
related: [huddle/overview, offices/overview]
---
Status: from the game code ([code], `Huddle`, `Huddle*State`, chapter FSMs); thresholds are FSM constants, not tested in play.

## Rules

- **Becoming the huddle** [code: HuddleFoetus, SpawnManager]: after the boy joins it in the huddle chamber, and at huddle savepoints (`characterType` Huddle, marked huddle in the savepoint tables). `observe.body.kind` is huddle then.
- **Buttons are held levels** [code: GameInput.cs:185-190]: action held = grab, jump held = jump in the stick direction. No taps.
- **Grab states** [code]: beams, boxes, containers, items, things to pull off (doors, servers, vault doors: `HuddlePullOffAnimatedObject`), hanging. Grab and pull away to tear things off.
- **Speed smashes** [code, huddle FSMs]: walls, furniture and doors break when the huddle hits them fast enough (CEO wall x speed > 5.5 m/s, cafeteria tables > 3, the Norway wall > 6.5, then > 5 after a second hit). Run, then hit.
- **Swimming** [code: HuddleSwimState]: the huddle floats and swims; some tanks limit its swim speed (huddle trap: half speed).
- **Dogs cannot kill the huddle** [code: HuddleHulvAI TrashedByHuddle]; guards and drops still matter.
- **The game sometimes steers** [code: BoySetInput, HuddleFallTest]: FSMs and the CEO wall replace the huddle's input for a while (stick held right). `observe.body.stick_forced` is true then and `forced_by` names the script; wait it out instead of steering. `observe.body.grab` names the grab state (none, GrabBeam, GrabBox, ...) and `holding` the grabbed object.
- **Throwing** [code: HuddleThrow, JetCrateThrowCheat, HuddleGrabState.cs:710-724]: the huddle throws what it holds when action is released with the stick past 0.5 sideways (or up): `interact {verb:"throw", dir:"right"}`; with the keyboard the game corrects the throw 2 steps late.

## Hint

- When something blocks the way in the huddle chapters, try speed first (back up, run into it), then grabbing and pulling.

## Steps

- Smash: `act {inputs:[{stick:[-1,0], steps:60}, {stick:[1,0], steps:120}]}`; read `changes` for the break.
