---
name: safety/rules
title: Safety rules (saves, Steam, loads, streaming)
description: Saves, Steam, loads, streaming: read once.
order: 10
related: [safety/timing, mechanics/death]
---
Status: rules read in the game code ([code]) and enforced by the tools; the save lock and the quiet jump are [inferred] and not tested in play yet.

## Rules

- **One save, in the Steam Cloud.** INSIDE has one slot, `INSIDE.sav` (2048 bytes). A chapter load, `level load_chapter`, `level reload kind chapter | newgame` and every savepoint the boy walks through write it, and unlocked chapters are never locked again [code: GameProgressionPlatform.cs:66-73]. The save does not matter here: loads, teleports and savepoints need no `confirm` and make no backup. Only when the user set EditorState `play.save_guard` = 1 do the tools that write it need `confirm:true` and back the save up to `_mod/save_backups` first.
- **Respawn is free.** `checkpoint {op:"reload"}` and dying both reload the last checkpoint without writing anything new [code: GameManager.Respawn, Load.Respawn].
- **Cheats are the user's call.** `checkpoint goto | next | prev` work only when the user set EditorState `play.cheats` = 1. Do not ask for it on your own; play from where you are.
- **Never touch achievements.** Orbs are achievement bits; local bits are pushed to Steam at the next sign-in [code: AchievementSteam.cs:102-115]. `save {op:"orbs"}` edits need `confirm:true` and the user's request.
- **One load at a time.** A load asked while another runs is dropped by the game [code: GameManager.cs:318-324]; the tools answer busy. After a load, observe again: ids from before are stale.
- **Bodies are re-created on every respawn** [code: SpawnManager.cs:153-215]. Never keep an old id; observe after every load or death.
- **Streaming follows the camera.** A camera moved away from the boy unloads his area after about 4 s of game time. Camera tools pin the boy's subscene; `restore` unpins.
- **No OS input.** Input goes only through act / interact / move_to (AgentCore). `keyhold` and `focustest` are refused.
- **Volume.** Master-volume RTPCs only go to 0 (mute); nothing raises the game volume.
- **When you stop**, call `restore`: it releases input, unfreezes, gives the camera back and closes panels.
