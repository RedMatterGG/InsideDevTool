---
name: mechanics/death
title: Death, respawn, enemies and getting unstuck
description: Deaths, respawn, enemies, getting unstuck.
order: 28
related: [safety/rules, mechanics/climbing]
---
Status: from the game code ([code], `Character.Kill`, `DeathManager`, `GameManager.Respawn`, `HulvAI`, `ChaserBase`, `SentryGun`, `SpotLightDetector`); not verified in play.

## Rules

- **One hit kills** [code: Character.cs:39-50]. Causes: fatal falls, drowning, kill volumes, dogs, guards (strangling), sentry guns and dart guns, crushers and gates, the shockwave, the girl in the water.
- **Respawn** [code: DeathManager.cs:59-92, GameManager.cs:144-150]: 4 s after death the game reloads the last checkpoint (2 s fade). `wait_until {any:[{alive:true}]}` or `checkpoint {op:"reload"}` (also a clean "restart this room" while alive). The boy object is new afterwards: observe again.
- **Dogs** (`Hulv`, `HulvAI`) [code]: run on waypoints, then chase (ChargeIn, Threaten, CircleBoy, ChargeToKill, SwimHunt). Each chase has a scripted way out that the level builds (a cliff or ledge to climb, a fence, a door that closes, a window); in the code the dogs follow waypoints and stop at those points (for example `HulvSoundTrigger` at the river edge) [code; the general rule is unverified]. Chases often speed the boy up a little (`ChaseBoyTest`, stick x 1.05 to 1.2) and some only start when the stick is not pushed right.
- **Guards** (`ChaserDude*`, `ChaserGuard*`) [code]: spot the boy (light, line of sight, being outside a shadow), run and strangle. Spotting usually has a short delay (0.5 to 2 s); hide in shadow or behind cover, wait for the patrol to pass.
- **Sentry guns and spotlights** (`SentryGun`, `SpotLightDetector`, `KillSpotLightDetector`) [code]: the boy dies if he stays visible in the light about 0.5 s. Move only while the light looks away; cover objects break the line.
- **Scripted catches**: some moments kill on purpose if you go the wrong way (a back-track camera shot in the city lineup room, the CEO window, crushing gates). Going back is rarely right.
- **Points**: several kill FSMs add to a `$Points` variable that nothing reads [code]: ignore.

## Hint

- When `stuck.calls_without_progress` reaches 3: stop repeating. Re-read observe.objects and their actions, look up the place in the chapter overview (guide section = checkpoint name), try a different object or direction, or reload the checkpoint.

## Steps

- Unstuck order: 1) observe {detail:"full"} and screenshot {mode:"marked"}; 2) guide section for the place at level hint, then steps; 3) try each object action once and read changes; 4) `checkpoint {op:"reload"}`; 5) tell the user what you tried.
