# InsideDev 0.11.0 command, config and MCP tool reference

Generated from the source code of InsideDev 0.11.0 by gen_commands.py; do not edit by hand.

- Bridge and console commands: 130 (108 bridge commands, 22 console words; 9 forwarded), from src/InsideDev/CoreBHelp.cs
- Extension commands: 16 words, 57 usage lines, from 5 extension classes and the forwarded rows
- Config keys: 106 entries from 95 Describe call sites (loops expanded), 104 distinct keys, in 12 parts
- MCP tools: 41 tools served by the game (play 15, dev 34, all 41), plus 1 answered by the stdio launcher

## Contents

- [1. Bridge and console commands](#1-bridge-and-console-commands)
- [2. Extension commands](#2-extension-commands)
- [3. Config keys](#3-config-keys)
- [4. MCP tools](#4-mcp-tools)

## 1. Bridge and console commands

The command table of src/InsideDev/CoreBHelp.cs. Every word works through the file bridge, the MCP command tool and the in-game console. Marker console: a word of the in-game console that the bridge passes on. Marker forwarded: handed to another part that has its own argument checks and help (see section 2). Min args: the least number of arguments a call needs.

Groups: [basics](#commands-basics), [window and render](#commands-window-and-render), [editor](#commands-editor), [boy and camera](#commands-boy-and-camera), [objects](#commands-objects), [state machines, signals, code](#commands-state-machines-signals-code), [history and mods](#commands-history-and-mods), [audio](#commands-audio), [memory](#commands-memory), [recording](#commands-recording), [console](#commands-console)

### Commands: basics

| Command | Usage | Text | Min args | Marker |
|---|---|---|---|---|
| `ping` | `ping` | is the tool there: answers pong and its version |  |  |
| `help` | `help [command]` | every command, or usage and text of one |  |  |
| `state` | `state [json]` | snapshot: character, savepoint, streaming, nearest triggers, audio, console tail; json = one object |  |  |
| `where` | `where [json]` | the boy's position and the current savepoint; json = one object |  |  |
| `boypos` | `boypos` | position of the boy |  |  |
| `doctor` | `doctor [json]` | self-check of every part: versions, loader, folders, free disk, ffmpeg, window, time scale, errors |  |  |
| `cfg` | `cfg [list] [filter] [json] \| cfg get <key> [json] \| cfg set <key> <value> \| cfg reset <key>` | settings of \_mod\\editor.cfg: value, default, owner, text; set saves, reset returns to the default |  |  |
| `background` | `background [status\|auto\|always\|off] [json]` | when the background scanners run: auto = only while the tool is in use, always = as before 0.11.0, off = only on request |  |  |
| `quit` | `quit` | closes the game the way its own exit does |  |  |
| `remote` | `remote [on\|off]` | file bridge (\_mod\\cmd) on or off for this session |  |  |
| `screenshot` | `screenshot [tag]` | PNG of the picture into \_mod\\cmd\\shot\_&lt;time>\[\_tag\].png at the end of the frame |  |  |
| `clip` | `clip [text]` | shows the clipboard, or puts text on it |  |  |
| `ext` | `ext [load] [json] \| ext json` | loaded extension modules, their cost and levels; load picks up new \_mod\\InsideDev.\*.dll files without a restart |  |  |
| `live` | `live ...` | commands of the live link extension |  | forwarded |

### Commands: window and render

| Command | Usage | Text | Min args | Marker |
|---|---|---|---|---|
| `diag` | `diag [json]` | report of the editor's renderer |  |  |
| `cams` | `cams [json]` | every camera with its targets and order |  |  |
| `render` | `render [auto\|eof\|overlay\|gamecam\|probe\|reset\|snapshot\|board [on\|off]]` | backend of the editor's own drawing |  |  |
| `bg` | `bg [on\|off]` | Application.runInBackground: keep the game running while its window is not focused |  |  |
| `fullscreen` | `fullscreen [on\|off]` | full screen or window |  |  |
| `res` | `res <w> <h> [full]` | screen resolution | 2 |  |
| `postfx` | `postfx [json] \| postfx on\|off\|default <Type> \| postfx set <Type> <field> <value>` | post effects of the gameplay camera |  |  |
| `perf` | `perf [json] \| perf reset [json] \| perf enable <subsystem> \| perf hide <roots> [part] \| perf show` | frame time of each editor subsystem, the background mode and the garbage per frame |  |  |
| `focustest` | `focustest <n> [awayMs] [backMs] [alttab\|minimize] [bg on\|off] [force] \| focustest stop \| focustest status [json]` | switches the real window focus away and back (sends OS input; force sends the keys although the game window could not be brought to the front) |  |  |
| `keyhold` | `keyhold [right\|left\|up\|<hex vk>] [ms] [text] \| keyhold status` | holds a real OS key (sends OS input); status = the last hold |  |  |

### Commands: editor

| Command | Usage | Text | Min args | Marker |
|---|---|---|---|---|
| `panel` | `panel [on\|off] [tab#]` | editor panel open or closed |  |  |
| `ui` | `ui [status] \| ui scale <f> \| ui layout dock\|window\|list \| ui layout save\|load\|delete <name> \| ui reset \| ui show <panel> \| ui dock left\|right\|bottom <fraction>\|on\|off \| ui window <x> <y> <w> <h> \| ui put <panel> left\|right\|bottom \| ui docks [json] \| ui focus <id>\|none \| ui scroll <id> <y> \| ui record [on\|off] \| ui find\|clicktext\|hovertext <text> [nth] \| ui widgets [filter] \| ui click\|rclick\|hover <x> <y> \| ui renderer gl\|mesh` | layout of the editor and scripted clicks on its widgets |  |  |
| `uifield` | `uifield` | text of the Inspector's find field |  |  |
| `palette` | `palette <query> [#n]` | hits of the command palette; #n opens hit n |  |  |
| `cmd` | `cmd [id]` | editor commands (undo, pick, open-&lt;panel> ...); with an id runs one |  |  |
| `mode` | `mode [author\|explore]` | Mod Author or Explore mode |  |  |
| `labels` | `labels [off\|selected\|nearby\|all\|on] \| labels types <0..50\|all>` | label level of the world overlay; types = component names shown per label (the selected object shows all) |  |  |
| `scope` | `scope [selected\|1\|2\|nearby\|all]` | how far the overlay follows the selection's relations |  |  |
| `pick` | `pick \| pick on\|off \| pick filter <name> \| pick status \| pick <x> <y> [look]` | pick the object under a screen point (no arguments: under the boy); look = report only, the selection stays |  |  |
| `gizmo` | `gizmo [on\|off] \| gizmo global on\|off` | move handles of the selection |  |  |
| `hud` | `hud [on\|off]` | the editor's status line |  | forwarded |
| `clean` | `clean [on\|off]` | hides every editor drawing for a clean picture |  | forwarded |
| `gamma` | `gamma <0..1>\|reset` | the game's brightness setting |  | forwarded ([gamma](#ext-gamma)) |
| `seed` | `seed <n>` | seeds the game's random numbers |  | forwarded ([seed](#ext-seed)) |

### Commands: boy and camera

| Command | Usage | Text | Min args | Marker |
|---|---|---|---|---|
| `tp` | `tp <x> <y> [z] \| tp <obj>` | teleports the boy to a position (two numbers keep his depth) or to an object | 1 |  |
| `cam` | `cam \| cam status [json] \| cam look <target> [dist] [yaw] [pitch] [fov] [seconds] [point=<name>] [size=<f>] \| cam back \| cam pose <x> <y> <z> <yaw> <pitch> [fov] [seconds] [roll] \| cam fov <deg> \| cam roll <deg> \| cam json [name] \| cam off` | the camera: describe it, aim it at an object, pose it, step back to the pose before, give it back |  |  |
| `freecam` | `freecam [on\|off]` | free camera |  |  |
| `zoom` | `zoom [factor\|reset]` | zoom of the game camera |  |  |
| `cspawn` | `cspawn [list] \| cspawn save [name] \| cspawn go\|respawn\|del <n>` | your own spawn points |  |  |

### Commands: objects

| Command | Usage | Text | Min args | Marker |
|---|---|---|---|---|
| `inspect` | `inspect <obj> [deep]` | components, fields, state machines and signal wires of an object |  |  |
| `select` | `select <obj>` | selects an object in the editor |  |  |
| `sel` | `sel \| sel back\|fwd\|clear \| sel <obj>` | the selection: show it, walk its history, clear it, or select an object |  |  |
| `children` | `children <obj>` | direct children of an object |  |  |
| `near` | `near [radius] [filter]` | objects around the boy, nearest first |  |  |
| `triggers` | `triggers [radius] \| triggers on\|off` | triggers around the boy; on\|off switches the trigger overlay |  |  |
| `hiddennear` | `hiddennear [radius]` | inactive objects around the boy |  |  |
| `hidden` | `hidden [status\|on\|off\|save\|reset] [json] \| hidden scan [seconds]` | the passive scanner for hidden objects and its database; on / off persist hidden.passive |  | forwarded ([hidden](#ext-hidden)) |
| `db` | `db stats [json] \| db find <query> \| db rec <obj> \| db selector <obj> \| db resolve <selector> \| db budget <ms> \| db dirty` | the object database behind every search |  |  |
| `sfind` | `sfind <words> [cat:animation\|sounds\|scripts\|triggers\|fsm\|characters] [clutter] [path]` | ranked search as in the Inspector's find field |  |  |
| `setactive` | `setactive <obj> on\|off` | activates or deactivates an object (undoable) | 2 |  |
| `enable` | `enable <obj> <Component> on\|off` | enables or disables a component (undoable) | 3 |  |
| `get` | `get <obj> <Component> <field>` | value of a field or property | 3 |  |
| `set` | `set <obj> <Component> <field> <value...>` | writes a field or a readable property (undoable) | 4 |  |
| `call` | `call <obj> <Component> <method>` | calls a method without parameters | 3 |  |
| `anim` | `anim <obj> [rebind]` | legacy animation states of an object; rebind rebuilds their bindings | 1 |  |
| `reparent` | `reparent <obj> <newParent\|none\|up\|restore>` | moves an object in the hierarchy, keeps its world position | 2 |  |
| `move` | `move <obj> <x> <y> <z>` | moves an object (undoable) | 4 |  |
| `movehere` | `movehere <obj> [dx] [dz]` | moves an object next to the boy | 1 |  |
| `resetpos` | `resetpos all\|<obj>` | puts moved objects back | 1 |  |
| `mats` | `mats <obj>` | materials and shader values of an object |  |  |
| `origin` | `origin <obj> [deep]` | the object in the level file compared with the live one |  |  |
| `refs` | `refs <obj> [all] \| refs stats` | what an object refers to |  |  |
| `refby` | `refby <obj> [all]` | what refers to an object |  |  |
| `chain` | `chain <obj> [depth] [back]` | what an object leads to through references (back: what leads to it) |  |  |
| `path` | `path <a> <b> [any]` | shortest reference path between two objects | 2 |  |
| `paths` | `paths <a> <b>` | all reference paths between two objects | 2 |  |
| `graph` | `graph <hops> [f\|s\|m\|o\|c\|all] \| graph look [mode] [hops] [filter] \| graph mode <m> \| graph live [s f a n\|all\|none]` | the relation graph around the selection (c = code edges); look = text only, the Graph panel is not changed |  |  |
| `events` | `events [n] [filter] [json] \| events on\|off\|auto\|clear\|status [json]` | the event monitor: signals, FSM changes, sound posts; on / off persist events.monitor, auto = only while in use |  |  |
| `bookmark` | `bookmark add [note] \| bookmark list \| bookmark go <n> [tp] \| bookmark del <n>` | bookmarks of objects |  |  |

### Commands: state machines, signals, code

| Command | Usage | Text | Min args | Marker |
|---|---|---|---|---|
| `fsm` | `fsm <obj> [fsmName] event\|state\|var <name> [value (undoable)] \| fsm <obj> wire <state\|*> <event> <toState> \| fsm <obj> unwire <state\|*> <event> \| fsm <obj> resetwires \| fsm <obj> events` | drives or rewires a PlayMaker state machine | 2 |  |
| `fsmgraph` | `fsmgraph <obj> [fsmName]` | dump of a state machine; opens it in the FSM Graph panel |  |  |
| `fsmfind` | `fsmfind <text>` | search in every loaded state machine |  |  |
| `fsmset` | `fsmset <fsm obj>\|<state>\|<action index>\|<field>\|<value>` | changes a setting of one action (field "(enabled)" = action on / off) |  |  |
| `sigs` | `sigs <obj>` | signal inputs of the state machines on an object |  |  |
| `sigwire` | `sigwire <fsm obj>\|<signal in>\|<sender obj>\|<signal out>` | connects a signal output to a state machine input |  |  |
| `sigunwire` | `sigunwire <fsm obj>\|<signal in>\|<sender obj>\|<signal out>` | removes such a connection |  |  |
| `signal` | `signal <obj> <signalOut>` | fires every connection of a signal output | 2 |  |
| `signalin` | `signalin <obj> <signalIn>` | calls a signal input directly | 2 |  |
| `logic` | `logic <Type\|object> \| logic shared [filter] \| logic anim [filter]` | what the game code of a type reads, writes and calls |  |  |
| `codedeps` | `codedeps [filter]` | which scripts depend on which |  |  |
| `gamecode` | `gamecode` | state of the game code dump |  |  |

### Commands: history and mods

| Command | Usage | Text | Min args | Marker |
|---|---|---|---|---|
| `history` | `history [n] [json]` | the last changes and the net changes |  |  |
| `undo` | `undo [n] \| undo to <id>` | takes back the last change, n changes, or every change back to entry #id |  |  |
| `redo` | `redo [n]` | applies it again |  |  |
| `revert` | `revert all [dry] \| revert <n> [dry] \| revert <label text> [dry]` | puts everything, net change n of history, or the net changes whose label holds the text back to the game's value; dry lists only |  |  |
| `mod` | `mod list\|reload [json] \| mod record <name> [description] \| mod show\|enable\|disable <name> [json] \| mod dry\|validate\|delete <name>` | mods made from recorded changes |  |  |

### Commands: audio

| Command | Usage | Text | Min args | Marker |
|---|---|---|---|---|
| `audio` | `audio rules \| audio native [on\|off] \| audio mute\|unmute\|nuke\|unnuke <event> \| audio replace <event> <with> \| audio force switch\|state\|rtpc <name> <value> \| audio clear <key\|all>` | rules that change what the game's sound engine gets |  |  |
| `audiolog` | `audiolog [n]` | the last sound events the game posted |  |  |
| `waits` | `waits` | state machines that wait for a music cue, beat or time |  |  |
| `timed` | `timed [json]` | components with music-timed triggers and their times |  |  |
| `timer` | `timer <Type> <field> <seconds\|mute\|unmute\|fire>` | retimes, mutes or fires one music-timed trigger (undoable) | 3 |  |
| `music` | `music` | music position, loop length, beat, bar |  |  |
| `adb` | `adb [stats] [json] \| adb export \| adb exportstatus \| adb find <text> \| adb filter <F> [n] \| adb show\|select <name\|id>` | the audio database: events, banks, where they are used |  |  |
| `wwise` | `wwise [stats] [json] \| wwise stacks [off\|important\|full] \| wwise version \| wwise stock \| wwise api [filter] \| wwise watched [filter] \| wwise level <0-3> \| wwise hash <name> \| wwise pid <id> \| wwise trace [n] [filter] \| wwise objs [filter] \| wwise banks \| wwise ids` | the native trace of calls into the sound engine |  |  |
| `sounds` | `sounds [filter] \| sounds audition <event> \| sounds show <event> \| sounds override <event> <binding#> <newEvent> \| sounds revert\|post\|trigger <event> <binding#>` | the sound library: who plays which event |  |  |
| `acap` | `acap status [json] \| acap preflight \| acap start <name> \| acap mark <ascii> \| acap stop \| acap result [json] \| acap guard on\|off \| acap output audible\|muted \| acap suspend on\|off` | capture of the game's audio output |  | forwarded ([acap](#ext-acap)) |

### Commands: memory

| Command | Usage | Text | Min args | Marker |
|---|---|---|---|---|
| `mem` | `mem [obj\|on\|off]` | memory addresses of an object's fields (for Cheat Engine) |  |  |
| `memwhat` | `memwhat <hex>` | which object an address belongs to |  |  |
| `memmethod` | `memmethod <hex>` | which method an address belongs to |  |  |
| `memjit` | `memjit <Type> <method> [+hex]` | address of a compiled method |  |  |
| `memread` | `memread <hex> [bytes]` | reads memory, at most 4096 bytes |  |  |
| `memexport` | `memexport` | address table of the selection for Cheat Engine |  |  |

### Commands: recording

| Command | Usage | Text | Min args | Marker |
|---|---|---|---|---|
| `mcam` | see [mcam](#ext-mcam) | MultiCam: multi-camera captures and replays from a spec |  | forwarded ([mcam](#ext-mcam)) |
| `srec` | see [srec](#ext-srec) | scene recorder and viewer |  | forwarded ([srec](#ext-srec)) |

### Commands: console

| Command | Usage | Text | Min args | Marker |
|---|---|---|---|---|
| `spawn` | `spawn <sub> <sp>` | loads a savepoint through the game |  | console |
| `load` | `load <sub> <sp>` | the same as spawn |  | console |
| `tpsp` | `tpsp <sub> <sp>` | teleports to a savepoint without a load |  | console |
| `subs` | `subs [filter]` | the areas (subscenes) with their savepoint counts |  | console |
| `levels` | `levels [filter]` | the build's scene files and their streaming state |  | console |
| `discover` | `discover all \| discover <sub>` | loads areas once so their savepoints are known |  | console |
| `catalog` | `catalog` | loads every area once and records its audio triggers |  | console |
| `post` | `post <event>` | posts a Wwise event |  | console |
| `cue` | `cue <name>` | injects a music cue or marker |  | console |
| `shockwave` | `shockwave nokill\|fire` | the shockwave: switch its kill off, or fire it now |  | console |
| `kill` | `kill` | kills the boy |  | console |
| `god` | `god [on\|off]` | god mode |  | console |
| `ts` | `ts <scale>` | time scale |  | console |
| `find` | `find <name>` | search by name (fills the Inspector's list) |  | console |
| `respawn` | `respawn` | respawn at the current savepoint |  | console |
| `reload` | `reload` | reloads the scene |  | console |
| `solids` | `solids [on\|off]` | overlay of solid colliders |  | console |
| `savepoints` | `savepoints [on\|off]` | savepoint markers |  | console |
| `flag` | `flag <name> [on\|off]` | one of the game's debug flags |  | console |
| `flags` | `flags` | all debug flags |  | console |
| `atlas` | `atlas \| atlas find <text> \| atlas level <n> \| atlas check` | the level atlas |  | console |
| `clear` | `clear` | empties the console |  | console |

## 2. Extension commands

Words added by the extension DLLs (classes that implement IInsideDevExtension; the core hands every word it does not know to them), and the sub-command help of the forwarded rows of section 1. Usage lines come from the Help and Usage string constants, the Help / Usage methods, the "usage: ..." replies and the test tables in the named source files; a usage reply already contained in the main help is not repeated.

| Extension class | Source | Words |
|---|---|---|
| `DetExtension` | src/det/DetCommands.cs | `det` |
| `McpExtension` | src/mcp/Extension.cs | `mcp` |
| `CtlExtension` | src/play/Ctl.cs | `ctl` |
| `PlayExtension` | src/play/PlayExtension.cs | `rec`, `campath`, `grec`, `pano`, `vsync`, `play` |
| `ReplayLabExtension` | src/replaylab/ReplayLab.cs | `lab` |

- Source comment in src/mcp/Extension.cs (line 27): `Bridge / console: mcp [status [json]\|on\|off\|port <n>\|jobs\|providers\|sessions [json]\|restore\|selftest\|test]`
- Source comment in src/mcp/Extension.cs (line 28): `Console only (they decide who may use the server): mcp token new\|clear\|show, mcp tools [<allowlist>\|clear], mcp hosts [<names>\|clear]`
- Source comment in src/play/PlayExtension.cs (line 11): `Bridge / console: play [status [json] \| tools \| restore [list [json]]], rec start\|stop\|status (Recorder.cs), vsync off\|on\|auto\|status [json] (Stepper.cs), pano <file> ... \| status (Pano.cs)`

Forwarded rows of section 1 without separate help text in the code: `live`, `hud`, `clean`.

<a id="ext-det"></a>

### Word: det

Dispatched by DetExtension in src/det/DetCommands.cs.

| Usage | Text | Source |
|---|---|---|
| `det prep [anim=default\|always] [cull=live\|freeze] [stream=live\|keep] [drip=on\|off] \| det prep full \| det prep default` |  | src/det/DetCommands.cs:42 (DetCommands.Help) |
| `det cue <k> <command> \| det cues [clear]` |  | src/det/DetCommands.cs:42 (DetCommands.Help) |
| `det run <name> <out> [kmax] [kmax=n] [fps=60] [cam=...]` |  | src/det/DetCommands.cs:42 (DetCommands.Help) |
| `det replay <source> <out> [cam=x,y,z,yaw,pitch,fov \| cam=x y z yaw pitch fov \| cam=<run>] [kmax=n] [fps=60]` |  | src/det/DetCommands.cs:42 (DetCommands.Help) |
| `det status \| det zone \| det abort` |  | src/det/DetCommands.cs:42 (DetCommands.Help) |
|  | &lt;out>: absolute folder, or relative to \_mod/det; &lt;source> / cam=&lt;run>: a folder path, or a run folder next to &lt;out> | src/det/DetCommands.cs:42 (DetCommands.Help) |
| `det cue <k >= 0> <command>` |  | src/det/DetCommands.cs:82 |
| `det replay <source> <out> [cam=...] [kmax=n] [fps=n] [overwrite=on]` |  | src/det/DetCommands.cs:154 |
| `det run <name> <out> [kmax] [kmax=n] [fps=n] [cam=...] [overwrite=on]` |  | src/det/DetCommands.cs:154 |

<a id="ext-mcp"></a>

### Word: mcp

Dispatched by McpExtension in src/mcp/Extension.cs.

Sub-commands handled in src/mcp/Extension.cs (case labels of the dispatcher): `on`, `off`, `port`, `restore`, `jobs`, `providers`, `sessions`, `token`, `tools`, `hosts`, `selftest`, `test`.

| Usage | Text | Source |
|---|---|---|
| `mcp port <1024-65535>` |  | src/mcp/Extension.cs:386 |
| `mcp token new\|clear\|show` |  | src/mcp/Extension.cs:483 |

<a id="ext-ctl"></a>

### Word: ctl

Dispatched by CtlExtension in src/play/Ctl.cs.

| Usage | Text | Source |
|---|---|---|
|  | ctl: drive the boy / huddle through AgentCore (no OS input, no window focus). &lt;t> = seconds (1.2) or logic steps (72s; 60 steps = 1 s). | src/play/CtlCore.cs:43 (CtlCore.Help) |
| `ctl status [json] \| ctl job [jN] [json] \| ctl wait [jN] [<seconds>] \| ctl stop \| ctl help` |  | src/play/CtlCore.cs:43 (CtlCore.Help) |
| `ctl stick <x> <y> [for <t>]   ctl press <jump\|action\|grab ...> [for <t>]   ctl tap <jump\|action\|grab ...> [<t>]` |  | src/play/CtlCore.cs:43 (CtlCore.Help) |
| `ctl hold <left\|right\|up\|down\|stick <x> <y>\|jump\|action\|grab ...>   ctl release [all \| stick\|jump\|action\|grab ...]` |  | src/play/CtlCore.cs:43 (CtlCore.Help) |
| `ctl run left\|right [<t>]   ctl walk left\|right [<t>]   ctl jump [left\|right] [<t>]   ctl grab on\|off   ctl swim <dir> [<t>]` |  | src/play/CtlCore.cs:43 (CtlCore.Help) |
| `ctl goto <x> [<y>] [walk]  (move_to navigator)   ctl stopat <x> [walk]  (plain stick, stops when the body crosses x)` |  | src/play/CtlCore.cs:43 (CtlCore.Help) |
| `ctl seq "<script>"  R L U D <t>[@0.3..1] stick, J [<t>] jump, A [<t>] action (no <t>: a 0.1 s tap), G <t> grab, W <t> wait, + = together` |  | src/play/CtlCore.cs:43 (CtlCore.Help) |
|  | e.g. ctl seq "R1.2 J R0.4+J G1 W0.5 L0.3" | src/play/CtlCore.cs:43 (CtlCore.Help) |
| `ctl track rec [name] \| stop \| save <name> [overwrite] \| play [name] [speed <k>] [freeze] [correct\|nocorrect] [free] \| list \| info [name] \| status \| load <name> \| trim <from> [<to>]` |  | src/play/CtlCore.cs:43 (CtlCore.Help) |
| `ctl time [state] \| freeze \| run [<k>] \| step <n> \| speed <k>   ctl mode agent\|mix\|human` |  | src/play/CtlCore.cs:43 (CtlCore.Help) |
|  | Without &lt;t>, stick / press / run / walk / swim hold until ctl release. Timed commands run as a job: the reply says job=jN, ctl job jN shows it. | src/play/CtlCore.cs:43 (CtlCore.Help) |

<a id="ext-rec"></a>

### Word: rec

Dispatched by PlayExtension in src/play/PlayExtension.cs.

| Usage | Text | Source |
|---|---|---|
| `rec start [fps 5..60] [cq 0..51] [file] [every=<n>] [enc=auto\|<encoder>] [desktop [raise] [output=<n>]] \| rec stop [review_fps] [wait] \| rec status [json] \| rec wait` |  | src/play/Recorder.cs:36 (Recorder.Usage) |
| `rec stop [review_fps 0..30] [wait]` |  | src/play/Recorder.cs:530 |

<a id="ext-campath"></a>

### Word: campath

Dispatched by PlayExtension in src/play/PlayExtension.cs.

| Usage | Text | Source |
|---|---|---|
| `campath load <dir with poses.csv and meta.csv> [camera] [offset_s] [rt] [fps=<output rate>]` |  | src/play/CamPath.cs:78 (CamPath.LoadUsage) |
| `campath mount <transform path\|off\|status> [lx ly lz] [rx ry rz] [fov] [near] [smooth 0..1] [into_scene_deg] [level 0..1] [hide 0\|1] [tau_h tau_v tau_yaw tau_pitch s] [pitch_max_deg] [free_up_deg free_down_deg], or the same by name in any order: local=x,y,z rot=x,y,z fov= near= smooth= into= level= hide=0\|1 tau=h,v,yaw,pitch pitch= free=up,down` |  | src/play/CamPath.cs:379 (CamPath.MountUsage) |

<a id="ext-grec"></a>

### Word: grec

Dispatched by PlayExtension in src/play/PlayExtension.cs.

| Usage | Text | Source |
|---|---|---|
| `grec start [file.mkv] [every n-th frame, default 2] [cq 0..51, default 16] [enc=auto\|<encoder>] [fps=<rate of the file>] [times=off] \| grec stop [wait] \| grec status [json] \| grec wait` |  | src/play/GameRec.cs:47 (GameRec.Usage) |

<a id="ext-pano"></a>

### Word: pano

Dispatched by PlayExtension in src/play/PlayExtension.cs.

| Usage | Text | Source |
|---|---|---|
| `pano [file.png \| take] [size=4096 \| <size>] [x y z \| eye=x,y,z] [noVignette] [lens] [near=m] [warm=0..30] [samples=1..16] [faces=<dir>] [compress] [delay=frames] [live \| hold] [overscan=0..45 deg] [wait] \| pano status [json] \| pano wait \| pano help` |  | src/play/Pano.cs:42 (Pano.Help) |

<a id="ext-vsync"></a>

### Word: vsync

Dispatched by PlayExtension in src/play/PlayExtension.cs.

| Usage | Text | Source |
|---|---|---|
| `vsync [off \| on \| auto \| status] [json]` |  | src/play/PlayExtension.cs:106 |

<a id="ext-play"></a>

### Word: play

Dispatched by PlayExtension in src/play/PlayExtension.cs.

| Usage | Text | Source |
|---|---|---|
| `play restore [list [json]]` |  | src/play/PlayExtension.cs:134 |
| `play [status [json] \| tools \| restore [list [json]]]` |  | src/play/PlayExtension.cs:139 |

<a id="ext-lab"></a>

### Word: lab

Dispatched by ReplayLabExtension in src/replaylab/ReplayLab.cs.

| Usage | Text | Source |
|---|---|---|
| `lab help \| list \| status \| stop \| verdicts \| out <dir>` |  | src/replaylab/ReplayLab.cs:112 (Lab.Help()) |
|  | common args: cam=x,y,z,yaw,pitch\[,fov\] (free camera pose for the test), clean=on\|off (editor overlay, default on), thr=&lt;diff threshold>, timeout=&lt;s> | src/replaylab/ReplayLab.cs:113 (Lab.Help()) |
|  | results: &lt;outRoot>/&lt;test>/verdict.txt, result.json, log.txt, \*.jpg | src/replaylab/ReplayLab.cs:115 (Lab.Help()) |
| `lab l5 s1\|s3\|s3b\|s2 [secs=N] [level=100] [hb=<heartbeat.json>] [hbage=2] [force] [confirm] [wake]` | L5: Wwise output capture (S1 silent, S3 suspended + level, S3b with timeBeginPeriod(1), S2 muter only) (prep: InsideMuter running and writing its heartbeat, INSIDE session muted, `audio force rtpc rtpc_master_volume 0`; order S1, S3, then S2 only after S3 failed (needs confirm, and wake if S3 suspended Wwise in this session)) | src/replaylab/LabAudio.cs:30 (LabTest) |
| `lab l1 [frames=120] [chick=<obj>] [bale=<obj>] [wheel=<obj>] [cam=...]` | L1: write order (lateUpdate -400 vs Camera.onPreCull) and onPreCull call order (prep: ChickenGun after DoSpawn, frozen or running (setup of REPLAY\_DESIGN 3.3: load 7 0, tp 221.0 -221.4 0.66, OnElectricityOn, DoSpawn); a chick, the bale and MotorWheel loaded (on screen is not required)) | src/replaylab/LabCore.cs:19 (LabTest) |
| `lab l3 [trials=6] [chick=<obj>] [bale=<obj>] [cam=...]` | L3: rigidbody interpolation / kinematic and legacy Animation at timeScale 0 (prep: ChickenGun after DoSpawn, frozen or running (setup of REPLAY\_DESIGN 3.3: load 7 0, tp 221.0 -221.4 0.66, OnElectricityOn, DoSpawn); a chick and the bale loaded) | src/replaylab/LabCore.cs:26 (LabTest) |
| `lab l6` | L6: UnityEngine.Random.seed fingerprint (read does not consume, set back reproduces) (prep: any loaded level) | src/replaylab/LabCore.cs:33 (LabTest) |
| `lab l8 [frames=120]` | L8: RuntimeCallbacks instances and the once-per-frame guard (prep: any loaded level, game running (timeScale 1) so fixedUpdate fires too) | src/replaylab/LabCore.cs:40 (LabTest) |
| `lab l7 [cullers=3_Interior_CULL,1b_IndoorDetails_CULL] [frames=240] [period=120] [dist=4] [in=x,y,z,yaw,pitch[,fov]] [out=...] [zone=<root prefix>\|all]` | L7: forced cullers (valid=false) and the emulation UpdateBoundsCullers(c => false) + hide by IsVisble (prep: ChickenGun loaded (#chickenGun\_Environment); groups inactive at the start (camera outside their facing planes; the test places the camera itself unless in=/out= are given)) | src/replaylab/LabCull.cs:17 (LabTest) |
| `lab l12 [tag=<scene name>] [frames=30] [dx=1.5] [yaw=10] [eps=1e-5] [cam=...]` | L12: camera probe: transforms that change when only the camera moves (WaterDust, LensFlareSimple) (prep: run once in ChickenGun (tag=chickengun) and once in a water scene with WaterDust (tag=water); results in L12\_&lt;tag>) | src/replaylab/LabCull.cs:24 (LabTest) |
| `lab l2 [speed=2] [frames=12] [blurframes=24] [chick=<obj>] [bale=<obj>] [only=chick\|bale] [chickat=x,y,z] [baleat=x,y,z] [blurobj=bale\|chick] [thr=12] [settle=12] [cam=...]` | L2: skin timing per write point, velocity and frozen vs running engine blur (prep: ChickenGun after DoSpawn (REPLAY\_DESIGN 3.3 setup); camera (cam= or the current view) shows one chick and the bale with free space to their right (they move ~0.4 m / 0.8 m along screen x); runs 2 x blurframes frames at timeScale 1 (the game advances ~0.8 s)) | src/replaylab/LabRender.cs:21 (LabTest) |
| `lab l4 [view=game\|off\|both] [root=ChickenGunObject/FX_PipeSuck] [systems=a,b] [off=60] [edge=0.8] [maxz=-2] [pthr=8] [repeat=10] [settle=12] [cam=...]` | L4: particles: GetParticles, Pause, Clear, SetParticles on a frozen system, game camera and 60 deg off axis (prep: ChickenGun after DoSpawn (REPLAY\_DESIGN 3.3 setup) with suction on (ToggleSuctionOn) so FX\_PipeSuck and the straw systems have live particles; the game camera sees them) | src/replaylab/LabRender.cs:28 (LabTest) |
| `lab l9 [light=SpotLight_Machine_BoundsCuller] [bale=<obj>] [group=<culler name>\|none] [at=x,y,z] [frames=12] [step=0.05] [radius=2.5] [thr=10] [cam=...]` | L9: static shadow caches: bale shadow under SpotLight\_Machine as is / dirty / dynamic, hidden culler group shadow (prep: ChickenGun after DoSpawn (REPLAY\_DESIGN 3.3 setup); camera (cam=) sees the floor under the machine spotlight (the bale is moved there, at= overrides the spot)) | src/replaylab/LabRender.cs:35 (LabTest) |
| `lab l10 [objs=a,b] [words=water,fly,drip] [hold=3] [live=0.5] [pthr=8] [settle=12] [cam=...]` | L10: shader time while frozen (\_Time, \_RealTime) and the OnPreCull/OnPreRender override (prep: a view with water, flies or drips (ChickenGun: FX\_WaterDripDown2; a water scene for water); objs= names the renderers to measure) | src/replaylab/LabRender.cs:42 (LabTest) |
| `lab l11 [rope=RopeCrane_BoundsCuller/CraneRopeLong] [samples=12] [slow=5] [kick=0] [hold=1] [margin=2] [thr=12] [settle=12] [cam=...]` | L11: rope replay: RopeLineRenderer disabled, recorded vertices written, BlendRope.Draw by reflection (prep: ChickenGun after DoSpawn (REPLAY\_DESIGN 3.3 setup) (level171 crane rope loaded and on screen); the rope should swing (push it in game, or kick=&lt;impulse> adds one)) | src/replaylab/LabRender.cs:49 (LabTest) |

<a id="ext-gamma"></a>

### Word: gamma

A forwarded row of section 1 (handled by the core).

| Usage | Text | Source |
|---|---|---|
| `gamma <0..1> \| gamma reset` |  | src/InsideDev/Runtime/MultiCam.cs:586 |

<a id="ext-seed"></a>

### Word: seed

A forwarded row of section 1 (handled by the core).

| Usage | Text | Source |
|---|---|---|
| `seed <n>` |  | src/InsideDev/Runtime/MultiCam.cs:595 |

<a id="ext-hidden"></a>

### Word: hidden

A forwarded row of section 1 (handled by the core).

| Usage | Text | Source |
|---|---|---|
| `hidden scan [seconds 1..3600]` |  | src/InsideDev/Runtime/PassiveHidden.cs:663 |

<a id="ext-acap"></a>

### Word: acap

A forwarded row of section 1 (handled by the core).

| Usage | Text | Source |
|---|---|---|
| `acap status [json] \| preflight [suspend=auto\|on\|off] [muteronly=on] [hb=<heartbeat.json>] [hbage=2]` |  | src/InsideDev/Runtime/AudioCapture.cs:1292 (AudioCapture.Usage) |
| `acap start <name> [suspend=auto\|on\|off] [muteronly=on] [level=100] [hb=<heartbeat.json>] [hbage=2]   (to <game>/_mod/audio/<name>.wav)` |  | src/InsideDev/Runtime/AudioCapture.cs:1292 (AudioCapture.Usage) |
| `acap mark <ascii> \| acap stop \| acap result [json] \| acap suspend on\|off \| acap rtpctest [level=100] \| acap changes 1\|2` |  | src/InsideDev/Runtime/AudioCapture.cs:1292 (AudioCapture.Usage) |
| `acap output [audible\|muted]   (acap.output in editor.cfg, default audible; suspend, muteronly, hb, hbage, rtpctest and 'suspend on' are muted-only)` |  | src/InsideDev/Runtime/AudioCapture.cs:1292 (AudioCapture.Usage) |
| `acap guard [on\|off]   (off: acap's rule on the master volume is released and not forced again until the next preflight, start or guard on)` |  | src/InsideDev/Runtime/AudioCapture.cs:1292 (AudioCapture.Usage) |
| `acap start <name> [suspend=auto\|on\|off] [muteronly=on] [level=100] [hb=<heartbeat.json>] [hbage=2]` |  | src/InsideDev/Runtime/AudioCapture.cs:1298 (AudioCapture.StartUsage) |
| `acap output audible\|muted (now muted\|audible)` |  | src/InsideDev/Runtime/AudioCapture.cs:1378 |
| `acap preflight [suspend=auto\|on\|off] [muteronly=on] [hb=<heartbeat.json>] [hbage=2]` |  | src/InsideDev/Runtime/AudioCapture.cs:1405 |

<a id="ext-mcam"></a>

### Word: mcam

A forwarded row of section 1 (handled by the core).

| Usage | Text | Source |
|---|---|---|
| `mcam check <spec> [json] [replay\|v2] \| start <spec> [<out>] [overwrite=on] \| rehearse <spec> [<out>] [overwrite=on] \| preview <spec> [<out>] [t] [overwrite=on] \| record <spec> [<out>] [overwrite=on] \| replay <spec> [<out>] [cams=a,b] [range=t0:t1] [resume] [overwrite=on] \| take <spec> [<out>] [cams=a,b] [range=t0:t1] [overwrite=on] \| passes <spec> [<out>] [cams=a,b] [range=t0:t1] \| golden <spec> [<out>] [every] \| stop \| abort \| status [json] \| show <cam> [t] \| pose <cam> [t] \| views <spec> [shots\|t,t,...] [cams=a,b] [out=<json>] \| list \| probe <obj> [json] \| probe <x> <y> <z> <r> [json] \| readback test\|status\|stat\|free \| schema  (no <out>: <spec dir>/takes/<name>_<time>; a folder that holds a take needs overwrite=on)` |  | src/InsideDev/Runtime/MultiCam.cs:30 (MultiCam.Help) |
| `mcam record <spec.json> [<out dir>] [overwrite=on]` |  | src/InsideDev/Runtime/MultiCam.cs:1412 (MultiCamLint.Usage()) |
| `mcam golden <spec.json> [<out dir>] [every]` |  | src/InsideDev/Runtime/MultiCam.cs:1413 (MultiCamLint.Usage()) |
| `mcam replay <spec.json> [<out dir>] [cams=a,b] [range=t0:t1] [resume] [overwrite=on]` |  | src/InsideDev/Runtime/MultiCam.cs:1414 (MultiCamLint.Usage()) |
| `mcam take <spec.json> [<out dir>] [cams=a,b] [range=t0:t1] [overwrite=on]` |  | src/InsideDev/Runtime/MultiCam.cs:1415 (MultiCamLint.Usage()) |
| `mcam <verb> <spec.json> [<out dir>] [cams=a,b] [range=t0:t1]` |  | src/InsideDev/Runtime/MultiCam.cs:1416 (MultiCamLint.Usage()) |
| `mcam probe <obj> [json] \| mcam probe <x> <y> <z> <radius> [json]` |  | src/InsideDev/Runtime/MultiCam.cs:4922 (MultiCamProbe.Usage) |
| `mcam readback test \| status [json] \| stat [json] \| free [force]` |  | src/InsideDev/Runtime/MultiCamAsyncGrab.cs:354 (MultiCamReadbackTest.Usage) |
| `mcam check <spec.json> [json] [replay\|v2]` |  | src/InsideDev/Runtime/MultiCam.cs:131 |
| `mcam <verb> <spec.json> [<out dir>] [t] [overwrite=on]` |  | src/InsideDev/Runtime/MultiCam.cs:154 |
| `mcam show\|pose <camera> [t]` |  | src/InsideDev/Runtime/MultiCam.cs:306 |
| `mcam views <spec.json> [shots \| <t>[,<t>...]] [cams=a,b] [out=<file.json>]` |  | src/InsideDev/Runtime/MultiCam.cs:382 |

<a id="ext-srec"></a>

### Word: srec

A forwarded row of section 1 (handled by the core).

| Usage | Text | Source |
|---|---|---|
| `srec load <dir\|last> [dry] [replace] [play [speed] [loop\|once]] [t=<s>\|@event] [cam=game\|keep] [<play key>=value ...] [json with dry]  (a relative dir is a folder under <game>\_mod\srec)` |  | src/InsideDev/Runtime/SceneCommands.cs:35 (SceneCommands.LoadUsage) |
| `srec seek <seconds\|start\|end\|@event[+-s]\|rel <seconds>\|sample:<n>\|<percent>%>` |  | src/InsideDev/Runtime/SceneCommands.cs:36 (SceneCommands.SeekUsage) |
| `srec record start [<dir>] [roots=a,b] [keep=subscene,...] [spec=<file>] [clock=real\|fixed:<rate>] [audio=on] [overwrite=on] [<record key>=value ...] \| srec record stop [keep\|load] \| srec record status [json]  (no dir: a new take_<date_time> under <game>\_mod\srec; a relative dir is a folder there)` |  | src/InsideDev/Runtime/SceneCommands.cs:37 (SceneCommands.RecordUsage) |
| `srec event <name> [detail ...] (name: letters, digits, '_' and '.'; referenced as @name)` |  | src/InsideDev/Runtime/SceneCommands.cs:38 (SceneCommands.EventUsage) |
| `srec status [json] \| srec objects [filter] [max=n] \| srec list [max=n] [json] \| srec events [json]` |  | src/InsideDev/Runtime/SceneCommands.cs:40 (SceneCommands.Usage) |
| `srec load <dir\|last> [dry] [replace] [play [speed] [loop]] [t=<s>\|@event] [cam=game\|keep] [<play key>=value ...] \| srec reload \| srec end` |  | src/InsideDev/Runtime/SceneCommands.cs:40 (SceneCommands.Usage) |
| `srec play [speed] [loop] \| srec pause \| srec seek <s\|start\|end\|@event[+-s]\|rel <s>\|sample:<n>\|<p>%> \| srec step [dt\|next\|back]` |  | src/InsideDev/Runtime/SceneCommands.cs:40 (SceneCommands.Usage) |
| `srec probe [n] [eps=m] [rot=deg] [out=dir] \| srec probe camera [n] [dx=m] [yaw=deg] [eps=m] [rot=deg] [out=dir] [roots=a,b] \| srec probe stop \| srec probe result` |  | src/InsideDev/Runtime/SceneCommands.cs:40 (SceneCommands.Usage) |
| `srec record start [<dir>] [roots=a,b] [keep=subscene,...] [spec=<file>] [clock=real\|fixed:<rate>] [audio=on] [overwrite=on] [<record key>=value ...] \| srec record stop [keep\|load] \| srec record status [json]` |  | src/InsideDev/Runtime/SceneCommands.cs:40 (SceneCommands.Usage) |
| `srec event <name> [detail ...]` |  | src/InsideDev/Runtime/SceneCommands.cs:40 (SceneCommands.Usage) |
|  | a relative &lt;dir> is a folder under &lt;game>\\\_mod\\srec; record start without &lt;dir> makes take\_&lt;date\_time> there | src/InsideDev/Runtime/SceneCommands.cs:40 (SceneCommands.Usage) |
| `srec step [dt seconds \| next \| back]` |  | src/InsideDev/Runtime/SceneCommands.cs:616 |
| `srec probe [n] [eps=m] [rot=deg] [out=dir] \| srec probe camera [n] ...` |  | src/InsideDev/Runtime/SceneCommands.cs:1381 |

## 3. Config keys

Keys of _mod\\editor.cfg as described by EditorState.Describe(part, key, default, text) (cfg lists them). Calls inside foreach loops are expanded to one row per value. Total: 106 rows, 104 distinct keys (a key described by two parts is listed under both).

Parts: core (30), editor (38), cam (4), data (9), mcam (2), postfx (1), acap (1), mcp (9), guide (2), notes (1), play (7), film (2)

### Part: core

| Key | Default | Meaning | Source |
|---|---|---|---|
| `background` | `auto` | when the background scanners run: auto (only while the tool is in use), always (as before 0.11.0), off (only on request) | src/InsideDev/CoreBCommands.cs:19 |
| `ffmpeg.path` | `""` | full path of ffmpeg.exe; empty = \_mod\\ffmpeg\\ffmpeg.exe, the PATH, C:\\ffmpeg\\bin | src/InsideDev/CoreBCommands.cs:20 |
| `bridge.enabled` | `1` | file bridge \_mod\\cmd on (1) or off (0), read at start; `remote on\|off` switches it for the session | src/InsideDev/CoreBCommands.cs:21 |
| `bridge.state` | `auto` | state.txt snapshot: auto (only while command files arrive), always (every 5 s whether anybody reads it or not) | src/InsideDev/CoreBCommands.cs:22 |
| `audio.spy` | `1` | 0 leaves the wrapper around the game's sound engine out: no audio log, no audio rules, no cue injection (read at start) | src/InsideDev/CoreBCommands.cs:23 |
| `audio.log` | `auto` | audio log, callback log and caller stacks: auto (only while the tool is in use), always | src/InsideDev/CoreBCommands.cs:24 |
| `audio.stacks` | `important` | caller stacks of sound posts while the audio log is awake: off, important (first posts of each event), full; `wwise stacks` changes it for the session only | src/InsideDev/CoreBCommands.cs:25 |
| `audio.timed` | `auto` | scan and firing log of music-timed components: auto (only while the tool is in use), always | src/InsideDev/CoreBCommands.cs:26 |
| `audio.catalog` | `auto` | audio\_catalog.txt grows with the areas you play through: auto (only while the tool is in use), always, off | src/InsideDev/CoreBCommands.cs:27 |
| `ui.scale` | `1` | editor UI scale (0.5 to 2; Settings tab) | src/InsideDev/DevRoot.cs:130 |
| `hud` | `1` | HUD lines at the top of the picture (F5) | src/InsideDev/DevRoot.cs:131 |
| `hotkeys.game` | `1` | 1 = F7 god, F8 kill, \[ \] \\ time scale and middle-click teleport always work; 0 = only while the editor panel is open | src/InsideDev/DevRoot.cs:132 |
| `console.history` | `""` | the last 50 console commands, oldest first, separated by \| (a \| inside a command is written %7C, a % is written %25) | src/InsideDev/DevRoot.cs:133 |
| `hidden.atlas` | `0` | Hidden tab: list the switched-off objects of all levels from the atlas instead of the loaded scenes | src/InsideDev/DevRoot.cs:134 |
| `diag.board` | `0` | diagnostics board drawn over the picture (F11) | src/InsideDev/DevRoot.cs:135 |
| `render.renderer` | `gl` | editor renderer: gl (immediate) or mesh (batched) | src/InsideDev/DevRoot.cs:136 |
| `mode.author` | `0` | Mod Author mode of the History panel (also described by part editor) | src/InsideDev/DevRoot.cs:137 |
| `inspector.mode` | `0` | Inspector detail: 0 Normal, 1 Advanced, 2 Raw | src/InsideDev/DevRoot.cs:138 |
| `insp.fold.signals` | `1` | Inspector: section Signal connections open | src/InsideDev/DevRoot.cs:139 |
| `insp.fold.connectors` | `1` | Inspector: section SignalConnector wiring open | src/InsideDev/DevRoot.cs:140 |
| `overlay.triggers` | `1` | world overlay: trigger volumes drawn (F2 cycles the categories, then off) | src/InsideDev/Overlay.cs:94 |
| `overlay.filter` | `all` | world overlay: trigger category shown: all, trigger, save, camera, kill, audio, logic, water, fx or stream | src/InsideDev/Overlay.cs:95 |
| `overlay.solids` | `0` | world overlay: solid colliders drawn (F3) | src/InsideDev/Overlay.cs:96 |
| `overlay.labels` | `3` | world overlay: label level 0 off, 1 selected, 2 near the boy, 3 all (F4) | src/InsideDev/Overlay.cs:97 |
| `overlay.label.types` | `3` | world overlay: component names in a label, the rest as +N (0 only the count, -1 all); the selected object always shows all | src/InsideDev/Overlay.cs:98 |
| `overlay.scope` | `1` | world overlay: scope around the selection 0 selected, 1 one hop, 2 two hops, 3 near the boy, 4 all (Shift+F4) | src/InsideDev/Overlay.cs:99 |
| `overlay.savepoints` | `1` | world overlay: savepoint markers drawn (F6) | src/InsideDev/Overlay.cs:100 |
| `overlay.links` | `1` | world overlay: lines from the selection to its related objects | src/InsideDev/Overlay.cs:101 |
| `overlay.distance` | `60` | world overlay: draw distance from the camera in metres (5 to 300) | src/InsideDev/Overlay.cs:102 |
| `overlay.hidden` | `0` | world overlay: markers on switched-off objects (F10); keeps the object scan running | src/InsideDev/Overlay.cs:103 |

### Part: editor

| Key | Default | Meaning | Source |
|---|---|---|---|
| `layout.mode` | `dock` | editor shell: dock (three docks around the game view) or window (one floating window) | src/InsideDev/Editor/EditorState.cs:188 |
| `dock.left.w` | `0.3` | width of the left dock as a fraction of the screen, 0.12 to 0.6 | src/InsideDev/Editor/EditorState.cs:189 |
| `dock.right.w` | `0.3` | width of the right dock as a fraction of the screen, 0.12 to 0.6 | src/InsideDev/Editor/EditorState.cs:190 |
| `dock.bottom.h` | `0.3` | height of the bottom dock as a fraction of the screen, 0.1 to 0.7 | src/InsideDev/Editor/EditorState.cs:191 |
| `dock.left.panels` | `""` | panel ids shown in the left dock, comma separated (empty: the built-in set) | src/InsideDev/Editor/EditorState.cs:194 |
| `dock.bottom.panels` | `""` | panel ids shown in the bottom dock, comma separated (empty: the built-in set) | src/InsideDev/Editor/EditorState.cs:194 |
| `dock.right.panels` | `""` | panel ids shown in the right dock, comma separated (empty: the built-in set) | src/InsideDev/Editor/EditorState.cs:194 |
| `dock.left.active` | `0` | index of the tab shown in the left dock | src/InsideDev/Editor/EditorState.cs:195 |
| `dock.bottom.active` | `0` | index of the tab shown in the bottom dock | src/InsideDev/Editor/EditorState.cs:195 |
| `dock.right.active` | `0` | index of the tab shown in the right dock | src/InsideDev/Editor/EditorState.cs:195 |
| `dock.left.visible` | `1` | 1: the left dock is shown, 0: hidden | src/InsideDev/Editor/EditorState.cs:196 |
| `dock.bottom.visible` | `1` | 1: the bottom dock is shown, 0: hidden | src/InsideDev/Editor/EditorState.cs:196 |
| `dock.right.visible` | `1` | 1: the right dock is shown, 0: hidden | src/InsideDev/Editor/EditorState.cs:196 |
| `window.x` | `30` | single window mode: left edge in UI pixels | src/InsideDev/Editor/EditorState.cs:198 |
| `window.y` | `70` | single window mode: top edge in UI pixels | src/InsideDev/Editor/EditorState.cs:199 |
| `window.w` | `760` | single window mode: width in UI pixels | src/InsideDev/Editor/EditorState.cs:200 |
| `window.h` | `580` | single window mode: height in UI pixels | src/InsideDev/Editor/EditorState.cs:201 |
| `window.active` | `0` | single window mode: index of the tab shown | src/InsideDev/Editor/EditorState.cs:202 |
| `layouts` | `""` | names of the saved layouts, comma separated; each is stored under layout.&lt;name>.\* (ui layout save &lt;name>) | src/InsideDev/Editor/EditorState.cs:203 |
| `explorer.cat` | `0` | Explorer search category: 0 all, 1 animation, 2 sounds, 3 scripts, 4 triggers, 5 state machines, 6 characters | src/InsideDev/Editor/EditorState.cs:204 |
| `explorer.clutter` | `0` | 1: the Explorer search also lists static meshes, decals, bones and spawned FX clones | src/InsideDev/Editor/EditorState.cs:205 |
| `explorer.path` | `0` | 1: the Explorer search also lists objects whose parent or level name matches | src/InsideDev/Editor/EditorState.cs:206 |
| `graph.mode` | `0` | Graph panel: 0 GAME, 1 WWISE, 2 COMBINED, 3 LOGIC | src/InsideDev/Editor/EditorState.cs:207 |
| `graph.zoom` | `1` | Graph panel zoom, 0.3 to 2.5 (used when graph.userzoom is 1) | src/InsideDev/Editor/EditorState.cs:208 |
| `graph.userzoom` | `0` | 1: the Graph panel keeps the zoom the user chose instead of fitting each new graph | src/InsideDev/Editor/EditorState.cs:209 |
| `graph.live.s` | `1` | 1: signals light up the Graph panel and fill its event strip | src/InsideDev/Editor/EditorState.cs:210 |
| `graph.live.f` | `1` | 1: state machine changes light up the Graph panel and fill its event strip | src/InsideDev/Editor/EditorState.cs:211 |
| `graph.live.a` | `1` | 1: sounds light up the Graph panel and fill its event strip | src/InsideDev/Editor/EditorState.cs:212 |
| `graph.live.n` | `1` | 1: animation starts light up the Graph panel and fill its event strip | src/InsideDev/Editor/EditorState.cs:213 |
| `gizmo.global` | `0` | 1: the move gizmo is shown for every selected object | src/InsideDev/Editor/EditorState.cs:214 |
| `render.glyphcache` | `1` | 1: glyph positions of the editor font are kept between frames; 0: read from the font for every glyph of every frame (use it if editor text ever shows wrong letters) | src/InsideDev/Editor/EditorState.cs:215 |
| `mode.author` | `0` | 1: Mod Author mode (selectors, Record as mod, the mod manager in the History panel) (also described by part core) | src/InsideDev/Editor/EditorState.cs:216 |
| `insp.fold.activity` | `1` | 1: the Inspector section ACTIVITY is unfolded, 0: folded (a folded section is not recomputed) | src/InsideDev/Editor/EditorState.cs:218 |
| `insp.fold.materials` | `1` | 1: the Inspector section MATERIALS is unfolded, 0: folded (a folded section is not recomputed) | src/InsideDev/Editor/EditorState.cs:218 |
| `insp.fold.levelfile` | `1` | 1: the Inspector section LEVELFILE is unfolded, 0: folded (a folded section is not recomputed) | src/InsideDev/Editor/EditorState.cs:218 |
| `insp.fold.atlas` | `1` | 1: the Inspector section ATLAS is unfolded, 0: folded (a folded section is not recomputed) | src/InsideDev/Editor/EditorState.cs:218 |
| `insp.fold.memory` | `1` | 1: the Inspector section MEMORY is unfolded, 0: folded (a folded section is not recomputed) | src/InsideDev/Editor/EditorState.cs:218 |
| `insp.fold.recent` | `1` | 1: the Inspector section RECENT is unfolded, 0: folded (a folded section is not recomputed) | src/InsideDev/Editor/EditorState.cs:218 |

### Part: cam

| Key | Default | Meaning | Source |
|---|---|---|---|
| `cam.hotkeys` | `always` | when Insert (free camera) and + - 0 (zoom) work: always, panel (only while the editor panel is open), off | src/InsideDev/Runtime/CameraControl.cs:382 |
| `cam.speed` | `6` | fly speed of the free camera in m/s (the mouse wheel changes and stores it) | src/InsideDev/Runtime/CameraControl.cs:383 |
| `cam.lookSensitivity` | `0.25` | mouse look of the free camera in degrees per pixel | src/InsideDev/Runtime/CameraControl.cs:384 |
| `cam.invertY` | `0` | 1: mouse up looks down in the free camera | src/InsideDev/Runtime/CameraControl.cs:385 |

### Part: data

| Key | Default | Meaning | Source |
|---|---|---|---|
| `ext.autoDisable` | `0` | 1 = an extension module whose Tick throws 30 times in a row is switched off, one whose Start threw is not ticked (0 = as in 0.10.0: logged, kept running) | src/InsideDev/Runtime/Extensions.cs:64 |
| `hidden.passive` | `0` | 1 = the passive hidden-object scanner runs all the time (hidden on / off); 0 = only while the Hidden tab or hidden scan asks | src/InsideDev/Runtime/RtXData.cs:19 |
| `events.monitor` | `0` | 1 = the event monitor (signal taps, state machine polling) is always on (events on / off); 0 = only while something reads it | src/InsideDev/Runtime/RtXData.cs:20 |
| `db.rootRefresh` | `60` | seconds between full root searches of the object database while it is awake (at least 10); a streaming change always triggers one | src/InsideDev/Runtime/RtXData.cs:21 |
| `audio.trace` | `0` | 1 = the Wwise trace runs all the time; 0 = only while an audio tool is in use (the native tracer only counts otherwise) | src/InsideDev/Runtime/RtXData.cs:22 |
| `audio.scan` | `0` | 1 = the audio scanners (sound library, audio database, reference scan) run all the time and record every area you walk through; 0 = only while an audio tool is in use | src/InsideDev/Runtime/RtXData.cs:23 |
| `mods.settle` | `0.25` | seconds streaming must be quiet after a level loads before its mod operations are resolved (0..30) | src/InsideDev/Runtime/RtXData.cs:24 |
| `audio.native` | `0` | 1 = audio rules are also enforced inside version.dll (audio native on / off) | src/InsideDev/Runtime/RtXData.cs:25 |
| `mem.show` | `0` | 1 = the Inspector shows memory addresses for Cheat Engine (mem on / off) | src/InsideDev/Runtime/RtXData.cs:26 |

### Part: mcam

| Key | Default | Meaning | Source |
|---|---|---|---|
| `mcam.overwrite` | `off` | on: mcam record / take / replay may write into a folder that already holds a run without overwrite=on (as before 0.11.0) | src/InsideDev/Runtime/MultiCamReplay.cs:308 |
| `mcam.planMaxMB` | `512` | largest replay plan in MB of memory (steps x cameras) that mcam replay / take / passes builds; a longer timeline is refused | src/InsideDev/Runtime/MultiCamReplay.cs:309 |

### Part: postfx

| Key | Default | Meaning | Source |
|---|---|---|---|
| `postfx.<Type>.<setting>` | `""` | a changed setting of a post effect on the gameplay camera (postfx set, Settings > Rendering), re-applied to every new camera; postfx.&lt;Type>.(enabled) is 1 or 0 | src/InsideDev/Runtime/RenderScale.cs:152 |

### Part: acap

| Key | Default | Meaning | Source |
|---|---|---|---|
| `acap.output` | `audible` | sound output of an audio capture: audible (the game sound plays, nothing is forced outside a capture window) or muted (InsideMuter heartbeat, master volume forced 0 outside the window); set with 'acap output' | src/InsideDev/Runtime/RplXSetup.cs:22 |

### Part: mcp

| Key | Default | Meaning | Source |
|---|---|---|---|
| `mcp.enabled` | `1` | MCP server on at game start (1 / 0); the commands mcp on / mcp off set it | src/mcp/Extension.cs:160 |
| `mcp.port` | `47811` | TCP port of the MCP server on 127.0.0.1 and \[::1\] (1024-65535); mcp port &lt;n> sets it | src/mcp/Extension.cs:161 |
| `mcp.tools` | `""` | tool allowlist: names, &lt;profile>:&lt;tool>, &lt;profile>:\* or \*, separated by commas or spaces; empty = every tool | src/mcp/Extension.cs:162 |
| `mcp.token` | `""` | when set, every request needs the header Authorization: Bearer &lt;token>; mcp token new / clear in the console | src/mcp/Extension.cs:163 |
| `mcp.hosts` | `""` | more names accepted in the Host header next to 127.0.0.1, localhost and \[::1\] (a port forward under another name) | src/mcp/Extension.cs:164 |
| `mcp.idle_restore_s` | `600` | seconds without a tool call after which the game is put back to normal play (0 = never) | src/mcp/Extension.cs:165 |
| `mcp.restore_on_close_s` | `60` | seconds after the last MCP client went away before the game is put back to normal play (0 = never) | src/mcp/Extension.cs:166 |
| `mcp.text_copy` | `full` | full = every tool result also carries its JSON as text; short = a one-line summary there (for clients that read structuredContent) | src/mcp/Extension.cs:167 |
| `play.save_guard` | `0` | 1 = the command tool refuses game loads that skip the save backup (load, spawn, reload, respawn, cspawn go) (also described by part play) | src/mcp/Extension.cs:170 |

### Part: guide

| Key | Default | Meaning | Source |
|---|---|---|---|
| `play.guide_dir` | `""` | Folder of the guide playbooks (&lt;group>/&lt;topic>/SKILL.md); empty = the guide folder next to InsideDev.Play.dll. The environment variable INSIDEDEV\_GUIDE\_DIR comes first. | src/play/Guide.cs:40 |
| `play.guide_max` | `solution` | The most the guide tool may reveal: hint, steps or solution. | src/play/Guide.cs:41 |

### Part: notes

| Key | Default | Meaning | Source |
|---|---|---|---|
| `play.notes_key` | `Home` | Key that flags the current moment as a note (a UnityEngine.KeyCode name such as Home or F9); none or off = no hotkey. | src/play/Notes.cs:34 |

### Part: play

| Key | Default | Meaning | Source |
|---|---|---|---|
| `play.perception_scan` | `all` | observe: which objects the component pass keeps. all = switched-off objects too (objects that the game switches on later are seen at once, a pass is needed only after a load or a streamed-in subscene); active = active objects only, rescanned for every observation | src/play/Perception.cs:29 |
| `play.perception_bg_s` | `0` | observe: seconds between background passes of the component lists while an agent is active (0 = none; passes run when an observation needs them) | src/play/Perception.cs:30 |
| `play.vsync_off` | `1` | vSync: 1 = off while an agent's tool calls arrive (30 s after the last one) and while the game clock is held (first action or timed ctl command until restore), else the game's own (default); 0 = always the game's own; 2 = off from game start (as before 0.11.0). vsync on / off / auto overrides it until restore. | src/play/PlayExtension.cs:60 |
| `play.save_guard` | `0` | 1 = loads and edits that write INSIDE.sav back the save up first and need confirm; 0 = no backup, no confirm. (also described by part mcp) | src/play/PlayExtension.cs:61 |
| `play.hold_timeout_s` | `0` | Optional safety release (off by default): seconds without a play tool call after which input held by an agent (act hold, a grab grip) is released while the game runs in realtime; 0 = never (default). Input held with ctl by a person is not timed. | src/play/PlayExtension.cs:62 |
| `play.step_pacing` | `auto` | Step-mode runs: auto = as fast as the game renders, paced to real time only in music-timed places; always = every run paced to 60 steps per second (watchable, sound keeps up); never = no pacing. | src/play/PlayExtension.cs:63 |
| `play.cheats` | `0` | checkpoint next / prev / goto (jumps to another checkpoint; they write the new spawn into INSIDE.sav) are refused unless this is 1 | src/play/PlayLevels.cs:58 |

### Part: film

| Key | Default | Meaning | Source |
|---|---|---|---|
| `film.encoder` | `auto` | Video encoder of grec and rec: auto tries hevc\_nvenc, hevc\_amf, hevc\_qsv, libx265, libx264 with a short test encode and takes the first that works; or one of auto, hevc\_nvenc, h264\_nvenc, hevc\_amf, h264\_amf, hevc\_qsv, h264\_qsv, libx265, libx264. | src/play/PfmXFilm.cs:62 |
| `film.rec_max_s` | `14400` | Longest desktop recording (rec start ... desktop, time record source desktop) in seconds; ffmpeg ends by itself after that. 0 = no cap. | src/play/PfmXFilm.cs:63 |

## 4. MCP tools

Tools of the in-game MCP server. Profiles of the server: `play`, `dev`, `all` (endpoints /mcp or /mcp/dev, /mcp/play, /mcp/all; all serves every tool). Arguments come from the input schema builders (src/mcp/ServerSchema.cs); nested objects and array items are flattened with dotted names.

| Tool | Profiles | Title | Source |
|---|---|---|---|
| [`act`](#tool-act) | play | Act (input steps) | src/play/Act.cs:47 |
| [`audio`](#tool-audio) | dev | Audio | src/play/AudioRender.cs:39 |
| [`body`](#tool-body) | dev | Bodies | src/play/WorldLogic.cs:57 |
| [`camera`](#tool-camera) | dev | Camera | src/play/WorldCamera.cs:48 |
| [`checkpoint`](#tool-checkpoint) | play | Checkpoints | src/play/PlayLevels.cs:59 |
| [`command`](#tool-command) | dev | Run InsideDev command | src/mcp/DevTools.cs:217 |
| [`film`](#tool-film) | play, dev | Filming | src/play/PfmXFilm.cs:24 |
| [`find_objects`](#tool-find_objects) | dev | Find objects | src/mcp/DevTools.cs:163 |
| [`fsm`](#tool-fsm) | dev | Drive an FSM | src/mcp/DevTools.cs:189 |
| [`fsm_find`](#tool-fsm_find) | dev | Find in FSMs | src/mcp/DevTools.cs:186 |
| [`fsm_graph`](#tool-fsm_graph) | dev | PlayMaker FSM | src/mcp/DevTools.cs:182 |
| [`game_state`](#tool-game_state) | dev | Game state | src/mcp/DevTools.cs:152 |
| [`geometry`](#tool-geometry) | play, dev | Level geometry | src/play/Geometry.cs:92 |
| [`guide`](#tool-guide) | play | Game guide | src/play/Guide.cs:42 |
| [`hazard`](#tool-hazard) | dev | Hazards | src/play/Hazards.cs:54 |
| [`input_track`](#tool-input_track) | play, dev | Input track (rehearse, then take) | src/play/ActTrack.cs:76 |
| [`inside_status`](#tool-inside_status) | launcher | INSIDE status | src/mcp/McpStdio.cs:179 |
| [`inspect`](#tool-inspect) | dev | Inspect object | src/mcp/DevTools.cs:168 |
| [`interact`](#tool-interact) | play | Interact | src/play/Interact.cs:34 |
| [`jobs`](#tool-jobs) | dev | Jobs | src/mcp/DevTools.cs:222 |
| [`level`](#tool-level) | dev | Levels and loads | src/play/PlayLevels.cs:73 |
| [`log`](#tool-log) | dev | InsideDev log | src/mcp/DevTools.cs:204 |
| [`logic`](#tool-logic) | dev | World logic | src/play/WorldLogic.cs:35 |
| [`menu`](#tool-menu) | play | Menus | src/play/Menus.cs:47 |
| [`move_to`](#tool-move_to) | play | Move to | src/play/Navigator.cs:49 |
| [`notes`](#tool-notes) | play | Flagged moments | src/play/Notes.cs:35 |
| [`observe`](#tool-observe) | play, dev | Observe | src/play/Observer.cs:79 |
| [`reflect`](#tool-reflect) | dev | Reflection | src/play/Reflect.cs:35 |
| [`render`](#tool-render) | dev | Render | src/play/AudioRender.cs:49 |
| [`restore`](#tool-restore) | play, dev | Restore normal play | src/play/Restore.cs:41 |
| [`run_scenario`](#tool-run_scenario) | dev | Run a scenario | src/play/GuideScenario.cs:24 |
| [`save`](#tool-save) | dev | Save, orbs, endings | src/play/Saves.cs:53 |
| [`screenshot`](#tool-screenshot) | play, dev | Screenshot | src/mcp/DevTools.cs:155 |
| [`select`](#tool-select) | dev | Select in editor | src/mcp/DevTools.cs:174 |
| [`set_active`](#tool-set_active) | dev | Switch object on/off | src/mcp/DevTools.cs:177 |
| [`show_panel`](#tool-show_panel) | dev | Show an editor panel | src/mcp/DevTools.cs:200 |
| [`signal`](#tool-signal) | dev | Fire a signal | src/mcp/DevTools.cs:195 |
| [`state_diff`](#tool-state_diff) | dev | State diff | src/play/StateDiff.cs:79 |
| [`stream`](#tool-stream) | dev | Streaming | src/play/PlayLevels.cs:95 |
| [`time`](#tool-time) | play, dev | Game clock | src/play/Stepper.cs:60 |
| [`ui`](#tool-ui) | dev | UI switches | src/play/Menus.cs:65 |
| [`wait_until`](#tool-wait_until) | play, dev | Wait until | src/play/Waits.cs:69 |

<a id="tool-act"></a>

### Tool: act

- Profiles: play
- Title: Act (input steps)
- Owner: INPUT
- Annotations: always loaded
- Source: src/play/Act.cs:47

Press the game's controls for exact logic steps (60 per second): a list of {stick, jump, action, steps}, or seq (a short script), at most 600 steps per call. The game runs only for those steps (step mode) and stops early on stop\_on. hold keeps the last input pressed after the call; a grab from interact grab stays held (release:true lets go). when waits for a condition first, then acts in the same call. Returns outcome, steps run, changes and a fresh observation.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `inputs` | array of object, up to 64 items |  |  | Input segments in order. |
| `inputs[].stick` | array of number -1..1, 2 items | default `[0, 0]` |  | Left stick x (right +), y (up +). 0 or magnitude 0.3..1; \|x\| > 0.8 runs. |
| `inputs[].jump` | boolean | default `false` |  | Jump button held (a press needs a release before the next one). |
| `inputs[].action` | boolean | default `false` |  | Action / grab button held (held = grab, short tap under 0.3 s = action). |
| `inputs[].steps` | integer 1..600 | default `1` |  | Logic steps to hold this input (1/60 s each). |
| `seq` | string (max 2000 chars) |  |  | Instead of inputs: "R1.2 J R0.4+J G1 W0.5 L0.3" = R L U D &lt;t>\[@0.3..1\] stick, J / A \[&lt;t>\] jump / action (no &lt;t>: a tap), G &lt;t> grab, W &lt;t> wait, + together; &lt;t> seconds, or steps as 72s. |
| `hold` | boolean | default `false` |  | Keep the last segment pressed after the call (until release or the next act). |
| `release` | boolean | default `false` |  | Release held input (also a grab) before the inputs run. |
| `input_mode` | string |  | `agent`, `mix`, `human` | agent (default on first use), mix (agent + physical pad) or human (give control back). |
| `stop_on` | array of string, up to 8 items | default `["death", "load"]` | `death`, `load`, `state_change`, `grounded`, `airborne`, `checkpoint`, `hazard`, `event` | Stop early when one of these happens. |
| `watch` | array of string, up to 8 items |  |  | Object ids: stop when the state of one changes. |
| `when` | object |  |  | Wait first, then act in the same call: one wait\_until condition, e.g. {hazard\_eta:{target:"h3", min\_s:4}} (4+ s to the next shockwave blow) or {music\_time:{from:0.2, to:1.5}}; not met in when\_timeout\_s = outcome timeout, no input. |
| `when.seconds` | number 0.05..600 |  |  | Game s. |
| `when.grounded` | boolean |  |  | On ground. |
| `when.airborne` | boolean |  |  | In air. |
| `when.state` | string |  |  | Body state. |
| `when.near` | object |  |  | Within m. |
| `when.near.target` | string | required |  | Id. |
| `when.near.within` | number 0.1..100 | default `1` |  | m. |
| `when.far_from` | object |  |  | Beyond m. |
| `when.far_from.target` | string | required |  | Id. |
| `when.far_from.beyond` | number 0.1..200 | default `5` |  | m. |
| `when.hazard_safe` | string |  |  | Hazard id. |
| `when.hazard_eta` | object |  |  | eta\_s >= min\_s. |
| `when.hazard_eta.target` | string | required |  | Hazard id. |
| `when.hazard_eta.min_s` | number 0..600 | default `4` |  | s. |
| `when.music_time` | object |  |  | Music s in \[from, to). |
| `when.music_time.from` | number 0..100000 | required |  | s. |
| `when.music_time.to` | number 0..100000 | required |  | s. |
| `when.music_time.cycle` | number 0..10000 | default `0` |  | s, 0 auto. |
| `when.fsm_state` | object |  |  | FSM state. |
| `when.fsm_state.object` | string | required |  | Id. |
| `when.fsm_state.fsm` | string |  |  | FSM. |
| `when.fsm_state.state` | string | required |  | State. |
| `when.signal` | object |  |  | Signal fired. |
| `when.signal.object` | string | required |  | Id. |
| `when.signal.output` | string | required |  | Output. |
| `when.object_active` | object |  |  | Object active. |
| `when.object_active.target` | string | required |  | Id. |
| `when.object_active.active` | boolean | default `true` |  | Wanted. |
| `when_timeout_s` | number 0.1..45 | default `15` |  | Longest wait for when (wall clock s). |
| `wait_ready_s` | number 0..45 | default `0` |  | Game not taking input yet (load, fade, streaming, preawake, input disabled, respawn): wait up to this many s, then go on in the same call. 0 = answer at once. |
| `then` | string | default `"freeze"` | `freeze`, `run` | step mode: freeze after the call (default) or keep running. |
| `changes` | string | default `"compact"` | `compact`, `full` | compact (near the body, plus deaths, checkpoints, loads, errors) or full (every signal and fsm change). |
| `observe` | string | default `"concise"` | `concise`, `none` | Observation in the result. |

<a id="tool-audio"></a>

### Tool: audio

- Profiles: dev
- Title: Audio
- Owner: WORLD\_B
- Source: src/play/AudioRender.cs:39

Wwise audio: music (read-only: position, loop, beat, bar), post an event (globally or on an object), stop a playing id, set an RTPC, state or switch. Master volume RTPCs accept only 0 (mute). restore stops posted events and resets changed RTPCs (never the master volume).

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"music"` | `music`, `post`, `stop`, `rtpc`, `state`, `switch` | music, post, stop, rtpc, state or switch. |
| `event` | string |  |  | op post: event name. |
| `object` | string |  |  | op post / rtpc / switch: object id or path (default global). |
| `name` | string |  |  | op rtpc: RTPC name. |
| `value` | number -1000000..1000000 |  |  | op rtpc. |
| `interp_s` | number 0..10 | default `0` |  | op rtpc: interpolation. |
| `group` | string |  |  | op state / switch: group name. |
| `state` | string |  |  | op state / switch: value name. |
| `playing_id` | integer 0..4294967295 |  |  | op stop. |

<a id="tool-body"></a>

### Tool: body

- Profiles: dev
- Title: Bodies
- Owner: WORLD\_B
- Annotations: destructive
- Source: src/play/WorldLogic.cs:57

Controllable bodies: list (boy, huddle, hat dudes, submarine), teleport (safe: refuses while dead, in a hat, in the sub or scripted; keeps the depth plane; snaps the camera; waits for streaming), hat (attach / detach / status of mind-control hats, MindHat.ForceAttach / ForceDetach), swap (boy / huddle, MainCharacterSwap).

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"list"` | `list`, `teleport`, `hat`, `swap` | list, teleport, hat or swap. |
| `body` | string |  |  | Body id (b1); default the controlled one. |
| `x` | number -100000..100000 |  |  | teleport: world x. |
| `y` | number -100000..100000 |  |  | teleport: world y. |
| `z` | number -100000..100000 |  |  | teleport: world z (default keep the depth plane). |
| `dx` | number -1000..1000 |  |  | teleport: relative x. |
| `dy` | number -1000..1000 |  |  | teleport: relative y. |
| `to_object` | string |  |  | teleport: to an object id or path. |
| `snap_camera` | boolean | default `true` |  | teleport. |
| `wait_stream` | boolean | default `true` |  | teleport: wait until streaming settles. |
| `hat_op` | string | default `"status"` | `attach`, `detach`, `status` | op hat. |
| `hat` | string |  |  | op hat: hat id or path. |
| `to` | string |  | `boy`, `huddle` | op swap. |
| `force` | boolean | default `false` |  | Skip the safety refusals (may break scripted scenes). |

<a id="tool-camera"></a>

### Tool: camera

- Profiles: dev
- Title: Camera
- Owner: WORLD\_B
- Source: src/play/WorldCamera.cs:48

The gameplay camera. state and boxes are read-only. box\_set switches a CameraBlendBox (framing zone) on / off or moves its points; follow targets another object; poi adds or clears a point of interest; fx turns shake / handshake off or on; pose flies the free camera to a pose; release gives the camera back. While overridden the body's area is pinned so it does not unload. Writes are re-read after 2 frames (verdict) and undone by restore.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"state"` | `state`, `boxes`, `box_set`, `follow`, `poi`, `fx`, `pose`, `release` | state, boxes, box\_set, follow, poi, fx, pose or release. |
| `box` | string |  |  | op box\_set: box id or path. |
| `active` | boolean |  |  | op box\_set. |
| `position_camera` | array of number -100000..100000, 3 items |  |  | op box\_set: camera point. |
| `position_aim` | array of number -100000..100000, 3 items |  |  | op box\_set: aim point. |
| `object` | string |  |  | op follow / poi: object id or path; empty poi clears. |
| `blend_s` | number 0..10 | default `1` |  | op follow: blend seconds. |
| `radius` | number 1..500 | default `30` |  | op boxes (m). |
| `shake` | boolean |  |  | op fx. |
| `handshake` | boolean |  |  | op fx. |
| `pose` | object |  |  | op pose. |
| `pose.x` | number -100000..100000 | required |  | World x. |
| `pose.y` | number -100000..100000 | required |  | World y. |
| `pose.z` | number -100000..100000 | required |  | World z. |
| `pose.yaw` | number -720..720 |  |  | Degrees. |
| `pose.pitch` | number -90..90 |  |  | Degrees. |
| `pose.fov` | number 5..140 |  |  | Degrees. |
| `pose.roll` | number -180..180 |  |  | Degrees. |
| `pose.seconds` | number 0..30 | default `0` |  | Ease time. |
| `pin` | boolean | default `true` |  | Pin the body's area while overridden. |
| `limit` | integer 1..100 | default `30` |  | op boxes: rows. |

<a id="tool-checkpoint"></a>

### Tool: checkpoint

- Profiles: play
- Title: Checkpoints
- Owner: WORLD\_A
- Annotations: destructive
- Source: src/play/PlayLevels.cs:59

Checkpoints (savepoints). current / list are read-only. reload = respawn at the last checkpoint (like dying, no save write; also skips the death wait). next / prev / goto jump to another checkpoint: a cheat, only when the user enabled play.cheats (EditorState play.cheats = 1); it writes the new spawn into INSIDE.sav. Waits until playable unless wait is false; a load still running after timeout\_s comes back as job.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"current"` | `current`, `list`, `reload`, `next`, `prev`, `goto` | current, list, reload, next, prev or goto. |
| `subscene` | integer 0..999 |  |  | op goto: savepoint subscene index (0..82, see list). |
| `savepoint` | integer 0..999 |  |  | op goto: savepoint index. |
| `name` | string |  |  | op goto: subscene by name instead of subscene (scene or puzzle name, or a part of it that matches one subscene; savepoint defaults to 0). |
| `filter` | string |  |  | op list: name filter. |
| `limit` | integer 1..200 | default `40` |  | op list: rows. |
| `offset` | integer 0..1000 | default `0` |  | op list: first row. |
| `wait` | boolean | default `true` |  | Wait until playable after a load. |
| `timeout_s` | number 1..50 | default `45` |  | Wait limit; when it runs out the result has job. |

<a id="tool-command"></a>

### Tool: command

- Profiles: dev
- Title: Run InsideDev command
- Owner: HOST
- Annotations: destructive
- Source: src/mcp/DevTools.cs:217

Run any InsideDev bridge/console command and return its output plus the log lines it wrote; 'help' lists them. OS-input commands (keyhold, focustest), commands that change this server (mcp off / port / token / tools / hosts, remote off), lines that name the server's settings (mcp.token, mcp.tools, mcp.hosts, mcp.enabled, mcp.port) and lines that drive secret / achievement objects are refused; the same list applies to POST /cmd and /cmds, run\_scenario steps, det cues and mcam spec lines (quit is allowed here and through POST /cmd only). This server trusts programs and pages of this machine (a page served from http://localhost on any port may call it); set mcp.token in the in-game console to require a token. isError is set from the command's own failure text. commands:\[...\] runs several lines in order in one call (results has one entry per line). wait:'&lt;status command>' keeps the call open after the command until the status command's reply contains one of the until texts, e.g. command 'pano shot.png', wait 'pano status'.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `command` | string |  |  | The command line, e.g. 'near 10' or 'get "Housing (4)" Transform localPosition'. |
| `max_chars` | integer 1000..200000 | default `20000` |  | Cap on returned text. |
| `commands` | array of string, 1..200 items |  |  | Several command lines instead of command; they run in order. |
| `stop_on_error` | boolean | default `false` |  | commands: stop at the first line that fails (the rest get code skipped). |
| `dry_run` | boolean | default `false` |  | Do not run anything: only report which lines would be refused. |
| `wait` | string |  |  | A status command to ask after command ran, until its reply contains one of the until texts. |
| `until` | string | default `"done\|failed\|idle\|not running\|stopped\|finished\|closed\|error"` |  | Texts that end the wait, separated by \| (case does not matter). |
| `wait_timeout_s` | number 0..50 | default `45` |  | wait: give up after this many seconds (wait\_done is then false). |

<a id="tool-film"></a>

### Tool: film

- Profiles: play, dev
- Title: Filming
- Owner: TIME
- Source: src/play/PfmXFilm.cs:24

Pictures and video of the game. pano: one 360 degree equirectangular PNG at the boy's head or at eye (the game is held for the six faces). grec\_start / grec\_stop: video of the game's own frames (windows over the game never get in) at a constant frame rate: one frame per every-th rendered frame while the game runs at a capture rate or held by the stepper, frames placed by game time in real-time play (video time = game time). campath\_load / campath\_arm / campath\_start / campath\_stop / mount: play a replay plan's camera or ride a transform on the live game camera (dev profile). status: all of them plus the real-time recorder of time {op:"record"}. Files without a folder go to &lt;game>/\_mod/recordings; every result names the full path. pano and grec\_stop wait for the finished file unless wait is false.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"status"` | `status`, `pano`, `grec_start`, `grec_stop`, `campath_load`, `campath_arm`, `campath_start`, `campath_stop`, `mount` | What to do. |
| `file` | string |  |  | op pano / grec\_start: file name (in \_mod/recordings) or a full path; default &lt;kind>\_&lt;date>\_&lt;time>. |
| `wait` | boolean | default `true` |  | op pano / grec\_stop: answer when the file is complete (within the call's time). |
| `size` | integer 256..16384 | default `4096` |  | op pano: width in pixels (height is half). |
| `eye` | array of number -100000..100000, 3 items |  |  | op pano: world point of the eye (default: the boy's head). |
| `vignette` | boolean | default `true` |  | op pano: keep the game's vignette. |
| `lens` | boolean | default `false` |  | op pano: keep barrel distortion and chromatic aberration. |
| `near` | number 0.001..10 |  |  | op pano / mount: near clip plane in metres. |
| `warm` | integer 0..30 | default `1` |  | op pano: frames to settle per face. |
| `samples` | integer 1..16 | default `1` |  | op pano: accumulated frames per face. |
| `delay` | integer 0..120 | default `2` |  | op pano: frames to wait before the capture. |
| `hold` | boolean | default `true` |  | op pano: hold the game for the six faces. |
| `overscan` | number 0..45 | default `0` |  | op pano: render each face this many degrees wider and use the centre (keeps screen-edge effects off the seams). |
| `compress` | boolean | default `false` |  | op pano: deflate the PNG in the game (slow). |
| `faces_dir` | string |  |  | op pano: also write the six faces into this folder (dev profile). |
| `every` | integer 1..600 | default `2` |  | op grec\_start: record every n-th rendered frame. |
| `cq` | integer 0..51 | default `16` |  | op grec\_start: constant quality, lower is better and larger. |
| `enc` | string |  |  | op grec\_start: video encoder (auto, hevc\_nvenc, hevc\_amf, hevc\_qsv, libx265, libx264 ...); default editor.cfg film.encoder. |
| `fps` | number 1..1000 |  |  | op grec\_start: frame rate of the file (default: the capture rate / every, the stepper's 60 / every, or in real-time play a nominal 60 / every with frames placed by game time). op campath\_load: output rate of the plan (default: from the plan). |
| `times` | boolean | default `true` |  | op grec\_start: also write &lt;file>.times.csv (per video frame: shown frame, game time, real time, repeated, video time, slot game time). |
| `dir` | string |  |  | op campath\_load: folder with poses.csv and meta.csv (mcam passes). |
| `camera` | string | default `"director"` |  | op campath\_load: camera name in the plan. |
| `offset_s` | number -1000000..1000000 | default `0` |  | op campath\_load: shift of the plan against the game clock. |
| `realtime` | boolean | default `false` |  | op campath\_load: pose by game time, no pauses or slow motion. |
| `object` | string |  |  | op mount: transform path the camera rides, or off. |
| `local` | array of number -100..100, 3 items |  |  | op mount: offset in the transform's space (m). |
| `rot` | array of number -360..360, 3 items |  |  | op mount: rotation offset (degrees). |
| `fov` | number 1..179 |  |  | op mount: field of view (degrees). |
| `smooth` | number 0..1 |  |  | op mount: share of the last pose kept per 1/60 s. |
| `into` | number 0..89 |  |  | op mount: turn toward +z by up to this many degrees. |
| `level` | number 0..1 |  |  | op mount: share of the roll taken out. |
| `hide` | boolean |  |  | op mount: collapse the transform while mounted. |
| `tau` | array of number 0..60, 2..4 items |  |  | op mount: time constants h, v \[, yaw, pitch\] in seconds. |
| `pitch` | number 1..89 |  |  | op mount: pitch limit (degrees). |
| `free` | array of number 0..89, 2 items |  |  | op mount: free eye pitch up, down (degrees). |

<a id="tool-find_objects"></a>

### Tool: find_objects

- Profiles: dev
- Title: Find objects
- Owner: HOST
- Annotations: read-only, idempotent
- Source: src/mcp/DevTools.cs:163

Smart search over loaded objects by name, ranked by match and what the object does. Returns up to limit rows.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `query` | string | required |  | Words to match against object names. |
| `category` | string |  | `animation`, `sounds`, `scripts`, `triggers`, `fsm`, `characters` | Optional filter. |
| `limit` | integer 1..100 | default `25` |  | Rows. |

<a id="tool-fsm"></a>

### Tool: fsm

- Profiles: dev
- Title: Drive an FSM
- Owner: HOST
- Annotations: destructive
- Source: src/mcp/DevTools.cs:189

Send an event to a PlayMaker FSM, force it into a state, or read/set one of its variables. event and state cannot be undone.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `object` | string | required |  | Object that has the FSM. |
| `action` | string | required | `event`, `state`, `var` | event, state or var. |
| `name` | string | required |  | Event, state or variable name. |
| `value` | string |  |  | New variable value (action var; omit to read). |
| `fsm` | string |  |  | FSM name when the object has several. |
| `strict` | boolean | default `false` |  | Refuse (error ambiguous) when a plain object name fits more than one object. |

<a id="tool-fsm_find"></a>

### Tool: fsm_find

- Profiles: dev
- Title: Find in FSMs
- Owner: HOST
- Annotations: read-only, idempotent
- Source: src/mcp/DevTools.cs:186

Search every loaded PlayMaker FSM (including switched-off ones) for events, signal inputs, states, action settings and signal connections.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `text` | string | required |  | Text to search for. |
| `limit` | integer 1..200 | default `30` |  | Hits. |

<a id="tool-fsm_graph"></a>

### Tool: fsm_graph

- Profiles: dev
- Title: PlayMaker FSM
- Owner: HOST
- Annotations: read-only, idempotent
- Source: src/mcp/DevTools.cs:182

Dump a PlayMaker state machine: states, exits with events, current state. open\_panel also shows it in the FSM Graph panel.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `object` | string | required |  | Object that has the FSM. |
| `fsm` | string |  |  | FSM name when the object has several. |
| `open_panel` | boolean | default `false` |  | Also select it and open the FSM Graph panel. |

<a id="tool-game_state"></a>

### Tool: game_state

- Profiles: dev
- Title: Game state
- Owner: HOST
- Annotations: read-only, idempotent
- Source: src/mcp/DevTools.cs:152

Text snapshot of the running game: InsideDev version, frame, boy position, savepoint, music position, active scenes, nearby triggers. For structured state use observe.

No arguments.

<a id="tool-geometry"></a>

### Tool: geometry

- Profiles: play, dev
- Title: Level geometry
- Owner: NAV
- Annotations: read-only, idempotent
- Source: src/play/Geometry.cs:92

Occupancy slice of the level around the body (or a point) at its depth plane: text rows with # solid, . free, ~ water, H ladder, \| rope, B body, ^ hazard, ? not loaded; plus ledges. Look ahead before move\_to (pits, walls, ledges up), plan routes, debug blocked moves.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"slice"` | `slice`, `ledges`, `probe` | slice, ledges or probe (one column under / over a point). |
| `center` | string |  |  | Object id or path (default the body). |
| `x` | number -100000..100000 |  |  | World x instead of center. |
| `y` | number -100000..100000 |  |  | World y. |
| `width` | number 2..64 | default `16` |  | Slice width (m). |
| `height` | number 2..32 | default `8` |  | Slice height (m). |
| `cell` | number 0.1..1 | default `0.25` |  | Cell size (m). |
| `format` | string | default `"rows"` | `rows`, `runs` | op slice: rows = one character per cell; runs = each row run-length coded, count then character, runs separated by a space ("12# 30. 4~"). |
| `crop` | boolean | default `false` |  | op slice: drop the border rows and columns that hold only . or ? (origin, cols and rows describe what is left). |

<a id="tool-guide"></a>

### Tool: guide

- Profiles: play
- Title: Game guide
- Owner: GUIDE
- Annotations: read-only, idempotent
- Source: src/play/Guide.cs:42

Playbooks for INSIDE: without topic the index (chapters, mechanics, puzzles, known stuck spots); with topic one playbook at a spoiler level: hint (default), steps or solution. observe.checkpoint.guide names the topic for the current place; section picks one heading (a place such as riverWithDog, or Savepoints).

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `topic` | string |  |  | Topic id from the index or observe.checkpoint.guide (e.g. forest/overview). Empty = index. |
| `level` | string | default `"hint"` | `hint`, `steps`, `solution` | How much to reveal. |
| `section` | string |  |  | Only this section heading of the topic. |

<a id="tool-hazard"></a>

### Tool: hazard

- Profiles: dev
- Title: Hazards
- Owner: WORLD\_B
- Source: src/play/Hazards.cs:54

Dangers near the body. near is read-only (kind, state, eta, whether it can be switched). set turns one hazard (target) or all near ones off / on (dogs, guards, sentries, spotlights, mermaid, kill zones, shockwave, kill FSMs). air sets infinite air / refills / changes the dive limit. god toggles invincibility (death animations can still play). Restored by restore. Each switch reports the call used and a verdict.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"near"` | `near`, `set`, `air`, `god` | near, set, air or god. |
| `target` | string |  |  | op set: hazard id or path. |
| `all_near` | boolean | default `false` |  | op set: every hazard within radius. |
| `radius` | number 1..200 | default `20` |  | op near / set (m). |
| `on` | boolean |  |  | op set: hazard active; op god: invincible. |
| `infinite` | boolean |  |  | op air. |
| `refill_s` | number 0..60 |  |  | op air: add seconds of air now. |
| `max_dive_s` | number 1..120 |  |  | op air: dive limit of the current water. |

<a id="tool-input_track"></a>

### Tool: input_track

- Profiles: play, dev
- Title: Input track (rehearse, then take)
- Owner: INPUT
- Source: src/play/ActTrack.cs:76

Record and replay input. start records every logic step of input that reaches the game (with body position and velocity) while you act / move\_to in step mode; stop, info, trim {from, to}, save {name} (\_mod/input\_tracks), load, list. play feeds the track back one frame per logic step in realtime at the rehearsal's 60 Hz lockstep (input only; drift measured; correct:true opts in to nudges), as a job: wait\_until {any:\[{job:"j3"}\]}; cancel with op stop or restore. The result's end says how a playback ended (finished, cancelled, restore, level\_load, died, stalled, desynced, error); status keeps it as last\_play. A manual srec recording may run during play (it owns frame pacing).

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"status"` | `status`, `start`, `stop`, `save`, `load`, `list`, `info`, `trim`, `play` | status, start, stop, save, load, list, info, trim or play. |
| `name` | string (max 64 chars) |  |  | Track name (letters, digits, \_ - .); info and play default to the track in memory. |
| `from` | integer 0..1000000 | default `0` |  | trim: first step kept. |
| `to` | integer -1..1000000 | default `-1` |  | trim: last step kept, -1 = end. |
| `overwrite` | boolean | default `false` |  | save: replace a track of that name. |
| `speed` | number 0.05..4 | default `1` |  | play: realtime speed (1 for a take). |
| `lockstep` | boolean | default `true` |  | play: hold the rehearsal's 60 Hz lockstep (default). false = free realtime: the game keeps its own 60 Hz logic steps at any frame rate, so game time follows the wall clock (sound takes). |
| `correct` | boolean | default `false` |  | play: nudge the body toward the recorded path (off: input only, for final takes; drift is measured either way). |
| `gain` | number 0..1 | default `0.05` |  | play: share of the drift removed per step. |
| `max_step` | number 0..0.5 | default `0.02` |  | play: largest correction per step (m). |
| `stop_drift` | number 0..100 | default `0` |  | play: stop beyond this drift (m), 0 = never. |
| `max_start` | number 0..1000 | default `1.5` |  | play: refuse when the body starts farther from frame 0 (m), 0 = no check. |
| `then` | string | default `"run"` | `run`, `freeze` | play: afterwards keep running or freeze. |
| `wait` | boolean | default `true` |  | play: wait up to timeout\_s for the end. |
| `timeout_s` | number 0..50 | default `45` |  | play / stop: wait limit. |
| `observe` | string | default `"concise"` | `concise`, `none` | play: observation in the result. |

<a id="tool-inside_status"></a>

### Tool: inside_status

- Profiles: every profile (answered by the stdio launcher, src/mcp/McpStdio.cs)
- Title: INSIDE status
- Owner: SERVER
- Annotations: read-only, idempotent
- Source: src/mcp/McpStdio.cs:179

Whether INSIDE with InsideDev is running and reachable, the game's instructions once it is up (read them if the session started before the game) and its health: main thread alive, frame, running job, tool counts, whether InsideDev.Play is loaded. Answered by the launcher, so it works while the game is down or busy; while the game is down it is the only tool.

No arguments.

<a id="tool-inspect"></a>

### Tool: inspect

- Profiles: dev
- Title: Inspect object
- Owner: HOST
- Annotations: read-only, idempotent
- Source: src/mcp/DevTools.cs:168

Describe an object: path, active state, components and their fields, references, FSM dump. Output is capped at max\_chars; page with offset.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `object` | string | required |  | Object id from observe (o12), hierarchy path (A/B/C), #instanceID, or a name: the exact name first, else text inside the name; of several matches the one nearest to the boy is used. |
| `deep` | boolean | default `false` |  | Expand nested serialized data. |
| `max_chars` | integer 1000..200000 | default `20000` |  | Cap on returned text. |
| `offset` | integer 0..100000000 | default `0` |  | Start of the page (characters). |

<a id="tool-interact"></a>

### Tool: interact

- Profiles: play
- Title: Interact
- Owner: INPUT
- Annotations: always loaded
- Source: src/play/Interact.cs:34

Do one thing with an object or the current body state: grab, release, push, pull, climb, climb\_down, drop, ladder\_up / ladder\_down / ladder\_off, rope\_jump, swim\_down / swim\_up, surface, hat\_detach, hat\_wear, sub\_enter, sub\_boost, sub\_exit, lever, valve, button, lift (lift doors), throw (huddle). target = id from observe (default nearest that offers the verb). Runs the game until the move completes or fails; returns state before / after, changes and an observation.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `verb` | string | required | `grab`, `release`, `push`, `pull`, `climb`, `climb_down`, `drop`, `ladder_up`, `ladder_down`, `ladder_off`, `rope_jump`, `swim_down`, `swim_up`, `surface`, `hat_detach`, `sub_boost`, `sub_exit`, `lever`, `valve`, `button`, `lift`, `sub_enter`, `hat_wear`, `throw` | What to do (see objects\[\].actions in observe). grab keeps holding across later act / move\_to calls; release lets go. |
| `target` | string |  |  | Object id from observe (o12). Default: nearest object offering the verb. |
| `dir` | string | default `"toward"` | `left`, `right`, `up`, `down`, `toward`, `away` | Direction for push (toward = stick toward the target), pull (always pulls the target toward the body unless left / right; hatches and boards: along their axis), throw, rope\_jump, ladder\_off (left / right jumps off, else drops), sub\_boost, lever (default: the way it can move), valve (right / down = clockwise). |
| `seconds` | number 0.05..10 | default `1` |  | Duration for push, pull, button hold, lift, throw swing, swim\_\*, ladder\_up / ladder\_down, sub\_boost charge, valve turning. |
| `approach` | boolean | default `true` |  | Walk to the target first when it is out of reach. |
| `allow_direct` | boolean | default `false` |  | hat\_detach: if the gesture fails, take the hat off with MindHat.ForceDetach (a direct game call; off keeps takes input-only). |
| `when` | object |  |  | Wait first, then act in the same call: one wait\_until condition, e.g. {hazard\_eta:{target:"h3", min\_s:4}} (4+ s to the next shockwave blow) or {music\_time:{from:0.2, to:1.5}}; not met in when\_timeout\_s = outcome timeout, no input. |
| `when.seconds` | number 0.05..600 |  |  | Game s. |
| `when.grounded` | boolean |  |  | On ground. |
| `when.airborne` | boolean |  |  | In air. |
| `when.state` | string |  |  | Body state. |
| `when.near` | object |  |  | Within m. |
| `when.near.target` | string | required |  | Id. |
| `when.near.within` | number 0.1..100 | default `1` |  | m. |
| `when.far_from` | object |  |  | Beyond m. |
| `when.far_from.target` | string | required |  | Id. |
| `when.far_from.beyond` | number 0.1..200 | default `5` |  | m. |
| `when.hazard_safe` | string |  |  | Hazard id. |
| `when.hazard_eta` | object |  |  | eta\_s >= min\_s. |
| `when.hazard_eta.target` | string | required |  | Hazard id. |
| `when.hazard_eta.min_s` | number 0..600 | default `4` |  | s. |
| `when.music_time` | object |  |  | Music s in \[from, to). |
| `when.music_time.from` | number 0..100000 | required |  | s. |
| `when.music_time.to` | number 0..100000 | required |  | s. |
| `when.music_time.cycle` | number 0..10000 | default `0` |  | s, 0 auto. |
| `when.fsm_state` | object |  |  | FSM state. |
| `when.fsm_state.object` | string | required |  | Id. |
| `when.fsm_state.fsm` | string |  |  | FSM. |
| `when.fsm_state.state` | string | required |  | State. |
| `when.signal` | object |  |  | Signal fired. |
| `when.signal.object` | string | required |  | Id. |
| `when.signal.output` | string | required |  | Output. |
| `when.object_active` | object |  |  | Object active. |
| `when.object_active.target` | string | required |  | Id. |
| `when.object_active.active` | boolean | default `true` |  | Wanted. |
| `when_timeout_s` | number 0.1..45 | default `15` |  | Longest wait for when (wall clock s). |
| `wait_ready_s` | number 0..45 | default `0` |  | Game not taking input yet (load, fade, streaming, preawake, input disabled, respawn): wait up to this many s, then go on in the same call. 0 = answer at once. |
| `observe` | string | default `"concise"` | `concise`, `none` | Observation in the result. |

<a id="tool-jobs"></a>

### Tool: jobs

- Profiles: dev
- Title: Jobs
- Owner: HOST
- Source: src/mcp/DevTools.cs:222

List, inspect, await or cancel multi-frame jobs (loads, actions, waits). Cancelling releases inputs and re-freezes the game.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"list"` | `list`, `status`, `await`, `cancel` | list, status, await or cancel. |
| `id` | string |  |  | Job id (j12) for status / await / cancel. |
| `timeout_s` | number 0..50 | default `10` |  | op await: how long to wait. |

<a id="tool-level"></a>

### Tool: level

- Profiles: dev
- Title: Levels and loads
- Owner: WORLD\_A
- Annotations: destructive
- Source: src/play/PlayLevels.cs:73

Level state and loads. status / list are read-only; wait\_ready waits for the ready predicate. load\_checkpoint (mode respawn \| quiet \| chapter), load\_chapter (0..65), reload (respawn \| chapter \| newgame), step (next \| prev savepoint). chapter, load\_chapter, reload chapter / newgame write the Steam Cloud save and unlock chapters; respawn writes the spawn; quiet locks the save and writes nothing. No confirm or backup is needed unless the user set EditorState play.save\_guard = 1.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"status"` | `status`, `list`, `wait_ready`, `load_checkpoint`, `load_chapter`, `reload`, `step` | status, list, wait\_ready, load\_checkpoint, load\_chapter, reload or step. |
| `subscene` | integer 0..999 |  |  | Savepoint subscene index. |
| `savepoint` | integer 0..999 |  |  | Savepoint index. |
| `chapter` | integer 0..65 |  |  | Chapter index. |
| `name` | string |  |  | op load\_checkpoint: subscene by name instead of subscene (scene or puzzle name, or a part of it that matches one subscene). |
| `what` | string | default `"all"` | `all`, `subscenes`, `chapters` | op list: which rows (all = subscenes and chapters). |
| `mode` | string | default `"respawn"` | `respawn`, `quiet`, `chapter` | op load\_checkpoint. |
| `kind` | string | default `"respawn"` | `respawn`, `chapter`, `newgame` | op reload. |
| `dir` | string | default `"next"` | `next`, `prev` | op step. |
| `filter` | string |  |  | op list: name filter. |
| `limit` | integer 1..300 | default `100` |  | op list: rows. |
| `offset` | integer 0..1000 | default `0` |  | op list: first row. |
| `wait` | boolean | default `true` |  | Wait until ready after a load. |
| `timeout_s` | number 1..50 | default `45` |  | Wait limit. |
| `settle_frames` | integer 0..120 | default `20` |  | Frames after ready. |
| `confirm` | boolean | default `false` |  | Only with play.save\_guard on: required for loads that write the save and unlock chapters. |

<a id="tool-log"></a>

### Tool: log

- Profiles: dev
- Title: InsideDev log
- Owner: HOST
- Annotations: read-only, idempotent
- Source: src/mcp/DevTools.cs:204

The last lines of InsideDev's log (command output, errors, events).

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `lines` | integer 1..400 | default `60` |  | How many lines. |

<a id="tool-logic"></a>

### Tool: logic

- Profiles: dev
- Title: World logic
- Owner: WORLD\_B
- Annotations: destructive
- Source: src/play/WorldLogic.cs:35

Playdead world logic. Read-only: triggers\_near, signal\_ports, fsm\_list, interactables\_near (levers, buttons, gates, doors, lifts, hatches, world flags with their signal inputs). Changes: trigger\_fire (enter / exit as probe, signal or teleport), signal\_in, signal\_out, signal\_wire / signal\_unwire, spawn\_clone, body\_set (rigidbody), anim\_set (legacy Animation). Changes are re-read and get a verdict; signal\_out / signal\_in / fsm events cannot be undone.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | required | `triggers_near`, `trigger_fire`, `signal_ports`, `signal_in`, `signal_out`, `signal_wire`, `signal_unwire`, `fsm_list`, `interactables_near`, `spawn_clone`, `body_set`, `anim_set` | What to do. |
| `object` | string |  |  | Object id, path, #instanceID or unique name. |
| `radius` | number 0.5..200 | default `15` |  | Near ops (m). |
| `kinds` | array of string, up to 8 items |  |  | triggers\_near filter: probe, savepoint, killer, camera, fsm, other; interactables\_near: lever, button, valve, gate, door, elevator, hatch, jetcrate, flag. |
| `edge` | string | default `"enter"` | `enter`, `exit` | trigger\_fire. |
| `mode` | string | default `"probe"` | `probe`, `signal`, `teleport` | trigger\_fire. |
| `input` | string |  |  | signal\_in / signal\_wire target input. |
| `output` | string |  |  | signal\_out / signal\_wire source output. |
| `to` | string |  |  | signal\_wire: receiving object. |
| `filter` | string |  |  | fsm\_list / near ops: name filter. |
| `state` | string |  |  | fsm\_list: only FSMs in this state. |
| `position` | array of number -100000..100000, 3 items |  |  | spawn\_clone: world position. |
| `rotation` | array of number -720..720, 3 items |  |  | spawn\_clone: euler degrees. |
| `preawake` | boolean | default `true` |  | spawn\_clone: run PreAwake on the clone. |
| `velocity` | array of number -1000..1000, 3 items |  |  | body\_set. |
| `angular` | array of number -1000..1000, 3 items |  |  | body\_set. |
| `force` | array of number -100000..100000, 3 items |  |  | body\_set: impulse. |
| `kinematic` | boolean |  |  | body\_set. |
| `wake` | boolean |  |  | body\_set. |
| `clip` | string |  |  | anim\_set: clip name. |
| `time` | number 0..10000 |  |  | anim\_set: seconds. |
| `speed` | number -10..10 |  |  | anim\_set. |
| `play` | boolean |  |  | anim\_set: play / stop. |
| `limit` | integer 1..200 | default `40` |  | Rows. |
| `offset` | integer 0..10000 | default `0` |  | First row. |
| `verify_frames` | integer 0..30 | default `2` |  | Re-read after this many frames. |

<a id="tool-menu"></a>

### Tool: menu

- Profiles: play
- Title: Menus
- Owner: WORLD\_A
- Source: src/play/Menus.cs:47

Read and drive the pause menu without a mouse: state lists the selected group's items with ids; open / close; up, down, left, right, submit, back (repeat n times); select jumps to an item id. Chapter loads, Quit and Reset To Defaults are hidden unless allow\_destructive; quitting is never done. At the title (the boy waits for a button) submit starts the game. message\_answer answers a storage message (answer; confirm:true when play.save\_guard is on). Works while the game is frozen.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"state"` | `state`, `open`, `close`, `up`, `down`, `left`, `right`, `submit`, `back`, `select`, `message_answer` | state, open, close, up, down, left, right, submit, back, select or message\_answer. |
| `id` | string |  |  | op select: item id from state (m3). |
| `repeat` | integer 1..20 | default `1` |  | Navigation presses. |
| `allow_destructive` | boolean | default `false` |  | Show and allow Quit, chapter loads over progress, Reset To Defaults. |
| `answer` | string |  | `ok`, `cancel` | op message\_answer: ok (CONTINUE) or cancel (RETRY). |
| `confirm` | boolean | default `false` |  | op message\_answer: required with play.save\_guard on (CONTINUE can overwrite or drop the save). |

<a id="tool-move_to"></a>

### Tool: move_to

- Profiles: play
- Title: Move to
- Owner: NAV
- Annotations: always loaded
- Source: src/play/Navigator.cs:49

Walk or run the controlled body to an object id (target), a world x, or a relative dx (metres, + right). Climbs small steps, jumps small gaps (jump\_gaps) and stops at walls, pits and ledges it cannot pass with outcome blocked and a reason. Runs the game until arrived / blocked / fell / died / timeout.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `target` | string |  |  | Object id from observe (o12). |
| `x` | number -100000..100000 |  |  | World x (m). |
| `dx` | number -500..500 |  |  | Metres from the body (+ right, - left). |
| `y` | number -100000..100000 |  |  | World y for swimming or climbing targets (m). |
| `gait` | string | default `"run"` | `walk`, `run` | walk or run. |
| `tolerance` | number 0.05..3 | default `0.3` |  | Arrival tolerance (m). |
| `stop` | string | default `"adjacent"` | `adjacent`, `exact` | adjacent = within grab reach facing the target, exact = at the point. |
| `max_steps` | integer 1..1800 | default `600` |  | Logic-step limit (60 per second). |
| `jump_gaps` | boolean | default `true` |  | Jump gaps and low walls on the way. |
| `climb` | boolean | default `true` |  | Climb onto ledges in the way. |
| `when` | object |  |  | Wait first, then act in the same call: one wait\_until condition, e.g. {hazard\_eta:{target:"h3", min\_s:4}} (4+ s to the next shockwave blow) or {music\_time:{from:0.2, to:1.5}}; not met in when\_timeout\_s = outcome timeout, no input. |
| `when.seconds` | number 0.05..600 |  |  | Game s. |
| `when.grounded` | boolean |  |  | On ground. |
| `when.airborne` | boolean |  |  | In air. |
| `when.state` | string |  |  | Body state. |
| `when.near` | object |  |  | Within m. |
| `when.near.target` | string | required |  | Id. |
| `when.near.within` | number 0.1..100 | default `1` |  | m. |
| `when.far_from` | object |  |  | Beyond m. |
| `when.far_from.target` | string | required |  | Id. |
| `when.far_from.beyond` | number 0.1..200 | default `5` |  | m. |
| `when.hazard_safe` | string |  |  | Hazard id. |
| `when.hazard_eta` | object |  |  | eta\_s >= min\_s. |
| `when.hazard_eta.target` | string | required |  | Hazard id. |
| `when.hazard_eta.min_s` | number 0..600 | default `4` |  | s. |
| `when.music_time` | object |  |  | Music s in \[from, to). |
| `when.music_time.from` | number 0..100000 | required |  | s. |
| `when.music_time.to` | number 0..100000 | required |  | s. |
| `when.music_time.cycle` | number 0..10000 | default `0` |  | s, 0 auto. |
| `when.fsm_state` | object |  |  | FSM state. |
| `when.fsm_state.object` | string | required |  | Id. |
| `when.fsm_state.fsm` | string |  |  | FSM. |
| `when.fsm_state.state` | string | required |  | State. |
| `when.signal` | object |  |  | Signal fired. |
| `when.signal.object` | string | required |  | Id. |
| `when.signal.output` | string | required |  | Output. |
| `when.object_active` | object |  |  | Object active. |
| `when.object_active.target` | string | required |  | Id. |
| `when.object_active.active` | boolean | default `true` |  | Wanted. |
| `when_timeout_s` | number 0.1..45 | default `15` |  | Longest wait for when (wall clock s). |
| `wait_ready_s` | number 0..45 | default `0` |  | Game not taking input yet (load, fade, streaming, preawake, input disabled, respawn): wait up to this many s, then go on in the same call. 0 = answer at once. |
| `observe` | string | default `"concise"` | `concise`, `none` | Observation in the result. |

<a id="tool-notes"></a>

### Tool: notes

- Profiles: play
- Title: Flagged moments
- Owner: GUIDE
- Source: src/play/Notes.cs:35

Moments the user flagged in the game with the notes hotkey (or added here): frame, checkpoint, body position, screenshot. list the latest, get one (image attaches its screenshot), add a note at the current moment, clear all (the old list is kept as a backup file).

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"list"` | `list`, `get`, `add`, `clear` | list, get, add or clear. |
| `id` | integer 0..1000000 |  |  | op get. |
| `text` | string (max 500 chars) |  |  | op add. |
| `limit` | integer 1..50 | default `10` |  | op list. |
| `image` | boolean | default `false` |  | op get: attach the screenshot. |

<a id="tool-observe"></a>

### Tool: observe

- Profiles: play, dev
- Title: Observe
- Owner: OBSERVE
- Annotations: read-only, idempotent, always loaded
- Source: src/play/Observer.cs:79

What the agent sees, as JSON: screen and readiness (next = what to call when not ready), the controlled body, nearby objects and hazards with ids and dx/dy in metres, checkpoint and guide topic, events. Call guide() once, then observe. Rules: the game is frozen between action calls (step mode); ids die on every load; at the title menu {op:"submit"} starts; call restore when done. image attaches a screenshot (marked = ids drawn); if your client drops images, use screenshot. Units: world metres, x right, y up, z depth; dx / dy are from the body; 60 steps = 1 s. Every observation is built from the objects that exist at that moment. An id keeps working after a death or reload when its object is back at the same place in the hierarchy; an id whose object is gone answers stale\_id. detail delta lists only what changed since your previous observation.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `detail` | string | default `"concise"` | `concise`, `full`, `delta` | concise (trimmed to 2400 characters, about 800 tokens), full (adds x / y, path, input, bodies, menu, camera, perf), or delta (concise, but objects and hazards only when new, changed, moved or a threat; gone lists the ids that left; the first call and the first after a load are complete). |
| `radius` | number 1..60 | default `12` |  | Metres around the body for objects and hazards. |
| `max_objects` | integer 0..40 | default `12` |  | Objects to list, nearest first. |
| `image` | string | default `"none"` | `none`, `preview`, `marked` | Attach a screenshot: none, preview or marked. |
| `max_width` | integer 160..1920 | default `960` |  | Image width (px). |

<a id="tool-reflect"></a>

### Tool: reflect

- Profiles: dev
- Title: Reflection
- Owner: WORLD\_B
- Annotations: destructive
- Source: src/play/Reflect.cs:35

Read and change anything by reflection: get / set a field or property (verified: confirmed, reverted\_by\_game, not\_applied, unreadable), call a method with JSON arguments, find\_methods, describe\_type. object + component for instances, type for statics. member may be a dotted path (properties.swim.infiniteAir). Arguments: numbers, strings, bools, null, \[x,y,z\] for Vector3, enum names, object ids or paths for object parameters. Steam and OS calls and raising the master volume are refused (save writes too when the user set play.save\_guard = 1). Batch: get {members:\[..\]} reads several members in one call (values\[\]), set {sets:\[{member, value}\]} writes several and verifies them together (results\[\]); get {repeat\_frames:n} reads again n frames later (value\_k, changed). Refused: calls and writes that reach files, processes, assemblies, the registry (PlayerPrefs), the display mode, the network or quit, also through UnityEngine (Application.CaptureScreenshot, WWW ...) and through the tool's own classes; in game code every AkSoundEngine member named \*Capture\*, \*Dump\*, \*Write\* or \*Save\* (StartOutputCapture ...), writes to static readonly fields, and writes to string fields or properties named \*path\*, \*file\*, \*dir\* or \*folder\*; reading the MCP server's settings or token (HostSettings, EditorState, members named \*token\* or \*password\*). Other game methods are not checked for file access: a game method that writes its own file at its own place still runs.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | required | `get`, `set`, `call`, `find_methods`, `describe_type` | get, set, call, find\_methods or describe\_type. |
| `object` | string |  |  | Object id, path, #instanceID or unique name. |
| `component` | string |  |  | Component type name (exact, then suffix). |
| `type` | string |  |  | Type name for statics, find\_methods, describe\_type. |
| `member` | string |  |  | Field, property or method name. |
| `value` | any |  |  | op set: new value (JSON). |
| `args` | array of any, up to 8 items |  |  | op call: arguments (JSON). |
| `members` | array of string, up to 32 items |  |  | op get: several members of the same object or type in one call (dotted paths allowed); the result has values\[\] instead of value. |
| `sets` | array of object, up to 16 items |  |  | op set: several writes on the same object or type, each {member, value}; checked one by one, verified together; the result has results\[\]. |
| `sets[].member` | string | required |  | Field or property (dotted path allowed). |
| `sets[].value` | any |  |  | New value (JSON). |
| `repeat_frames` | integer 0..120 | default `0` |  | op get: read again after this many rendered frames (value\_k, changed). |
| `text` | string |  |  | op find\_methods: name filter. |
| `verify_frames` | integer 0..30 | default `2` |  | op set: re-read after frames. |
| `restore_on_end` | boolean | default `false` |  | op set: undo it on restore. |
| `limit` | integer 1..200 | default `40` |  | Rows. |

<a id="tool-render"></a>

### Tool: render

- Profiles: dev
- Title: Render
- Owner: WORLD\_B
- Annotations: idempotent
- Source: src/play/AudioRender.cs:49

Render switches: state (read-only), post (PDPostCombined on / off), fog, default\_lights (neutral light rig), debug\_view (PDRenderDebug modes), debug\_flags (EditorMode flags, list or set one), shot\_route (Playdead's 127-savepoint camera route: list, or apply entry index to the free camera). Restored by restore.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"state"` | `state`, `post`, `fog`, `default_lights`, `debug_view`, `debug_flags`, `shot_route` | state, post, fog, default\_lights, debug\_view, debug\_flags or shot\_route. |
| `on` | boolean |  |  | op post / fog / default\_lights / debug\_flags. |
| `mode` | string |  | `off`, `Depth`, `Normals`, `NormalsContrast`, `Lighting`, `Shadows`, `Transparency`, `Unlit`, `UnlitTransparency` | op debug\_view. |
| `flag` | string |  |  | op debug\_flags: EDebugFlags name (omit to list). |
| `index` | integer 0..1000 |  |  | op shot\_route: entry to apply. |
| `apply` | boolean | default `false` |  | op shot\_route: move the free camera to the entry. |
| `limit` | integer 1..200 | default `40` |  | Rows. |
| `offset` | integer 0..1000 | default `0` |  | First row. |

<a id="tool-restore"></a>

### Tool: restore

- Profiles: play, dev
- Title: Restore normal play
- Owner: TIME
- Annotations: idempotent
- Source: src/play/Restore.cs:41

Put the game back to normal: cancel the running action, release held input, give control back to the player, realtime at speed 1, camera back to the game, god / hazards / air / stream pins / menu lock / render and audio changes made through MCP undone. Lists what it reset. The save lock is kept unless include\_save\_lock. dry\_run only lists what it would do.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `only` | array of string, up to 32 items |  |  | Only these registry keys or key prefixes (camera, time, input, hazard:, ...). Default all. |
| `include_save_lock` | boolean | default `false` |  | Also release the save lock (the game then writes INSIDE.sav). |
| `keep_agent_input` | boolean | default `false` |  | Keep AgentCore installed (neutral input) instead of human control. |
| `dry_run` | boolean | default `false` |  | Change nothing: list what a restore would cancel (would\_cancel), reset (would\_reset) and keep. |
| `observe` | string | default `"concise"` | `concise`, `none` | Observation in the result. |

<a id="tool-run_scenario"></a>

### Tool: run_scenario

- Profiles: dev
- Title: Run a scenario
- Owner: GUIDE
- Annotations: destructive
- Source: src/play/GuideScenario.cs:24

Run a scripted test: steps in order through the agent input and the step clock. do = load (checkpoint), press / hold / release (stick, jump, action), wait (steps or seconds), until (one wait\_until condition within within\_s), expect (observe path cmp value), capture (screenshot), command (console), observe, note. Stops at the first failed step unless stop\_on\_fail is false. A run longer than the call budget ends with outcome timeout and next = the step to pass as start\_at. op check only validates; op list shows scenario files. Loads need no confirm unless the user set EditorState play.save\_guard = 1 (then loads that write the save need confirm:true in the step).

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"run"` | `run`, `check`, `list` | run, check (validate only) or list (scenario files). |
| `file` | string |  |  | Scenario file name in \_mod/scenarios or &lt;guide>/scenarios (without .json), instead of steps. |
| `steps` | array of object, 1..200 items |  |  | Inline steps (instead of file). |
| `steps[].do` | string | required | `load`, `press`, `hold`, `release`, `wait`, `until`, `expect`, `capture`, `command`, `observe`, `note` | Step kind. |
| `steps[].label` | string (max 200 chars) |  |  | Name shown in results. |
| `steps[].stick` | array of number -1..1, 2 items |  |  | press / hold: stick \[x, y\], each -1..1 (0 or magnitude 0.3..1; above 0.8 runs). |
| `steps[].jump` | boolean |  |  | press / hold: jump button (rising edge, buffered 0.25 s). |
| `steps[].action` | boolean |  |  | press / hold: action button (tap under 0.3 s or hold = grab). |
| `steps[].steps` | integer 1..3600 |  |  | press / wait: logic steps (60 = 1 s). |
| `steps[].seconds` | number 0.02..60 |  |  | wait: game seconds instead of steps. |
| `steps[].stop_on_death` | boolean | default `true` |  | press / wait / until: a death fails the step (false runs through it). |
| `steps[].until` | any |  |  | until: one wait\_until condition object, e.g. {"grounded":true} or {"near":{"target":"o3"}}. |
| `steps[].within_s` | number 0.1..50 | default `10` |  | until: game-time limit in seconds. |
| `steps[].path` | string (max 200 chars) |  |  | expect: observe path (body.state, body.x, screen, checkpoint.savepoint, objects.0.kind, hazards.length). |
| `steps[].cmp` | string |  | `eq`, `ne`, `lt`, `le`, `gt`, `ge`, `exists`, `missing`, `contains` | expect: comparison (default eq with value, exists without). |
| `steps[].value` | any |  |  | expect: value to compare with. |
| `steps[].tol` | number 0..1000 | default `0.01` |  | expect: number tolerance. |
| `steps[].mode` | string | default `"respawn"` | `respawn`, `quiet`, `chapter`, `reload_respawn`, `reload_chapter`, `newgame` | load: respawn, quiet, chapter (writes the save), reload\_respawn, reload\_chapter or newgame. |
| `steps[].subscene` | integer 0..999 |  |  | load: savepoint subscene index. |
| `steps[].savepoint` | integer 0..999 | default `0` |  | load: savepoint index. |
| `steps[].confirm` | boolean | default `false` |  | load: allow a load that writes INSIDE.sav (needed only with play.save\_guard on). |
| `steps[].shot` | string | default `"full"` | `preview`, `full`, `marked` | capture: preview, full or marked (the PNG stays on disk; only the path is returned). |
| `steps[].cmd` | string (max 300 chars) |  |  | command: console command line. |
| `steps[].text` | string (max 300 chars) |  |  | note: text of the note. |
| `start_at` | integer 0..199 | default `0` |  | First step index to run (continue after a timeout). |
| `stop_on_fail` | boolean |  |  | Stop at the first failed step (default true; when not given a file's own stop\_on\_fail applies). |
| `observe` | string | default `"concise"` | `concise`, `none` | Observation at the end. |

<a id="tool-save"></a>

### Tool: save

- Profiles: dev
- Title: Save, orbs, endings
- Owner: WORLD\_A
- Annotations: destructive
- Source: src/play/Saves.cs:53

The Steam Cloud save INSIDE.sav. info and savepoints\_near are read-only; backup copies it to \_mod/save\_backups; lock / unlock stop and resume the game's save writes (released when the game quits). orbs reads or edits orb bits 0..13 and 14 (power secret), only local bits, confirm:true to edit; wrist\_secret on / off; ending credits \| secret \| restart (secret clears orbs: confirm; restart = new game). Never touches Steam achievements directly. Save backups and save confirms apply only when the user set EditorState play.save\_guard = 1.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"info"` | `info`, `backup`, `lock`, `unlock`, `orbs`, `wrist_secret`, `ending`, `savepoints_near` | info, backup, lock, unlock, orbs, wrist\_secret, ending or savepoints\_near. |
| `action` | string | default `"read"` | `read`, `set`, `clear` | op orbs. |
| `bits` | array of integer 0..14, up to 15 items |  |  | op orbs: bits 0..14. |
| `layer` | string | default `"both"` | `ever`, `recent`, `both` | op orbs: ever, recent or both. |
| `on` | boolean |  |  | op wrist\_secret. |
| `kind` | string |  | `credits`, `secret`, `restart` | op ending. |
| `radius` | number 1..500 | default `30` |  | op savepoints\_near (m). |
| `confirm` | boolean | default `false` |  | Required for orb edits and ending secret; with play.save\_guard on also for ending restart and unlock after a cheat. |

<a id="tool-screenshot"></a>

### Tool: screenshot

- Profiles: play, dev
- Title: Screenshot
- Owner: HOST
- Annotations: read-only, idempotent
- Source: src/mcp/DevTools.cs:155

Capture the game screen. preview = inline image (default 960 px wide, about 700 image tokens); marked = preview with observe ids drawn on objects; full or path = full-size PNG file only. format jpeg returns a JPEG several times smaller than the PNG. The JSON text has ok, mode, format, width, height, src\_width, src\_height, path and uri (the image as a file), bytes, black (an almost black frame), frame, ms and marks \[{id, label, x, y}\] (pixels in the image). No structuredContent, so clients that pass only structured results still get the image.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `mode` | string | default `"preview"` | `preview`, `marked`, `full`, `path` | preview, marked, full or path. |
| `max_width` | integer 0..4096 | default `960` |  | Scale down to at most this width (preview / marked). 0 = no scaling. |
| `clean` | boolean | default `true` |  | Hide InsideDev panels, HUD and overlays for the shot. |
| `fast` | boolean | default `false` |  | full / path: return once the file is written, without the black check (for many frames use time {op:"record"}). |
| `format` | string | default `"png"` | `png`, `jpeg` | preview / marked: png (lossless) or jpeg (much smaller). |
| `quality` | integer 1..100 | default `80` |  | jpeg quality. |

<a id="tool-select"></a>

### Tool: select

- Profiles: dev
- Title: Select in editor
- Owner: HOST
- Annotations: idempotent
- Source: src/mcp/DevTools.cs:174

Select an object in the InsideDev editor (Inspector, Explorer and graph follow the selection). Editor-only, no game change.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `object` | string | required |  | Object id, path, #instanceID or name (see inspect). |

<a id="tool-set_active"></a>

### Tool: set_active

- Profiles: dev
- Title: Switch object on/off
- Owner: HOST
- Annotations: idempotent
- Source: src/mcp/DevTools.cs:177

Switch a GameObject on or off (recorded in History, undo with command 'undo'). Re-reads the state after 2 frames. A name that fits several objects acts on the nearest one and says so (matches, candidates).

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `object` | string | required |  | Object id, path, #instanceID or name (see inspect). |
| `active` | boolean | required |  | true = on, false = off. |
| `strict` | boolean | default `false` |  | Refuse (error ambiguous) when a plain name fits more than one object. |

<a id="tool-show_panel"></a>

### Tool: show_panel

- Profiles: dev
- Title: Show an editor panel
- Owner: HOST
- Annotations: idempotent
- Source: src/mcp/DevTools.cs:200

Open the InsideDev editor on a panel, optionally docking and sizing it. Panels: spawns, levels, triggers, hidden, world, audio, inspector, cheats, console, diag, settings, objects, explorer, graph, events, sounds, atimeline, wwiseapi, history, audiodb, logic, modified, fsmgraph, fsmfind; none closes the editor.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `panel` | string | required |  | Panel id, or none. |
| `dock` | string |  | `left`, `right`, `bottom` | Dock side. |
| `size` | number 0.1..0.7 |  |  | Dock size as a fraction of the screen (left/right 0.12-0.6, bottom 0.1-0.7). |

<a id="tool-signal"></a>

### Tool: signal

- Profiles: dev
- Title: Fire a signal
- Owner: HOST
- Annotations: destructive
- Source: src/mcp/DevTools.cs:195

Fire one of an object's Playdead signal outputs (raises every connection of that output, like the game does). Cannot be undone.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `object` | string | required |  | Object with the signal output. |
| `output` | string | required |  | Signal output name (see inspect / fsm\_find). |
| `strict` | boolean | default `false` |  | Refuse (error ambiguous) when a plain object name fits more than one object. |

<a id="tool-state_diff"></a>

### Tool: state_diff

- Profiles: dev
- Title: State diff
- Owner: WORLD\_B
- Annotations: idempotent
- Source: src/play/StateDiff.cs:79

Snapshot every component field around the body (or a point / root) and diff later: snapshot name, act or wait, then diff name (against a new snapshot or another named one). Shows exactly which fields a lever, trigger or action changed. Covers active flags, transforms, enabled flags, rigidbodies, FSM states and variables, and the public and private fields of game scripts.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"snapshot"` | `snapshot`, `diff`, `list`, `drop` | snapshot, diff, list or drop. |
| `name` | string | default `"a"` |  | Snapshot name. |
| `against` | string |  |  | op diff: compare with this snapshot instead of a new one. |
| `center` | string |  |  | Object id or path (default the body). |
| `radius` | number 1..50 | default `8` |  | Metres. |
| `root` | string |  |  | Hierarchy path: everything under it instead of a radius. |
| `components` | array of string, up to 20 items |  |  | Only these component types. |
| `max_fields` | integer 100..50000 | default `5000` |  | Field cap per snapshot. |
| `limit` | integer 1..500 | default `100` |  | op diff: rows. |
| `ignore_noise` | boolean | default `false` |  | op diff: leave out what changes by itself: Animation clip times, Rigidbody velocity / angular / sleeping, Transform changes of at most 0.02. |
| `group` | boolean | default `false` |  | op diff: one entry per object with its changes (objects\[\]) instead of flat rows (diff\[\]). |
| `rolling` | boolean | default `false` |  | op diff without against: the fresh snapshot replaces the named one, so the next diff shows what changed since this one. |

<a id="tool-stream"></a>

### Tool: stream

- Profiles: dev
- Title: Streaming
- Owner: WORLD\_A
- Source: src/play/PlayLevels.cs:95

Subscene streaming. Streaming follows the camera: a camera away from the boy unloads his area after 4 s of game time. pin keeps a subscene loaded (AlwaysLoaded), load force-loads one (SavepointTask.WaitForSubscene recipe), freeze stops all streaming changes. subscene -1 = the body's area. status lists loaded, pinned and the body's subscenes; filter narrows by name (filter all = every one).

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"status"` | `status`, `pin`, `unpin`, `load`, `freeze`, `unfreeze` | status, pin, unpin, load, freeze or unfreeze. |
| `subscene` | integer -1..999 | default `-1` |  | Savepoint subscene index, -1 = the body's area. |
| `name` | string |  |  | Subscene name instead of the index. |
| `activate` | boolean | default `true` |  | op load: activate after loading. |
| `filter` | string |  |  | op status: name filter. |

<a id="tool-time"></a>

### Tool: time

- Profiles: play, dev
- Title: Game clock
- Owner: TIME
- Source: src/play/Stepper.cs:60

Game clock. The game runs in realtime until the first action call; from then on it is in step mode: frozen between calls, running only inside act, move\_to, interact, wait\_until and time step. op realtime lets it run (speed scales it). Mines shockwave, lineup and lab lift / water lock run on the music clock: step-mode runs there are paced to real time (the music pauses while frozen), slow motion desyncs them; act / move\_to / interact take when to wait for a safe phase inside the call. op vsync {on:false}: no vSync in realtime; {on:true}: the game's own vSync (for a real-time film); state has fps. op record films the game in real time (ffmpeg): by default its own frames (grec, windows over the game never get in); source desktop captures the screen area of the window instead, windows on top included; record\_stop ends it.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | default `"state"` | `state`, `step_mode`, `realtime`, `pause`, `resume`, `step`, `speed`, `rng_seed`, `vsync`, `record`, `record_stop` | state, step\_mode, realtime, pause, resume, step, speed, rng\_seed, vsync, record or record\_stop. |
| `steps` | integer 1..600 | default `1` |  | op step: logic steps to run (1/60 s each), then freeze. |
| `speed` | number 0.05..4 | default `1` |  | op speed / realtime: game speed while running. |
| `seed` | integer 0..2147483647 | default `0` |  | op rng\_seed: PDRandom.seed, RandomGenerator.ResetAll, UnityEngine.Random.seed. |
| `observe` | string | default `"concise"` | `concise`, `none` | op step: observation in the result. |
| `on` | boolean | default `true` |  | op vsync: true = the game's own vSync, false = off; either wins over editor.cfg play.vsync\_off until restore. |
| `source` | string | default `"game"` | `game`, `desktop` | op record: game = the game's own frames (grec; windows over the game never get in); desktop = the screen area of the window (ffmpeg ddagrab; windows on top are recorded too). |
| `record_fps` | integer 5..60 | default `30` |  | op record: frames per second of the video (source game: every n-th rendered frame that gives about this rate). |
| `every` | integer 1..600 |  |  | op record, source game: record every n-th rendered frame (instead of record\_fps). |
| `cq` | integer 0..51 | default `16` |  | op record: constant quality of the encoder (lower is better and larger; 16 is near lossless for review). |
| `raise` | boolean | default `false` |  | op record, source desktop: lift the game window to the top without taking focus (the capture records what is on screen). |
| `file` | string |  |  | op record: file name in \_mod/recordings (default grec\_&lt;date>\_&lt;time>.mkv, desktop rec\_&lt;date>\_&lt;time>.mkv). |
| `ffmpeg` | string |  |  | op record: ignored, kept so old clients are not refused; a client never names the program to start (ffmpeg comes from editor.cfg ffmpeg.path, INSIDEDEV\_FFMPEG, &lt;game>/\_mod/ffmpeg or the PATH). |
| `review_fps` | number 0..30 | default `0` |  | op record\_stop: also cut JPG review frames at this rate into &lt;file>\_frames (0 = none). |

<a id="tool-ui"></a>

### Tool: ui

- Profiles: dev
- Title: UI switches
- Owner: WORLD\_A
- Annotations: destructive
- Source: src/play/Menus.cs:65

Game UI switches: menu\_lock (MenuManager.reserveMenu: Esc does nothing), title\_hide (TitleManager.HideAll), overlay\_text (MessageOverlay.Show), settings (volume 0 only = mute, brightness, rumble, fullscreen, resolution; brightness / rumble write INSIDE.sav), message\_answer (storage error prompt), savepoint\_menu (Playdead's dev savepoint picker, INFERRED; it reads the physical controller).

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `op` | string | required | `menu_lock`, `title_hide`, `overlay_text`, `settings`, `message_answer`, `savepoint_menu` | menu\_lock, title\_hide, overlay\_text, settings, message\_answer or savepoint\_menu. |
| `on` | boolean |  |  | op menu\_lock / title\_hide. |
| `text` | string (max 200 chars) |  |  | op overlay\_text. |
| `seconds` | number 0.5..30 | default `3` |  | op overlay\_text. |
| `volume` | number 0..1 |  |  | op settings: master volume 0..1. |
| `brightness` | number 0..1 |  |  | op settings: 0..1. |
| `rumble` | boolean |  |  | op settings. |
| `fullscreen` | boolean |  |  | op settings. |
| `resolution` | integer 0..64 |  |  | op settings: index into the resolution list. |
| `answer` | string |  | `ok`, `cancel` | op message\_answer. |
| `confirm` | boolean | default `false` |  | Only with play.save\_guard on: required for message\_answer and settings that write the save. |

<a id="tool-wait_until"></a>

### Tool: wait_until

- Profiles: play, dev
- Title: Wait until
- Owner: TIME
- Annotations: always loaded
- Source: src/play/Waits.cs:69

Let the game run until one condition in any holds (steps, seconds, grounded, controllable, ready, checkpoint\_changed, dead, alive, state, screen, near, far\_from, hazard\_safe, hazard\_eta, music\_time, fsm\_state, signal, object\_active, job), then freeze. A timeout is not an error: check satisfied. Use after loads, respawns and timed hazards; to act right after a phase use act / move\_to / interact {when}.

| Argument | Type | Required / default | Enum | Description |
|---|---|---|---|---|
| `any` | array of object, 1..8 items | required |  | Conditions; the first that holds ends the wait. Each item has exactly one key. |
| `any[].steps` | integer 1..36000 |  |  | After this many logic steps. |
| `any[].seconds` | number 0.05..600 |  |  | After this much game time. |
| `any[].grounded` | boolean |  |  | Body on the ground. |
| `any[].airborne` | boolean |  |  | Body in the air. |
| `any[].controllable` | boolean |  |  | Input reaches the body. |
| `any[].ready` | boolean |  |  | Load finished and playable (PlayLevels.Ready). |
| `any[].loaded` | boolean |  |  | No load in progress. |
| `any[].checkpoint_changed` | boolean |  |  | A new savepoint was reached. |
| `any[].dead` | boolean |  |  | Body died. |
| `any[].alive` | boolean |  |  | Body alive again. |
| `any[].state` | string |  |  | Body state name (Run, Jump, Ladder, Swim, ...). |
| `any[].screen` | string |  | `title`, `loading`, `in_world`, `dead`, `cutscene`, `pause_menu`, `menu`, `credits`, `message`, `unknown` | Screen value. |
| `any[].near` | object |  |  | Body within distance of an object. |
| `any[].near.target` | string | required |  | Object id or path. |
| `any[].near.within` | number 0.1..100 | default `1` |  | Metres. |
| `any[].far_from` | object |  |  | Body farther than distance from an object or hazard. |
| `any[].far_from.target` | string | required |  | Id or path. |
| `any[].far_from.beyond` | number 0.1..200 | default `5` |  | Metres. |
| `any[].hazard_safe` | string |  |  | Hazard id: its threat is over or the body is in cover. |
| `any[].hazard_eta` | object |  |  | A hazard's eta\_s is at least min\_s (the shockwave: that many seconds before the next blow). |
| `any[].hazard_eta.target` | string | required |  | Hazard id (h3). |
| `any[].hazard_eta.min_s` | number 0..600 | default `4` |  | Seconds. |
| `any[].music_time` | object |  |  | Music position in \[from, to) s, folded into cycle (0 = the running shockwave's cycle, else not folded); from > to wraps. |
| `any[].music_time.from` | number 0..100000 | required |  | s. |
| `any[].music_time.to` | number 0..100000 | required |  | s. |
| `any[].music_time.cycle` | number 0..10000 | default `0` |  | Cycle length (s), 0 = auto. |
| `any[].fsm_state` | object |  |  | An FSM enters a state. |
| `any[].fsm_state.object` | string | required |  | Object id or path. |
| `any[].fsm_state.fsm` | string |  |  | FSM name. |
| `any[].fsm_state.state` | string | required |  | State name. |
| `any[].signal` | object |  |  | A signal output fires. |
| `any[].signal.object` | string | required |  | Object id or path. |
| `any[].signal.output` | string | required |  | Signal output name. |
| `any[].object_active` | object |  |  | An object becomes active / inactive. |
| `any[].object_active.target` | string | required |  | Id or path. |
| `any[].object_active.active` | boolean | default `true` |  | Wanted state. |
| `any[].job` | string |  |  | Job id (j3): it finished. |
| `timeout_s` | number 0.1..50 | default `20` |  | Wall-clock limit. |
| `max_steps` | integer 1..36000 | default `3600` |  | Game-step limit. |
| `then` | string | default `"freeze"` | `freeze`, `run` | step mode: freeze after (default) or keep running. |
| `observe` | string | default `"concise"` | `concise`, `none` | Observation in the result. |
