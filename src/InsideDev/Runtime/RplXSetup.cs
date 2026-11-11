using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace InsideDev
{
    // One-time registration of the srec viewer and acap with the cfg and doctor commands: the config key they read and
    // one quick, read-only check each. Called from the first srec / acap command; a call at start-up (DevCore) makes the
    // key and the checks visible before that.
    internal static class RplXSetup
    {
        static bool done;

        // main thread
        public static void Register()
        {
            if (done) return;
            done = true;
            try
            {
                EditorState.Describe("acap", AudioCapture.OutputKey, AudioCapture.OutputAudible,
                    "sound output of an audio capture: audible (the game sound plays, nothing is forced outside a capture window) or muted (InsideMuter heartbeat, master volume forced 0 outside the window); set with 'acap output'");
                Doctor.Register("acap", AudioCapture.DoctorRows);
                Doctor.Register("srec viewer", SceneCommands.DoctorRows);
                SceneRecorder.RegisterDoctor();
            }
            catch (Exception e) { DevLog.Error("srec / acap registration", e); }
        }

        static bool keepAliveDone;

        // main thread. The MCP server (InsideDev.Mcp.dll, optional) must not run its automatic restores while an srec
        // recording or an srec replay runs. InsideDev.dll cannot reference that module, so its McpKeepAlive is found by name
        // once it is loaded; without it nothing is registered and the next srec command looks again.
        public static void KeepAlive()
        {
            if (keepAliveDone) return;
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("InsideDev.McpExt.McpKeepAlive", false); } catch { t = null; }
                if (t != null) break;
            }
            if (t == null) return;
            keepAliveDone = true;
            try
            {
                var m = t.GetMethod("Register", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Func<string>) }, null);
                if (m == null) { DevLog.Write("[srec] McpKeepAlive.Register(string, Func<string>) not found: MCP restores do not wait for srec"); return; }
                m.Invoke(null, new object[] { "srec", new Func<string>(SceneCommands.RecordBusy) });
                m.Invoke(null, new object[] { "replay", new Func<string>(SceneCommands.ReplayBusy) });
            }
            catch (Exception e) { DevLog.Error("srec keep-alive registration", e); }
        }
    }
}
