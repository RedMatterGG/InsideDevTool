---
name: mechanics/objects
title: Boxes, levers, buttons, valves and hatches
description: Boxes, levers, buttons, valves, hatches, plates.
order: 23
related: [mechanics/controls, mechanics/mind_hats]
---
Status: from the game code ([code], `BoyGrabState`, `Lever`, `SwitchButtonNew`, `SwitchValve`, `Hatch`, `VentHatch`, `PullOffBoards`, `JetCrate`); the valve rotation rule was not traced [unverified].

## Rules

- **Grab** [code: BoyGrabState]: action held while facing a grabbable (a loose rigid body or a grab handle, within about 1 x 1.2 m in front of the boy). Stick toward the object pushes, away pulls; releasing action lets go. Walking into a pushable box also pushes it.
- **Levers** (`Lever`, `LeverBreaker`, `LeverReturn`) [code]: grab the handle and pull the stick left or right; many send `left` / `right` signals that switch power, gates, water or lights. Return levers spring back when released.
- **Buttons** (`SwitchButtonNew`, `ButtonNew`, `ButtonNewPushHatch`) [code]: a `SwitchButtonNew` is pressed with a new action press while the boy stands at it facing it (or jumps / hangs on a rope at it) [code: SwitchButtonNew.cs:773]; a hold button stays in only while action is held: `interact {verb:"button", seconds}`. Floor plates are stepped on or get a box. Some only work when powered. The result shows the signals in `changes.signals`.
- **Valves** (`SwitchValve`) [code: SwitchValve.cs:337-562]: hold action and turn the stick around; `interact {verb:"valve"}`. The exact gesture is [unverified]; with the keyboard the game uses another branch.
- **Hatches and vent covers** (`Hatch`, `VentHatch`, `UnderwaterHatch`) [code]: grab the handle and pull away from the opening (about 0.75 to 1.1 s of pulling): `interact {verb:"pull", target:"<hatch>", seconds:2}` (a floor hatch pulls up, a vent cover sideways). From below, on the ladder, `interact {verb:"push"}` pushes it up. Grabbing a hatch from the wrong side makes the boy tug uselessly (`IdiotGrab`).
- **Boards on doorways** (`PullOffBoards`, `BoardBreak`, `DoorBoarded`) [code]: grab a board and pull (`interact {verb:"pull"}`); several boards need several pulls. Some heavy boards need two or more helpers pulling with the boy (`AlbinoMultiPullBoards`, mines).
- **Pressure plates** (`TriggerCount`, `MindHatReceiverPressurePlate`, `SwitchBoxAlbinoCounter`) [code]: count bodies or weight on the plate; some need a hat dude, some a count of 20 (mines lift).
- **Lift doors** (`OpenElevatorDoors`, `OpenElevatorDoors2`) [code: OpenElevatorDoors.cs:160-330]: grab facing the way the door opens, then stick up while holding action until it opens; letting go before drops it back (it does not kill). `interact {verb:"lift", seconds:4}`. `OpenElevatorDoors2` needs albino helpers.
- **Carrying a grab** [tool]: after `interact {verb:"grab"}` the boy keeps holding through later act / move_to calls (`observe.body.grip_held`); `interact {verb:"release"}` or `act {release:true}` lets go. Other verbs let go first and warn `grip_released`.
- **Jet crates** (`JetCrate`, lab and huddle) [code]: a crate with a jet; once freed it flies along a stream and can be grabbed and carried.

## Hint

- Most puzzles are "a box, a lever or a body goes on a switch". List `observe.objects` with their actions; `changes` after an action tells you what reacted.

## Steps

- Push a box to a spot: `move_to {target:"<box>", stop:"adjacent"}`, `interact {verb:"push", target:"<box>", seconds:2}`, observe, repeat.
- Pull a lever: `interact {verb:"lever", target:"<lever>", dir:"left"}`; read `changes.signals`.
