---
name: mechanics/controls
title: Controls and input semantics
description: Stick, jump, action: buffer, tap vs hold, drop.
order: 20
related: [mechanics/climbing, mechanics/objects, mechanics/huddle]
---
Status: rules read in the game code ([code], `GameInput.cs`, `BoyInput.cs`, `Boy*State.cs`); timings are code constants, not measured in play.

The game reads three things per logic step (60 per second): the left stick, jump and action. The same input drives the boy, the huddle, hat-controlled dudes, the submarine, levers and valves. Send them with `act {inputs:[{stick:[x,y], jump, action, steps}]}`; the verbs of `interact` and `move_to` are macros of the same input.

## Rules

- **Stick** [code]: screen space, x > 0 right, y > 0 up. Deadzone 0.3: send 0 or a magnitude from 0.3 to 1. Below 0.8 the boy walks, above 0.8 he runs (about 2.72 m/s). Directions snap to the axes inside a 63 degree cone; diagonals do not count as up or down for ladders and ledges.
- **Jump is a rising edge** [code: GameInput.cs:220-248]: a press is buffered for 0.25 s and consumed at take-off. To jump again, release jump for at least one step first. Holding jump gives the full height; releasing it early damps the rise.
- **Action, standing on ground or in water** [code: GameInput.cs:193-208]: held = grab (push, pull, hold a ledge or a handle); a tap (pressed 1 to about 17 steps, under 0.3 s, then released) = action, and it fires on release.
- **Action in the air or on a ladder:** a rising edge.
- **Drop** [code: BoyInput.cs:29-39]: jump + stick down (y < -0.5) lets go of a ladder or rope. With the keyboard as the last used device (controller Digital) it is action + down instead.
- **Huddle** [code: GameInput.cs:185-190]: action and jump are plain held levels (no edge, no tap).
- **After a menu or any UI change** the game ignores jump and action until every button was released once [code: suppressAction]. Send a neutral step first.
- **During fades and loads input is off**; it comes back at fade 0.2 (respawn) or 0.6 (chapter load) with the jump buffer cleared. `wait_until {controllable:true}` before acting.
- **Keyboard quirk:** `observe.input.controller` follows the real device. When it is Digital (the user touched the keyboard), several places change: the submarine boost uses action, the cliff climb lets go with action + down, valves read stick gestures differently, the huddle jet-crate throw is corrected 2 steps later. Prefer that the user leaves the keyboard alone.
- **Scripted moments** take the stick away for a while (`BoySetInput`, `BoyLimitInput`, chase speed-ups): the result may show the body doing something you did not send. That is the game, not a bug.

## Hint

- One act call = up to 600 steps (10 s). Short bursts (20 to 60 steps) and an observe in between work best.
- Stuck against a wall or a step: the boy climbs low obstacles by himself when you keep pushing the stick toward them.

## Steps

- Running jump over a gap: `act {inputs:[{stick:[1,0], steps:30}, {stick:[1,0], jump:true, steps:20}, {stick:[1,0], steps:30}]}`; jump is held for 20 steps for full height.
- Grab and pull: `interact {verb:"grab", target:"o12"}` then `interact {verb:"pull", target:"o12", seconds:1}`; or act with action held and the stick away from the object.
- Tap action (lever, button): `act {inputs:[{action:true, steps:6}, {steps:6}]}`.
