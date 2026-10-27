---
name: secrets/orbs
title: Secret orbs, the big orb and the secret ending
description: 14 orbs, the code, the secret ending (spoilers).
order: 900
related: [safety/rules, farm/overview]
---
Status: locations and chains read in the game code and scene data (orb map `orb_achievement_map.txt`, scene audits 2026-09-28); the approach routes marked [inferred] are assembled from trigger layouts. Nothing here was verified in play.

## Warnings

- **Orbs are Steam achievements.** Pulling an orb calls the game's own `User.Achievement.Acquire` (local bit and Steam). That is normal play, but only do it when the user wants orbs. Never edit orb bits with tools unless asked (`save {op:"orbs"}` needs confirm).
- **The secret ending resets orb progress and starts a new game** [code: FinalSecretFade.cs:100-147: `Acquire(PowerSecret)`, `ResetProgression()`, `StartNewGame()`]. It writes the save. Ask the user before pulling the final wire.
- An orb taken in this run (since the last chapter load) shows up already broken after a respawn; an orb taken in any earlier run keeps its light off [code: SecretPod.cs:201-231].

## Hint

- Every orb sits off the main path: behind a vent, a board, a hatch or underwater, usually a short detour back or down from a savepoint. The 13 normal orbs are in: forest lake, farm field basement, farm pig pens, city basement (first hat), city rooftops, sewer submarine secret, beach to mines, mines torch cave, lab girl explore, lab dive, lab rotating shadow, lab ceiling water, offices albino lift ceiling.
- The 14th is in the big orb room in the offices; it opens only when the 13 are taken.
- Tape recorders near some orbs play a code as sounds (up / left / right).

## Steps

- Pulling an orb [code: SecretPod.cs:555-589]: grab its handle (action held) and pull the stick in its pull direction (more than about 0.55 of the way) for 0.7 s (1 s in the sewer one). The light goes out and the hum stops.
- 0 Murky Waters, forest lake (5) [code]: a pillar in the water cave: grab it and rock it with the stick until it falls; climb over it; the platform smash opens the floor; pull the orb.
- 1 Field Research, farm fields (6) [code]: open the hatch in the field, take the ladder down, the orb is in the left basement room.
- 2 Wee Wee Wee, farm pigJump (8) [code]: a board into the secret pig pen breaks (`BoardBreak`); a long rope there.
- 3 Left Behind, city mindhatBasement (15) [code]: pull the vent grate (`VentHatchV2`, 0.75 s), pull the orb to the right.
- 4 Obscure Foundations, city rooftops2New (17) [code]: next to the tape recorder; pull the orb to the left.
- 5 Friends in Low Places, sewer SubmarineSecret (31) [code]: ram the block at the entrance with the submarine; two hats: hat 1 only for the boy, hat 2 can be worn by a controlled dude; the orb can be pulled by a controlled dude (`MindHatReceiverDevice`); the hat cannot be detached while dude 2 is in a scripted animation.
- 6 On the Other Side, beach to mines (35) [code]: from the chapter savepoint go back left in the water; the ladder breaks when the sub comes up; ram the scaffold at 5.5 m/s or more (boost); get out at the hatch and pull the orb.
- 7 Like Minded, mines (41 -> 42 -> 43) [code]: in the albino tank room tear the boards off with at least 2 albinos (`AlbinoMultiPullBoards`); ladder down; in the dark cave pick up the torch: the dogs fear it. Water or carrying it too far right puts it out and a dog attacks. Pull the orb at the far left; the dogs then leave.
- 8 Respite, lab GirlExplore (55) [code]: a vent (`VentHatchSecret`) next to it; a tape recorder 11 m away.
- 9 Unfathomable, lab diveToLab (57) [code]: underwater behind a gate with two handles A and B: both must be pulled.
- 10 Clockwork Contraptions, lab rotateTheShadow (59) [code]: the orb is on the moving wheel blocker (`WheelBlocker_Thing2`).
- 11 Room for Reflection, lab JetCrateToCeilingWater (62) [code]: a secret lift: its button sends it down / up; after the orb the lift goes down.
- 12 Office Space, offices AlbinosOnElevator (65) [code]: on the ceiling above a ledge; a jump-aim trigger right below it (`BoyJumpAimTrigger`) [the jump or throw route is inferred]; tape recorder 3.3 m away.

## Solution

- World positions (x, y) [code]: orb 0 lake about (-108, -217) (egg cave); 1 fields (70.1, -230.5); 3 mindhatBasement (557.2, -232.4); 4 rooftops (700.1, -261.3); 5 sub secret (1699.2, -582.6), block at (1738.4, -563.9); 6 beachToMines x 1801.8 (savepoint at x 2013); 7 torch cave x 1961, dog trigger at x 1999.9, torch put out at x 2017.5 / 2020 / 2026.9; 8 GirlExplore (2752.3, -649.3); 9 diveToLab (2884.7, -751.4), gate (2930.8, -774.0); 10 rotateTheShadow (3055.8, -758.1); 11 ceiling water (3188.6, -750.0); 12 albino lift (3361.5, -748.9); big orb room (3740.4, -756.4).
- **The code** [code: SecretJoystick.cs:657-685, SecretTape.cs:85-123]: up, right, left, left, left, up, up, right, left, right, right, right, up, up (14 flicks; any rotation of the sequence is accepted). The tapes play it as up / left / right sounds every 3 s; their position in the loop depends on time.
- **The big orb** [code: SecretPodLarge.cs:203-483]: with all 13 bits the room opens; near the orb the stick is slowed up to 20 %. Pulling it switches the light off and activates the wrist secret (`WristSecret.Activate`).
- **The secret ending** [code]: in the farm basement the joystick accepts the code only while the wrist secret is active; the gate opens, a tunnel leads to the final room; lift the door (`OpenElevatorDoors`), pull the wire twice (1.5 s, then 2 s): the room goes black, orb progress is reset and a new game starts (see Warnings).
