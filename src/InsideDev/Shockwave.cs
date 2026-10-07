using System;
using System.Reflection;
using UnityEngine;

namespace InsideDev
{
    // The mines "shockwave" (ForcePushManager) is timed off the MUSIC PLAY POSITION, not cue markers:
    //   blowChecker   (MusicEventChecker @ 5.9s)          -> InitBlow(): rumble, kill timer if outside cover in a deadly zone
    //   warningChecker(@ musicCycleTime - explosionTime + offset) -> StartWall(): wave visual starts moving in
    //   resetChecker  (@ 2.5s)                             -> ResetWall()
    // This helper exposes the live timing and lets you disable the kill, freeze it, retime it or fire it manually.
    public static class Shockwave
    {
        public static bool noKill;
        static string blowTxt = "", explTxt = "";
        static readonly BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static FieldInfo fKillEnabled, fKillTimer, fBlow, fWarn, fReset, fExploding;
        static MethodInfo mInitBlow, mStartWall, mResetWall;

        public static ForcePushManager Mgr { get { try { return LevelGlobals.forcePushManager; } catch { return null; } } }

        static void Bind()
        {
            if (fKillEnabled != null) return;
            var t = typeof(ForcePushManager);
            fKillEnabled = t.GetField("killEnabled", BF);
            fKillTimer = t.GetField("killTimer", BF);
            fBlow = t.GetField("blowChecker", BF);
            fWarn = t.GetField("warningChecker", BF);
            fReset = t.GetField("resetChecker", BF);
            fExploding = t.GetField("exploding", BF);
            mInitBlow = t.GetMethod("InitBlow", BF);
            mStartWall = t.GetMethod("StartWall", BF);
            mResetWall = t.GetMethod("ResetWall", BF);
        }

        // called from LateUpdate: ForcePushManager.Update runs InitBlow() before re-enabling kill in the same frame,
        // so clearing killEnabled after its Update means the next blow never arms the kill timer.
        public static void LateUpdate()
        {
            if (!noKill) return;
            var m = Mgr;
            if (m == null) return;
            Bind();
            try
            {
                fKillEnabled.SetValue(m, false);
                if ((float)fKillTimer.GetValue(m) > 0f) fKillTimer.SetValue(m, 0f);
            }
            catch { }
        }

        public static void FireNow()
        {
            var m = Mgr; if (m == null) return; Bind();
            try { mStartWall.Invoke(m, null); mInitBlow.Invoke(m, null); DevLog.Write("shockwave: fired manually"); } catch (Exception e) { DevLog.Error("shockwave fire", e); }
        }

        public static void ResetNow()
        {
            var m = Mgr; if (m == null) return; Bind();
            try { mResetWall.Invoke(m, null); } catch (Exception e) { DevLog.Error("shockwave reset", e); }
        }

        public static float CheckerTime(string which)
        {
            var m = Mgr; if (m == null) return -1; Bind();
            var f = which == "blow" ? fBlow : which == "warn" ? fWarn : fReset;
            var c = f.GetValue(m) as MusicEventChecker;
            return c != null ? c.eventTime_s : -1;
        }

        public static void SetCheckerTime(string which, float t)
        {
            var m = Mgr; if (m == null) return; Bind();
            var f = which == "blow" ? fBlow : which == "warn" ? fWarn : fReset;
            var c = f.GetValue(m) as MusicEventChecker;
            if (c != null) c.SetEventTime(t);
        }

