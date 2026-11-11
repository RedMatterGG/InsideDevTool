using System;
using System.Collections.Generic;
using UnityEngine;

namespace InsideDev.Play
{
    // PpcXObserve.cs (owner OBSERVE): what each client was shown last, and observe detail delta.
    //   View = the state "since the previous observe" needs: the events cursor, the no-progress counter and the rows
    //   of the previous observation. One view per MCP session id. A call without a session (the concise observation
    //   inside action results built through Observer.Concise(tool)) uses the view that was used last, and a new view
    //   starts as a copy of that one, so a single client, and a client that opens a session per call, see exactly one
    //   continuous state; two clients that both call observe each keep their own.
    //   Delta: the rows of this observation are compared with the rows the view was shown before. Listed: rows that
    //   are new, whose state / actions changed (objects), whose state / threat / cover changed (hazards; digits in a
    //   state text such as a cycle time do not count), objects that moved more than 0.5 m, and every hazard that is a
    //   threat now. gone = ids shown before that are not in the lists now. The base is replaced by every non-delta
    //   observation of the view (what the budget cut is not part of it) and updated by every delta. After a load, or
    //   without a base, a delta is a complete concise list with delta.full = true.
    public static partial class Observer
    {
        struct Shown
        {
            public string sig;
            public Vector3 pos;
            public bool hazard;
        }

        sealed class View
        {
            public long eventsCursor, stuckMark, used;
            public readonly StuckCounter stuck = new StuckCounter();
            public readonly HashSet<string> stuckSeen = new HashSet<string>();
            public string stuckCheckpoint;
            public bool hasBase;
            public int baseEpoch = -1, baseFrame;
            public long baseStep;
            public readonly Dictionary<string, Shown> shown = new Dictionary<string, Shown>();

            public void CopyFrom(View o)
            {
                eventsCursor = o.eventsCursor;
                stuckMark = o.stuckMark;
                stuck.CopyFrom(o.stuck);
                foreach (var s in o.stuckSeen) stuckSeen.Add(s);
                stuckCheckpoint = o.stuckCheckpoint;
                hasBase = o.hasBase;
                baseEpoch = o.baseEpoch;
                baseFrame = o.baseFrame;
                baseStep = o.baseStep;
                foreach (var kv in o.shown) shown[kv.Key] = kv.Value;
            }
        }

        const int MaxViews = 16;
        const float MovedSqr = 0.25f;
        static readonly Dictionary<string, View> views = new Dictionary<string, View>();
        static View lastView;
        static long viewClock;

        // main thread
        static View ViewFor(string session)
        {
            View v;
            string key = session ?? "";
            if (key.Length == 0 && lastView != null) v = lastView;
            else if (!views.TryGetValue(key, out v))
            {
                v = new View();
                if (lastView != null) v.CopyFrom(lastView);
                if (views.Count >= MaxViews) DropOldestView();
                views[key] = v;
            }
            v.used = ++viewClock;
            lastView = v;
            return v;
        }

        static void DropOldestView()
        {
            string oldest = null;
            long at = long.MaxValue;
            foreach (var kv in views)
                if (!ReferenceEquals(kv.Value, lastView) && kv.Value.used < at) { at = kv.Value.used; oldest = kv.Key; }
            if (oldest != null) views.Remove(oldest);
        }

        static string ObjSig(ObjInfo x)
        {
            return x.kind + "|" + (x.state ?? "") + "|" + string.Join(",", x.actions.ToArray());
        }

        static string HazSig(HazardInfo x)
        {
            return x.kind + "|" + (x.threat ? "T" : "-") + (x.safe ? "S" : "-") + "|" + NoDigits(x.state);
        }

