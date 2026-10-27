using System;
using System.Collections.Generic;

namespace InsideDev.Play
{
    // PerceptionKinds.cs (owner OBSERVE): the Unity-free tables behind observe's device and killer rows, compiled into
    // the offline tests too (qa/mcp/OBSERVE). Classes are matched by name, so the tables also cover the Device
    // subclasses that PerceptionScan finds through one FindObjectsOfType(Device) (decompiled AC/Device.cs and its 33
    // subclasses; placed counts from extract/index/script_usage.tsv).
    //   DeviceKinds: game class -> observe kind and interact verbs. Secrets (SecretPod*, SecretJoystick, SecretWaterGate,
    //   SecretWirePull, Secret, SecretSuitcase) are never listed: their game logic reaches the achievement progression.
    //   WagonCrush is a hazard (crusher), FenceField only limits the stick (BoySetInput), MindHatTakeOff is not placed.
    //   KillerKinds: the extra killer classes (Dart, ChaserGuardCargoHall, CapsuleRunOverLogic, GateAvoid,
    //   WaterFlushGateKill, ZombieMonitorCameraGameplay, ForcePushMovingCover, BoyExplosion, FarmPigFight) with their
    //   threat states (lowered with StateNames.Lower), and the PlayMaker kill actions. PD_KillBoy and
    //   RopeGunShootAndKillMainCharacter kill; KillBoyWithVoiceEmotion only sets the death voice of a death caused
    //   elsewhere (BoyAudio.KillBoyWithEmotion), so FSMs that have only that one are not killers.
    //   OpenElevatorDoors / OpenElevatorDoors2 call Boy.Kill only on a boy that is already dead: lift doors are devices.
    //   ObjectPick: which rows survive the cut to max_objects (actionable first, each group nearest first).
    internal static class DeviceKinds
    {
        public const string Lever = "lever", Hatch = "hatch", Boards = "boards", Door = "door", Device = "device", LiftDoor = "lift_door";

        static readonly Dictionary<string, string> kinds = new Dictionary<string, string>
        {
            { "Lever", Lever }, { "LeverAnalog", Lever }, { "LeverAnalogWater", Lever }, { "LeverOneTime", Lever }, { "LeverElevatorStuck", Lever },
            { "SuctionLever", Lever }, { "SwitchTallRod", Lever }, { "ChickenGunSwitch", Lever },
            { "Hatch", Hatch }, { "VentHatch", Hatch }, { "UnderwaterHatch", Hatch },
            { "PullOffBoards", Boards }, { "BoardBreak", Boards }, { "DoorBoarded", Boards }, { "AlbinoMultiPullBoards", Boards },
            { "Door", Door },
            { "MinesGate", Device }, { "SuperPillar", Device }, { "WindowInteractive", Device }, { "TorchPickup", Device }, { "UnderwaterCoil", Device },
            { "UnderwaterLock", Device }, { "RopeLadder", Device }, { "SuperLadder", Device }, { "WaterStreamGrabHook", Device },
            { "HuddleOvenGrab", Device }, { "PushCoverLogic", Device },
            { "OpenElevatorDoors", LiftDoor }, { "OpenElevatorDoors2", LiftDoor }
        };

        static readonly string[] pushTypes = { "MinesGate", "SuperPillar" };
        static readonly string[] grabOnlyTypes = { "RopeLadder", "WaterStreamGrabHook" };
        static readonly string[] huddleTypes = { "HuddleOvenGrab", "PushCoverLogic" };
        static readonly string[] followerTypes = { "AlbinoMultiPullBoards", "OpenElevatorDoors2" };

        // the extra component classes PerceptionScan scans besides Device (non-Device classes of the table)
        public static readonly string[] ScanTypes = { "UnderwaterHatch", "AlbinoMultiPullBoards", "WaterStreamGrabHook", "HuddleOvenGrab", "PushCoverLogic", "OpenElevatorDoors", "OpenElevatorDoors2" };

        // observe kind of a device class, null = not listed
        public static string KindOf(string typeName)
        {
            if (string.IsNullOrEmpty(typeName) || IsSecret(typeName)) return null;
            string k;
            return kinds.TryGetValue(typeName, out k) ? k : null;
        }

        public static bool IsSecret(string typeName)
        {
            return typeName != null && (typeName == "Secret" || typeName.StartsWith("SecretPod", StringComparison.Ordinal) || typeName == "SecretJoystick" ||
                typeName == "SecretWaterGate" || typeName == "SecretWirePull" || typeName == "SecretSuitcase");
        }

        public static bool NeedsFollowers(string typeName) { return Array.IndexOf(followerTypes, typeName) >= 0; }

