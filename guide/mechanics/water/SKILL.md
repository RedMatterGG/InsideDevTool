---
name: mechanics/water
title: Swimming, diving and air
description: Swim, dive, 11 s of air, the girl.
order: 22
related: [mechanics/submarine, lab/overview]
---
Status: from the game code ([code], `BoySwimState`, `BoyWater`, `Boy.UpdateDivingTime`, lab FSMs); not verified in play.

## Rules

- **Swim** [code: BoySwimState]: the stick is the swim direction; stick down dives. At an edge, jump climbs out. Action held near an object grabs it underwater (BoySwimGrabState).
- **Air** [code: BoyWater.cs:18]: diving time is 11 s by default (`maxDivingTime`), counted while submerged; when it runs out the boy drowns. `observe.body.air_s` shows what is left while diving. Surfacing resets it.
- **Places change the limit** [code, lab FSMs]: zones give extra air or longer dives (`TriggerExtraAir`: +3.5 s and 15.5 s dives in the lab elevator ride; the girl chases set dives of 25 s and give +5 or +6 s when she stops) and some take air away (`TriggerDepleteAir`: -5 s). First contact with the lake in the forest costs 2 s of air.
- **Water can be deadly by itself**: the lab flush tunnel stream kills (`WaterFlushKiller`), and some deep water has guards with tranquiliser darts (forest lake).
- **The girl (WaterGirl)** in the lab and the sewers chases the boy underwater and drowns him when she catches him. She is slower than she looks: the game scales her speed down in several zones (`GirlSpeedCheat`, factor 0.935 to 0.955) and pauses her 0.8 s after some escapes [code]. Gates (`GateCrush`) that close behind the boy stop her.

## Hint

- Plan a dive before going under: count the metres to the next surface or air pocket; 11 s at swim speed is not far.
- When the girl wakes, swim straight for the exit the level shows (gate, hatch, surface); do not stop to look back.

## Steps

- Dive and come back: `act {inputs:[{stick:[0,-1], steps:60}, {stick:[1,0], steps:120}]}`, then `wait_until {any:[{state:"Swim"}]}` and `interact {verb:"surface"}`.
- Climb out at an edge: `interact {verb:"swim_up"}` near the edge, then `act {inputs:[{stick:[1,0.6], jump:true, steps:20}]}`.