        // a state text without its numbers ("cycle 3.1/6 s" and "cycle 3.4/6 s" are the same state)
        internal static string NoDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) if (!(c >= '0' && c <= '9') && c != '.' && c != '-') sb.Append(c);
            return sb.ToString();
        }

        // turns the complete row lists into the delta lists; returns the "delta" block, gone = the "gone" block
        static Json.Obj DeltaRows(View v, Latest lt, List<object> objs, List<object> hazs, out Json.Obj gone)
        {
            gone = null;
            var info = new Json.Obj();
            if (!v.hasBase || v.baseEpoch != ids.Epoch)
            {
                info["full"] = true;
                info["reason"] = v.hasBase ? "load" : "no_baseline";
                return info;
            }
            info["full"] = false;
            info["base_step"] = (double)v.baseStep;
            info["base_frame"] = (double)v.baseFrame;
            var seen = new HashSet<string>();
            var keepO = new List<object>();
            int sameO = 0, sameH = 0;
            for (int i = 0; i < lt.objects.Count && i < objs.Count; i++)
            {
                var x = lt.objects[i];
                if (x.id == null) continue;
                seen.Add(x.id);
                Shown was;
                string change = null;
                if (!v.shown.TryGetValue(x.id, out was) || was.hazard) change = "new";
                else if (was.sig != ObjSig(x)) change = "changed";
                else if ((x.pos - was.pos).sqrMagnitude > MovedSqr) change = "moved";
                if (change == null) { sameO++; continue; }
                var jo = objs[i] as Json.Obj;
                if (jo != null) { jo["change"] = change; keepO.Add(jo); }
            }
            var keepH = new List<object>();
            for (int i = 0; i < lt.hazards.Count && i < hazs.Count; i++)
            {
                var x = lt.hazards[i];
                if (x.id == null) continue;
                seen.Add(x.id);
                Shown was;
                string change = null;
                if (!v.shown.TryGetValue(x.id, out was) || !was.hazard) change = "new";
                else if (was.sig != HazSig(x)) change = "changed";
                else if (x.threat) change = "threat";
                if (change == null) { sameH++; continue; }
                var jo = hazs[i] as Json.Obj;
                if (jo != null) { jo["change"] = change; keepH.Add(jo); }
            }
            var goneO = new List<object>();
            var goneH = new List<object>();
            foreach (var kv in v.shown)
                if (!seen.Contains(kv.Key)) (kv.Value.hazard ? goneH : goneO).Add(kv.Key);
            objs.Clear();
            objs.AddRange(keepO);
            hazs.Clear();
            hazs.AddRange(keepH);
            gone = new Json.Obj();
            gone["objects"] = goneO;
            gone["hazards"] = goneH;
            info["same_objects"] = (double)sameO;
            info["same_hazards"] = (double)sameH;
            return info;
        }

        // the view's base becomes what this observation told the client (r after the budget cut)
        static void Remember(View v, Json.Obj r, Latest lt, bool partial)
        {
            if (!partial || v.baseEpoch != ids.Epoch) v.shown.Clear();
            else
            {
                var g = r["gone"] as Json.Obj;
                if (g != null)
                    foreach (var key in new[] { "objects", "hazards" })
                    {
                        var l = g[key] as List<object>;
                        if (l != null) foreach (var x in l) { var id = x as string; if (id != null) v.shown.Remove(id); }
                    }
            }
            var sent = new HashSet<string>();
            foreach (var key in new[] { "objects", "hazards" })
            {
                var l = r[key] as List<object>;
                if (l == null) continue;
                foreach (var x in l) { var jo = x as Json.Obj; string id = jo != null ? jo.Str("id", null) : null; if (id != null) sent.Add(id); }
            }
            foreach (var x in lt.objects) if (x.id != null && sent.Contains(x.id)) v.shown[x.id] = new Shown { sig = ObjSig(x), pos = x.pos, hazard = false };
            foreach (var x in lt.hazards) if (x.id != null && sent.Contains(x.id)) v.shown[x.id] = new Shown { sig = HazSig(x), pos = x.pos, hazard = true };
            v.hasBase = true;
            v.baseEpoch = ids.Epoch;
            v.baseStep = LogicManager.stepCount;
            v.baseFrame = Time.frameCount;
        }
    }
}
