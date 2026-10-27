---
name: safety/timing
title: Step mode, realtime and music-timed hazards
description: Step mode vs realtime; music-timed hazards; rehearse-then-take input tracks.
order: 11
related: [mechanics/shockwave, safety/rules]
---
Status: clock rules from the tool design and the game code ([code]); one-step-per-frame stepping is [inferred] until tested in play.

## Rules

- **Step mode is the default.** The game is frozen (time scale 0) between calls and runs only inside act, move_to, interact, wait_until and `time {op:"step"}`. 60 logic steps = 1 s of game time. Think as long as you need.
- **Freezing at exactly 0 is safe everywhere.** Stepping and slow motion are not safe where a hazard follows the Wwise music clock instead of game time: the music keeps its own position, so the hazard drifts against your steps.
- **Music-timed places** [code]: the mines shockwave (`ForcePushManager`, 6 s music cycle, chapters mines and the lab elevator ride), the lineup march in the city (`WaitForMusicTime`, `_SleepWalkerMarchLogic`, lineup), the lab elevator fall (`ElevatorFall` runs on the music timer) and the lab flush tunnel (`AudioManagerFlushTunnel`, a 16 s music cycle). Results carry the warning `music_timed: <what>` near them.
- **Step mode is paced there** [code: Stepper]: near a music-timed hazard every step-mode run follows real time at 60 frames per second (one logic step per frame), and the music pauses while the game is frozen, so game time and the music stay together. Results then carry `paced: ...`; "late" frames mean the game rendered below 60 fps and fell behind. Realtime (`time {op:"realtime"}`) also works.
- **Wait, then act, in one call:** `act`, `move_to` and `interact` take `when` (one wait_until condition) and wait for it inside the same call before the input starts: `{hazard_eta:{target:"<id>", min_s:4}}` (at least 4 s before the hazard can kill, e.g. right after a shockwave blow), `{music_time:{from, to, cycle}}` (a music window), `{hazard_safe:"<id>"}`, `{fsm_state:{...}}`. No model think time falls between the wait and the move. If the condition does not hold within `when_timeout_s`, the call ends with outcome timeout and runs no input. During the wait the held input (hold, a grab) stays pressed.
- **Loads:** the clock never freezes during a load or before the game is ready (a freeze then is applied after the load). Every load resets the game's time scale; the stepper re-applies its mode.
- **Real time vs game time:** a few things use real time or the PC clock (bird waits in the farm, the CEO office clocks); none of them blocks progress.
- **Rehearse, then take (input tracks):** `input_track {op:"start", name}` records every logic step of input (and where the body was) while you play in step mode. Then `stop`, `info` (samples to choose a range), `trim {from, to}`, `save {name}`. For the take load the same checkpoint, wait until ready and `input_track {op:"play", name}`: the same input runs at the rehearsal's lockstep (captureFramerate 60, one logic step per frame, paced to real time), no 600-step cap, input only: the game replays the rehearsal by itself and the drift is measured. `correct:true` nudges the body toward the recorded path (not for final takes). It is a job: if the result says playing, `wait_until {any:[{job:"<id>"}], timeout_s:50}`; `input_track {op:"stop"}` or restore cancels. Read `playback.max_drift_m`; a large drift means the run went differently (a missed jump or smash).
- **Recording a take:** a manual `srec record start` may run during play. The scene recorder owns the frame pacing, `time` keeps freeze and speed, the track owns only the input; `time` state shows `recording` and warns that step-mode runs then follow the recorder's clock. Start the recording between calls, right before play.