        // interact verbs of a device for the controlled body kind (boy, dude, huddle, submarine)
        public static List<string> Verbs(string typeName, string bodyKind)
        {
            var l = new List<string>();
            string kind = KindOf(typeName);
            if (kind == null) return l;
            bool huddleDevice = Array.IndexOf(huddleTypes, typeName) >= 0;
            if (bodyKind == "huddle")
            {
                if (huddleDevice) { l.Add("grab"); l.Add("push"); l.Add("pull"); }
                return l;
            }
            if (bodyKind != "boy" && bodyKind != "dude") return l;
            if (huddleDevice) return l;
            switch (kind)
            {
                case Lever: l.Add("lever"); break;
                case LiftDoor: l.Add("lift"); break;
                case Door: break;
                default:
                    l.Add("grab");
                    if (Array.IndexOf(grabOnlyTypes, typeName) >= 0) break;
                    l.Add(Array.IndexOf(pushTypes, typeName) >= 0 ? "push" : "pull");
                    break;
            }
            return l;
        }
    }

    internal static class KillerKinds
    {
        sealed class Row
        {
            public string kind, stateField;
            public string[] threats;
            public bool musicTimed, positional;
        }

        static readonly Dictionary<string, Row> rows = new Dictionary<string, Row>
        {
            { "Dart", new Row { kind = "sentry", stateField = "state", threats = new[] { "prepare", "shoot", "kill", "tranquilise" } } },
            { "ChaserGuardCargoHall", new Row { kind = "guard", stateField = "guardMode", threats = new[] { "chasing_boy", "strangling_boy" } } },
            { "CapsuleRunOverLogic", new Row { kind = "crusher", stateField = "state", threats = new[] { "ground", "ragdoll", "crush" } } },
            { "GateAvoid", new Row { kind = "crusher", stateField = "state", threats = new[] { "close", "crush" } } },
            { "WaterFlushGateKill", new Row { kind = "kill_zone", positional = true, threats = new string[0] } },
            { "ZombieMonitorCameraGameplay", new Row { kind = "sentry", stateField = "state", threats = new[] { "kill_boy" }, musicTimed = true } },
            { "ForcePushMovingCover", new Row { kind = "crusher", stateField = "state", threats = new[] { "blow" }, musicTimed = true } },
            { "BoyExplosion", new Row { kind = "kill_zone", stateField = "state", threats = new[] { "warm_up", "explode", "warm_up_headless" } } },
            { "FarmPigFight", new Row { kind = "other", stateField = "state", threats = new[] { "attack", "charge", "idle_angry" } } }
        };

        public static readonly string[] Types = { "Dart", "ChaserGuardCargoHall", "CapsuleRunOverLogic", "GateAvoid", "WaterFlushGateKill", "ZombieMonitorCameraGameplay",
            "ForcePushMovingCover", "BoyExplosion", "FarmPigFight" };

        public static readonly string[] KillActions = { "PD_KillBoy", "RopeGunShootAndKillMainCharacter" };

        public static bool Known(string typeName) { return typeName != null && rows.ContainsKey(typeName); }

        public static string KindOf(string typeName) { Row r; return typeName != null && rows.TryGetValue(typeName, out r) ? r.kind : null; }

        public static string StateField(string typeName) { Row r; return typeName != null && rows.TryGetValue(typeName, out r) ? r.stateField : null; }

        public static bool MusicTimed(string typeName) { Row r; return typeName != null && rows.TryGetValue(typeName, out r) && r.musicTimed; }

        // a positional killer (no state of its own): a threat when the body is inside or within 1 m of it
        public static bool Positional(string typeName) { Row r; return typeName != null && rows.TryGetValue(typeName, out r) && r.positional; }

        // lowered state (StateNames.Lower) -> dangerous now
        public static bool Threat(string typeName, string state)
        {
            Row r;
            if (typeName == null || state == null || !rows.TryGetValue(typeName, out r)) return false;
            return Array.IndexOf(r.threats, state) >= 0;
        }

        public static bool IsKillAction(string actionTypeName) { return actionTypeName != null && Array.IndexOf(KillActions, actionTypeName) >= 0; }

        // hazard kind of a kill FSM from its GameObject name
        public static string FsmKind(string goName)
        {
            string n = (goName ?? "").ToLowerInvariant();
            if (n.Contains("spotlight")) return "spotlight";
            if (n.Contains("rope") || n.Contains("shoot")) return "rope_gun";
            if (n.Contains("guard") || n.Contains("man")) return "guard";
            return "kill_zone";
        }
    }

    internal static class ObjectPick
    {
        // rows sorted nearest first, cut to limit: the actionable rows first (nearest first), then the others; the kept
        // indices come back in the original (nearest first) order
        public static List<int> Keep(IList<bool> actionable, int limit)
        {
            var keep = new List<int>();
            int n = actionable != null ? actionable.Count : 0;
            if (limit < 0 || n <= limit) { for (int i = 0; i < n; i++) keep.Add(i); return keep; }
            for (int i = 0; i < n && keep.Count < limit; i++) if (actionable[i]) keep.Add(i);
            for (int i = 0; i < n && keep.Count < limit; i++) if (!actionable[i]) keep.Add(i);
            keep.Sort();
            return keep;
        }
    }
}
