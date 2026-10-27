---
name: mechanics/shockwave
title: The shockwave (mines)
description: Mines shockwave: 6 s music cycle, cover.
order: 27
related: [safety/timing, mines/overview]
---
Status: timings read in the game code ([code], `ForcePushManager`, `ForcePushCover`, `HeadlessKillTrigger`, `MinesRotater`); the relation to step mode is [inferred] from code and must be checked in play.

## Rules

- **It follows the music, not game time** [code: ForcePushManager.cs:78-130, 287-316, 362-375]: one cycle is 6 s of music position. The warning comes at 5.55 s (the wall of air starts far away), the blow at 5.9 s, reset at 2.5 s. A blow kills a body that is in a deadly zone and not in cover, at least 0.2 s after a load.
- **Step mode works here** (safety/timing): near the shockwave every step-mode run is paced to real time and the music pauses while the game is frozen, so the cycle stays with your steps (results say `paced: ...`). `observe.hazards` gives the shockwave's `eta_s` to the next blow and `safe` when the body is in cover. Realtime (`time {op:"realtime"}`) works too, but then the wave keeps coming while you think.
- **Cover** [code: ForcePushCover, 19 in the mines]: stand fully behind a solid cover object (walls, shields, crates, the moving cover). Some covers move or turn: the rotator (`MinesRotater`, forcePushRotator) and the sliding cover pushed by each blow (`ForcePushMovingCover`, forcePushMovingCover).
- **Head height matters** [code: HeadlessKillTrigger.cs:84-140]: in forcePushGetWagon three shield zones kill when only the head sticks out of cover at the blow (the shields flip with a lever).
- **Holding a crate at the blow** with the stick pushed (> 0.4) makes the boy stumble [code: forcePushGetWagon CrateShockwave].
- **Stuck helper** [code: forcePushRotator BarrelAtBeam_After]: standing 15 s in one spot after the savepoint drops a barrel at the beam.
- **No music, no wave** [inferred, AUD 05 section 2.1]: the cycle reads the Wwise music position; if the music does not play the wave may not come at all.

## Hint

- Move between covers right after a blow (you have about 5 s), never during the last second before the next one.

## Steps

- Loop from cover to cover, one call per dash: `act {when:{hazard_eta:{target:"<shockwave id>", min_s:4.5}}, inputs:[{stick:[1,0], steps:150}], stop_on:["death","load"]}`. The call waits in cover until the blow has passed (4.5 s or more to the next one), then runs at once, inside the same call. Keep each dash under about 4 s (240 steps) and stop behind the next cover (`move_to {x, when:{...}}` does the same with the navigator).
- Before the next dash, `observe`: the shockwave row must show `safe` (in cover); if not, step back first.
- `when:{music_time:{from:0.1, to:1.0}}` waits for a window of the 6 s music cycle instead (cycle 0 = the running shockwave's own cycle).
