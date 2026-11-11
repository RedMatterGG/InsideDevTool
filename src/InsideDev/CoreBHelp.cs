using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace InsideDev
{
    // The one table of bridge commands: name, usage, one line of text, the least number of arguments. Remote.Run asks it
    // before it dispatches: a word that is not here never reaches the switch in Remote.cs, so a command cannot exist
    // without its help entry, and a call with too few arguments is answered with the usage line. `help` prints the
    // table, `help <command>` one entry. Console rows are the words of the in-game console (DevRoot.cs, Commands.Run),
    // which the bridge passes on.
    public static class CoreBHelp
    {
        public sealed class Cmd
        {
            public string group, name, usage, text;
            public int min;         // arguments a call needs at least
            public bool console;    // handled by the console's Commands.Run only
            public bool forwarded;  // handed to another part that has its own argument checks and help
        }

        const string Basics = "basics", Window = "window and render", Ui = "editor", Boy = "boy and camera", Objects = "objects",
                     Logic = "state machines, signals, code", History = "history and mods", Audio = "audio", Memory = "memory",
                     Record = "recording", Console = "console";

        static readonly Cmd[] table =
        {
            C(Basics, "ping", 0, "ping", "is the tool there: answers pong and its version"),
            C(Basics, "help", 0, "help [command]", "every command, or usage and text of one"),
            C(Basics, "state", 0, "state [json]", "snapshot: character, savepoint, streaming, nearest triggers, audio, console tail; json = one object"),
            C(Basics, "where", 0, "where [json]", "the boy's position and the current savepoint; json = one object"),
            C(Basics, "boypos", 0, "boypos", "position of the boy"),
            C(Basics, "doctor", 0, "doctor [json]", "self-check of every part: versions, loader, folders, free disk, ffmpeg, window, time scale, errors"),
            C(Basics, "cfg", 0, "cfg [list] [filter] [json] | cfg get <key> [json] | cfg set <key> <value> | cfg reset <key>", "settings of _mod\\editor.cfg: value, default, owner, text; set saves, reset returns to the default"),
            C(Basics, "background", 0, "background [status|auto|always|off] [json]", "when the background scanners run: auto = only while the tool is in use, always = as before 0.11.0, off = only on request"),
            C(Basics, "quit", 0, "quit", "closes the game the way its own exit does"),
            C(Basics, "remote", 0, "remote [on|off]", "file bridge (_mod\\cmd) on or off for this session"),
            C(Basics, "screenshot", 0, "screenshot [tag]", "PNG of the picture into _mod\\cmd\\shot_<time>[_tag].png at the end of the frame"),
            C(Basics, "clip", 0, "clip [text]", "shows the clipboard, or puts text on it"),
            C(Basics, "ext", 0, "ext [load] [json] | ext json", "loaded extension modules, their cost and levels; load picks up new _mod\\InsideDev.*.dll files without a restart"),
            C(Basics, "live", 0, "live ...", "commands of the live link extension", true),

            C(Window, "diag", 0, "diag [json]", "report of the editor's renderer"),
            C(Window, "cams", 0, "cams [json]", "every camera with its targets and order"),
            C(Window, "render", 0, "render [auto|eof|overlay|gamecam|probe|reset|snapshot|board [on|off]]", "backend of the editor's own drawing"),
            C(Window, "bg", 0, "bg [on|off]", "Application.runInBackground: keep the game running while its window is not focused"),
            C(Window, "fullscreen", 0, "fullscreen [on|off]", "full screen or window"),
            C(Window, "res", 2, "res <w> <h> [full]", "screen resolution"),
            C(Window, "postfx", 0, "postfx [json] | postfx on|off|default <Type> | postfx set <Type> <field> <value>", "post effects of the gameplay camera"),
            C(Window, "perf", 0, "perf [json] | perf reset [json] | perf enable <subsystem> | perf hide <roots> [part] | perf show", "frame time of each editor subsystem, the background mode and the garbage per frame"),
            C(Window, "focustest", 0, "focustest <n> [awayMs] [backMs] [alttab|minimize] [bg on|off] [force] | focustest stop | focustest status [json]", "switches the real window focus away and back (sends OS input; force sends the keys although the game window could not be brought to the front)"),
            C(Window, "keyhold", 0, "keyhold [right|left|up|<hex vk>] [ms] [text] | keyhold status", "holds a real OS key (sends OS input); status = the last hold"),

            C(Ui, "panel", 0, "panel [on|off] [tab#]", "editor panel open or closed"),
            C(Ui, "ui", 0, "ui [status] | ui scale <f> | ui layout dock|window|list | ui layout save|load|delete <name> | ui reset | ui show <panel> | ui dock left|right|bottom <fraction>|on|off | ui window <x> <y> <w> <h> | ui put <panel> left|right|bottom | ui docks [json] | ui focus <id>|none | ui scroll <id> <y> | ui record [on|off] | ui find|clicktext|hovertext <text> [nth] | ui widgets [filter] | ui click|rclick|hover <x> <y> | ui renderer gl|mesh", "layout of the editor and scripted clicks on its widgets"),
            C(Ui, "uifield", 0, "uifield", "text of the Inspector's find field"),
            C(Ui, "palette", 0, "palette <query> [#n]", "hits of the command palette; #n opens hit n"),
            C(Ui, "cmd", 0, "cmd [id]", "editor commands (undo, pick, open-<panel> ...); with an id runs one"),
            C(Ui, "mode", 0, "mode [author|explore]", "Mod Author or Explore mode"),
            C(Ui, "labels", 0, "labels [off|selected|nearby|all|on] | labels types <0..50|all>", "label level of the world overlay; types = component names shown per label (the selected object shows all)"),
            C(Ui, "scope", 0, "scope [selected|1|2|nearby|all]", "how far the overlay follows the selection's relations"),
            C(Ui, "pick", 0, "pick | pick on|off | pick filter <name> | pick status | pick <x> <y> [look]", "pick the object under a screen point (no arguments: under the boy); look = report only, the selection stays"),
            C(Ui, "gizmo", 0, "gizmo [on|off] | gizmo global on|off", "move handles of the selection"),
            C(Ui, "hud", 0, "hud [on|off]", "the editor's status line", true),
            C(Ui, "clean", 0, "clean [on|off]", "hides every editor drawing for a clean picture", true),
            C(Ui, "gamma", 0, "gamma <0..1>|reset", "the game's brightness setting", true),
            C(Ui, "seed", 0, "seed <n>", "seeds the game's random numbers", true),

            C(Boy, "tp", 1, "tp <x> <y> [z] | tp <obj>", "teleports the boy to a position (two numbers keep his depth) or to an object"),
            C(Boy, "cam", 0, "cam | cam status [json] | cam look <target> [dist] [yaw] [pitch] [fov] [seconds] [point=<name>] [size=<f>] | cam back | cam pose <x> <y> <z> <yaw> <pitch> [fov] [seconds] [roll] | cam fov <deg> | cam roll <deg> | cam json [name] | cam off", "the camera: describe it, aim it at an object, pose it, step back to the pose before, give it back"),
            C(Boy, "freecam", 0, "freecam [on|off]", "free camera"),
            C(Boy, "zoom", 0, "zoom [factor|reset]", "zoom of the game camera"),
            C(Boy, "cspawn", 0, "cspawn [list] | cspawn save [name] | cspawn go|respawn|del <n>", "your own spawn points"),

            C(Objects, "inspect", 0, "inspect <obj> [deep]", "components, fields, state machines and signal wires of an object"),
            C(Objects, "select", 0, "select <obj>", "selects an object in the editor"),
            C(Objects, "sel", 0, "sel | sel back|fwd|clear | sel <obj>", "the selection: show it, walk its history, clear it, or select an object"),
            C(Objects, "children", 0, "children <obj>", "direct children of an object"),
            C(Objects, "near", 0, "near [radius] [filter]", "objects around the boy, nearest first"),
            C(Objects, "triggers", 0, "triggers [radius] | triggers on|off", "triggers around the boy; on|off switches the trigger overlay"),
            C(Objects, "hiddennear", 0, "hiddennear [radius]", "inactive objects around the boy"),
            C(Objects, "hidden", 0, "hidden [status|on|off|save|reset] [json] | hidden scan [seconds]", "the passive scanner for hidden objects and its database; on / off persist hidden.passive", true),
            C(Objects, "db", 0, "db stats [json] | db find <query> | db rec <obj> | db selector <obj> | db resolve <selector> | db budget <ms> | db dirty", "the object database behind every search"),
            C(Objects, "sfind", 0, "sfind <words> [cat:animation|sounds|scripts|triggers|fsm|characters] [clutter] [path]", "ranked search as in the Inspector's find field"),
            C(Objects, "setactive", 2, "setactive <obj> on|off", "activates or deactivates an object (undoable)"),
            C(Objects, "enable", 3, "enable <obj> <Component> on|off", "enables or disables a component (undoable)"),
            C(Objects, "get", 3, "get <obj> <Component> <field>", "value of a field or property"),
            C(Objects, "set", 4, "set <obj> <Component> <field> <value...>", "writes a field or a readable property (undoable)"),
            C(Objects, "call", 3, "call <obj> <Component> <method>", "calls a method without parameters"),
            C(Objects, "anim", 1, "anim <obj> [rebind]", "legacy animation states of an object; rebind rebuilds their bindings"),
            C(Objects, "reparent", 2, "reparent <obj> <newParent|none|up|restore>", "moves an object in the hierarchy, keeps its world position"),
            C(Objects, "move", 4, "move <obj> <x> <y> <z>", "moves an object (undoable)"),
            C(Objects, "movehere", 1, "movehere <obj> [dx] [dz]", "moves an object next to the boy"),
            C(Objects, "resetpos", 1, "resetpos all|<obj>", "puts moved objects back"),
            C(Objects, "mats", 0, "mats <obj>", "materials and shader values of an object"),
            C(Objects, "origin", 0, "origin <obj> [deep]", "the object in the level file compared with the live one"),
            C(Objects, "refs", 0, "refs <obj> [all] | refs stats", "what an object refers to"),
            C(Objects, "refby", 0, "refby <obj> [all]", "what refers to an object"),
            C(Objects, "chain", 0, "chain <obj> [depth] [back]", "what an object leads to through references (back: what leads to it)"),
            C(Objects, "path", 2, "path <a> <b> [any]", "shortest reference path between two objects"),
            C(Objects, "paths", 2, "paths <a> <b>", "all reference paths between two objects"),
            C(Objects, "graph", 0, "graph <hops> [f|s|m|o|c|all] | graph look [mode] [hops] [filter] | graph mode <m> | graph live [s f a n|all|none]", "the relation graph around the selection (c = code edges); look = text only, the Graph panel is not changed"),
            C(Objects, "events", 0, "events [n] [filter] [json] | events on|off|auto|clear|status [json]", "the event monitor: signals, FSM changes, sound posts; on / off persist events.monitor, auto = only while in use"),
            C(Objects, "bookmark", 0, "bookmark add [note] | bookmark list | bookmark go <n> [tp] | bookmark del <n>", "bookmarks of objects"),

            C(Logic, "fsm", 2, "fsm <obj> [fsmName] event|state|var <name> [value (undoable)] | fsm <obj> wire <state|*> <event> <toState> | fsm <obj> unwire <state|*> <event> | fsm <obj> resetwires | fsm <obj> events", "drives or rewires a PlayMaker state machine"),
            C(Logic, "fsmgraph", 0, "fsmgraph <obj> [fsmName]", "dump of a state machine; opens it in the FSM Graph panel"),
            C(Logic, "fsmfind", 0, "fsmfind <text>", "search in every loaded state machine"),
            C(Logic, "fsmset", 0, "fsmset <fsm obj>|<state>|<action index>|<field>|<value>", "changes a setting of one action (field \"(enabled)\" = action on / off)"),
            C(Logic, "sigs", 0, "sigs <obj>", "signal inputs of the state machines on an object"),
            C(Logic, "sigwire", 0, "sigwire <fsm obj>|<signal in>|<sender obj>|<signal out>", "connects a signal output to a state machine input"),
            C(Logic, "sigunwire", 0, "sigunwire <fsm obj>|<signal in>|<sender obj>|<signal out>", "removes such a connection"),
            C(Logic, "signal", 2, "signal <obj> <signalOut>", "fires every connection of a signal output"),
            C(Logic, "signalin", 2, "signalin <obj> <signalIn>", "calls a signal input directly"),
            C(Logic, "logic", 0, "logic <Type|object> | logic shared [filter] | logic anim [filter]", "what the game code of a type reads, writes and calls"),
            C(Logic, "codedeps", 0, "codedeps [filter]", "which scripts depend on which"),
            C(Logic, "gamecode", 0, "gamecode", "state of the game code dump"),

            C(History, "history", 0, "history [n] [json]", "the last changes and the net changes"),
            C(History, "undo", 0, "undo [n] | undo to <id>", "takes back the last change, n changes, or every change back to entry #id"),
            C(History, "redo", 0, "redo [n]", "applies it again"),
            C(History, "revert", 0, "revert all [dry] | revert <n> [dry] | revert <label text> [dry]", "puts everything, net change n of history, or the net changes whose label holds the text back to the game's value; dry lists only"),
            C(History, "mod", 0, "mod list|reload [json] | mod record <name> [description] | mod show|enable|disable <name> [json] | mod dry|validate|delete <name>", "mods made from recorded changes"),

            C(Audio, "audio", 0, "audio rules | audio native [on|off] | audio mute|unmute|nuke|unnuke <event> | audio replace <event> <with> | audio force switch|state|rtpc <name> <value> | audio clear <key|all>", "rules that change what the game's sound engine gets"),
            C(Audio, "audiolog", 0, "audiolog [n]", "the last sound events the game posted"),
            C(Audio, "waits", 0, "waits", "state machines that wait for a music cue, beat or time"),
            C(Audio, "timed", 0, "timed [json]", "components with music-timed triggers and their times"),
            C(Audio, "timer", 3, "timer <Type> <field> <seconds|mute|unmute|fire>", "retimes, mutes or fires one music-timed trigger (undoable)"),
            C(Audio, "music", 0, "music", "music position, loop length, beat, bar"),
            C(Audio, "adb", 0, "adb [stats] [json] | adb export | adb exportstatus | adb find <text> | adb filter <F> [n] | adb show|select <name|id>", "the audio database: events, banks, where they are used"),
            C(Audio, "wwise", 0, "wwise [stats] [json] | wwise stacks [off|important|full] | wwise version | wwise stock | wwise api [filter] | wwise watched [filter] | wwise level <0-3> | wwise hash <name> | wwise pid <id> | wwise trace [n] [filter] | wwise objs [filter] | wwise banks | wwise ids", "the native trace of calls into the sound engine"),
            C(Audio, "sounds", 0, "sounds [filter] | sounds audition <event> | sounds show <event> | sounds override <event> <binding#> <newEvent> | sounds revert|post|trigger <event> <binding#>", "the sound library: who plays which event"),
            C(Audio, "acap", 0, "acap status [json] | acap preflight | acap start <name> | acap mark <ascii> | acap stop | acap result [json] | acap guard on|off | acap output audible|muted | acap suspend on|off", "capture of the game's audio output", true),

            C(Memory, "mem", 0, "mem [obj|on|off]", "memory addresses of an object's fields (for Cheat Engine)"),
            C(Memory, "memwhat", 0, "memwhat <hex>", "which object an address belongs to"),
            C(Memory, "memmethod", 0, "memmethod <hex>", "which method an address belongs to"),
            C(Memory, "memjit", 0, "memjit <Type> <method> [+hex]", "address of a compiled method"),
            C(Memory, "memread", 0, "memread <hex> [bytes]", "reads memory, at most 4096 bytes"),
            C(Memory, "memexport", 0, "memexport", "address table of the selection for Cheat Engine"),

            C(Record, "mcam", 0, null, "MultiCam: multi-camera captures and replays from a spec", true),
            C(Record, "srec", 0, null, "scene recorder and viewer", true),

            K("spawn", "spawn <sub> <sp>", "loads a savepoint through the game"),
            K("load", "load <sub> <sp>", "the same as spawn"),
            K("tpsp", "tpsp <sub> <sp>", "teleports to a savepoint without a load"),
            K("subs", "subs [filter]", "the areas (subscenes) with their savepoint counts"),
            K("levels", "levels [filter]", "the build's scene files and their streaming state"),
            K("discover", "discover all | discover <sub>", "loads areas once so their savepoints are known"),
            K("catalog", "catalog", "loads every area once and records its audio triggers"),
            K("post", "post <event>", "posts a Wwise event"),
            K("cue", "cue <name>", "injects a music cue or marker"),
            K("shockwave", "shockwave nokill|fire", "the shockwave: switch its kill off, or fire it now"),
            K("kill", "kill", "kills the boy"),
            K("god", "god [on|off]", "god mode"),
            K("ts", "ts <scale>", "time scale"),
            K("find", "find <name>", "search by name (fills the Inspector's list)"),
            K("respawn", "respawn", "respawn at the current savepoint"),
            K("reload", "reload", "reloads the scene"),
            K("solids", "solids [on|off]", "overlay of solid colliders"),
            K("savepoints", "savepoints [on|off]", "savepoint markers"),
            K("flag", "flag <name> [on|off]", "one of the game's debug flags"),
            K("flags", "flags", "all debug flags"),
            K("atlas", "atlas | atlas find <text> | atlas level <n> | atlas check", "the level atlas"),
            K("clear", "clear", "empties the console"),
        };

        static Cmd C(string group, string name, int min, string usage, string text) { return new Cmd { group = group, name = name, min = min, usage = usage, text = text }; }
        static Cmd C(string group, string name, int min, string usage, string text, bool forwarded) { return new Cmd { group = group, name = name, min = min, usage = usage, text = text, forwarded = forwarded }; }
        static Cmd K(string name, string usage, string text) { return new Cmd { group = Console, name = name, usage = usage, text = text, console = true }; }

        static Dictionary<string, Cmd> byName;

        public static Cmd Find(string name)
        {
            if (byName == null)
            {
                var d = new Dictionary<string, Cmd>(StringComparer.Ordinal);
                foreach (var c in table) d[c.name] = c;
                byName = d;
            }
            Cmd r;
            return byName.TryGetValue(name, out r) ? r : null;
        }

        public static IList<Cmd> All { get { return table; } }

        public static string Usage(Cmd c)
        {
            if (c.usage != null) return c.usage;
            if (c.name == "mcam") return MultiCam.Help;
            if (c.name == "srec") return SceneCommands.Usage;
            return c.name;
        }

        // An extension can describe its own commands: a public string property Help on its extension class
        // ("word <usage>: text", one command per line). Without it the extension is listed by its name.
        static readonly Dictionary<Type, PropertyInfo> helpProps = new Dictionary<Type, PropertyInfo>();
        static string ExtensionHelp(IInsideDevExtension x)
        {
            try
            {
                var t = x.GetType();
                PropertyInfo p;
                if (!helpProps.TryGetValue(t, out p))
                {
                    p = t.GetProperty("Help", BindingFlags.Instance | BindingFlags.Public);
                    if (p != null && (p.PropertyType != typeof(string) || !p.CanRead)) p = null;
                    helpProps[t] = p;
                }
                if (p != null) return p.GetValue(x, null) as string;
            }
            catch { }
            return null;
        }

        public static string Help(List<string> a)
        {
            if (a.Count > 1 && a[1] != "all") return One(a[1].ToLowerInvariant());
            var sb = new StringBuilder();
            int n = 0;
            foreach (var c in table) if (!c.console) n++;
            sb.Append("remote: ").Append(n).Append(" commands and ").Append(table.Length - n).Append(" console words. help <command> = usage and text of one.\n");
            sb.Append("        <obj> = name (nearest match), full/path, or #instanceID.  Quote names with spaces: \"My Obj\".\n");
            sb.Append("        json as the last word of state / where / doctor / cfg / background / timed / perf / events / history / mod / ext / db stats / adb stats /\n");
            sb.Append("        wwise stats / hidden / diag / cams / postfx / ui docks / cam status / focustest status gives one JSON line.\n");
            string group = null;
            int col = 0;
            foreach (var c in table)
            {
                if (c.group != group)
                {
                    group = c.group; col = 0;
                    sb.Append('\n').Append(group == Console ? "console words (the same through the bridge)" : group).Append(":\n        ");
                }
                string u = c.usage ?? c.name + " ... (help " + c.name + ")";
                // the long ones (ui, wwise, fsm ...) are listed by name here and in full by help <command>
                if (u.Length > 110) u = c.name + " ... (help " + c.name + ")";
                if (col > 0 && col + u.Length > 150) { sb.Append("\n        "); col = 0; }
                else if (col > 0) { sb.Append(" | "); col += 3; }
                sb.Append(u); col += u.Length;
            }
            sb.Append('\n');
            if (Extensions.loaded.Count > 0)
            {
                sb.Append("\nextensions:\n");
                foreach (var x in Extensions.loaded)
                {
                    string h = ExtensionHelp(x);
                    if (!string.IsNullOrEmpty(h)) sb.Append("        ").Append(h.Trim().Replace("\n", "\n        ")).Append('\n');
                    else { string nm; try { nm = x.Name; } catch { nm = x.GetType().Name; } sb.Append("        ").Append(nm).Append('\n'); }
                }
            }
            return sb.ToString();
        }

        static string One(string name)
        {
            var c = Find(name);
            if (c == null)
            {
                var near = new List<string>();
                foreach (var x in table) if (x.name.IndexOf(name, StringComparison.Ordinal) >= 0 || (x.usage != null && x.usage.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)) near.Add(x.name);
                return "unknown command '" + name + "'" + (near.Count > 0 ? "; it appears in: " + string.Join(", ", near.ToArray()) : "") +
                       (Extensions.loaded.Count > 0 ? "\n  (words of an extension answer their own help, for example: " + name + " help)" : "");
            }
            var sb = new StringBuilder();
            sb.Append(Usage(c).Replace(" | ", "\n")).Append('\n');
            sb.Append("  ").Append(c.text);
            if (c.console) sb.Append("  (a word of the in-game console; the bridge passes it on and returns what it logged)");
            if (c.min > 0) sb.Append("  (needs at least ").Append(c.min).Append(c.min == 1 ? " argument)" : " arguments)");
            return sb.ToString();
        }
    }
}
