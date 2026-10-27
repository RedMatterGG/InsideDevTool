---
name: mechanics/mind_hats
title: Mind-control hats, dudes and albinos
description: Mind-control hats, dudes, detach; albinos.
order: 24
related: [city/overview, mines/overview, mechanics/objects]
---
Status: from the game code ([code], `MindHat`, `MindHatReceiver`, `MindHatOnHead`, `AlbinoGroupManager`, `AlbinoMultiPullBoards`); not verified in play.

## Rules

- **Hats** [code: MindHat.cs:1095-1116]: the boy enters the hat's hang-on trigger (jump up into it) and puts it on (`observe.body.special` / state shows mind control). While he wears it, his body stands still and the dudes linked to that hat copy his stick and jump with a small delay (`frameDelay`, up to 30 steps); their grab and action come from his action.
- **Which dudes** [code]: up to 4 pooled dude bodies (`MindHatReceiver`), spawned by the level's `MindHatDudeSpawner`s; `body {op:"list"}` on the dev profile shows them. Hats sit in the farm barn (pigFight), the city (mindhatBasement, mindHatWagon) and the sewer secret; in the sewer secret a controlled dude can wear the second hat himself (a chain).
- **Detach gesture** [code: MindHat.cs:610-660]: stick down (y < -0.8) with action or a new jump within 0.2 s, or hold down about 0.33 s and then press action or jump. Stick |x| > 0.4 cancels it. Jumps are blocked while the gesture runs. `interact {verb:"hat_detach"}` does it.
- **Hats that will not come off** [code]: in the sewer secret the game blocks the detach while a controlled dude is in a scripted animation (`AchievementMindHatDetachController`).
- **Staying too long can kill**: in the city water-gate room a guard fetches a sentry gun about 30 s after the dudes pull the lever, and shoots if the boy is still in the hat (`_Moment Sentry WTF`, `JumpToWaterDontShoot`) [code].
- **Albinos (mines, offices)** [code: AlbinoGroupManager]: pale followers that react to what the boy does: they follow, wait, push and lift him. They are not steered directly. Doors and boards marked `helpersNeededToInteract` need 2 or more of them with the boy (`OpenElevatorDoors2`, `AlbinoMultiPullBoards`).
- **The mines head hat** [code: MindHatOnHead, mindhatDetach]: from the mindhatDetach moment on the boy wears a hat on his head and the woken albinos follow him; the shockwave blows it off at the cargo lift exit (`MindHatTakeOffSimple`).

## Hint

- Hat puzzles are usually: a dude stands where the boy cannot go (a plate, a lever, a gate), and the boy steers him there. Look for pressure plates and levers near the dudes.

## Steps

- Wear a hat: `interact {verb:"hat_wear", target:"<hat>"}` (walks under it and jumps into it; a free hat lists hat_wear in its actions); observe shows the dudes in `bodies`.
- Steer: plain act inputs now move the dudes. Get off: `interact {verb:"hat_detach"}` (the input gesture only; `allow_direct:true` lets it fall back to MindHat.ForceDetach, a direct call that is not for final takes).