        public static void Draw(UI ui)
        {
            var m = Mgr;
            if (m == null)
            {
                ui.Label("No shockwave manager in the loaded scenes. It exists in the Mines shockwave section (#forcePush... areas) - spawn there and this panel comes alive.", UI.Dim);
                return;
            }
            Bind();
            MusicManager music = null;
            try { music = PersistentBehaviour<GlobalAudio>.instance.music; } catch { }
            float pos = music != null ? music.GetMusicPosition_s() : -1f;
            float cycle = m.musicCycleTime;
            float blowAt = CheckerTime("blow"), warnAt = CheckerTime("warn"), resetAt = CheckerTime("reset");
            float t = cycle > 0 ? Mathf.Repeat(pos, cycle) : pos;
            float toBlow = cycle > 0 ? Mathf.Repeat(blowAt - t, cycle) : 0f;
            bool exploding = false; try { exploding = (bool)fExploding.GetValue(m); } catch { }
            bool killEn = false; try { killEn = (bool)fKillEnabled.GetValue(m); } catch { }

            ui.Label("SHOCKWAVE (ForcePushManager on '" + m.gameObject.name + "')  - timed by the music play position, not by cue markers", new Color(1f, 0.85f, 0.3f, 1f));
            ui.Label(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "music {0:0.00}s / cycle {1:0.00}s   ->   next BLOW in {2:0.00}s      wave moving: {3}   in cover: {4}   deadly zones active: {5}   kill armed: {6}",
                t, cycle, toBlow, exploding ? "yes" : "no", ForcePushManager.GetInCover() ? "YES" : "no", ForcePushManager.getActiveZoneCount(), killEn ? "yes" : "no"));
            // countdown bar
            ui.BeginRow();
            ui.Label("cycle", UI.Dim, 40);
            ui.Slider("sw_bar", t, 0f, Mathf.Max(0.01f, cycle), ui.Width - 10);
            ui.EndRow();

            ui.BeginRow();
            noKill = ui.Toggle(noKill, "Disable shockwave KILL (wave + rumble still play)");
            bool en = ui.Toggle(m.enabled, "manager enabled (off = no waves at all)");
            if (en != m.enabled) { Changes.Record(m.gameObject, m, "ForcePushManager", m.enabled); m.enabled = en; }
            ui.EndRow();
            ui.BeginRow();
            if (ui.Button("Fire shockwave now")) FireNow();
            if (ui.Button("Reset wall")) ResetNow();
            if (ui.Button("Select in inspector")) { if (DevCore.Instance != null) DevCore.Instance.SelectInInspector(m.gameObject); }
            ui.EndRow();

            ui.Label(string.Format(System.Globalization.CultureInfo.InvariantCulture, "timing (seconds into the music loop):  blow {0:0.00}   wall starts {1:0.00}   reset {2:0.00}   explosionTime {3:0.00}", blowAt, warnAt, resetAt, m.explosionTime), UI.Dim);
            ui.BeginRow();
            ui.Label("exact blow at (s):", null, 120);
            bool e1 = ui.TextField("sw_blow_txt", ref blowTxt, 80);
            ui.Label("wave travel (s):", null, 110);
            bool e2 = ui.TextField("sw_expl_txt", ref explTxt, 80);
            if (ui.Button("apply", 60) || e1 || e2)
            {
                float vb, ve;
                if (float.TryParse(explTxt, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ve) && ve > 0f) m.explosionTime = ve;
                if (float.TryParse(blowTxt, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out vb))
                { SetCheckerTime("blow", vb); SetCheckerTime("warn", Mathf.Repeat(vb + 0.1f - m.explosionTime + m.offset, cycle)); }
                DevLog.Write("shockwave: blow @" + CheckerTime("blow") + "s, travel " + m.explosionTime + "s");
                blowTxt = explTxt = "";
            }
            ui.EndRow();
            if (blowTxt.Length == 0 && !ui.FocusIs("sw_blow_txt")) blowTxt = blowAt.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            if (explTxt.Length == 0 && !ui.FocusIs("sw_expl_txt")) explTxt = m.explosionTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            ui.BeginRow();
            ui.Label("blow at", null, 60);
            float nb = ui.Slider("sw_blow", blowAt, 0f, Mathf.Max(0.1f, cycle), 260);
            if (Mathf.Abs(nb - blowAt) > 0.01f) { SetCheckerTime("blow", nb); SetCheckerTime("warn", Mathf.Repeat(nb + 0.1f - m.explosionTime + m.offset, cycle)); }
            ui.Label("  wave travel time", null, 120);
            float ne = ui.Slider("sw_expl", m.explosionTime, 0.1f, 3f, 200);
            if (Mathf.Abs(ne - m.explosionTime) > 0.01f) { m.explosionTime = ne; SetCheckerTime("warn", Mathf.Repeat(CheckerTime("blow") + 0.1f - ne + m.offset, cycle)); }
            ui.EndRow();
            ui.Label("Note: moving 'blow at' away from the music hit desyncs the kill from what you hear - that's the point of Playdead's design.", UI.Dim);
        }
    }
}
