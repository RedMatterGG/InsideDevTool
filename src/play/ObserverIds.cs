using System;
using System.Collections.Generic;
using System.Globalization;
using InsideDev.McpExt;
using UnityEngine;
using UObj = UnityEngine.Object;

namespace InsideDev.Play
{
    // ObserverIds.cs (owner OBSERVE): ids, target resolution and screen marks. An id maps to the GameObject (a
    // component resolves to its GameObject); the key is (instance id, prefix, sub) so one object can be o12 as a box,
    // o13 as the ledge on its left edge and h4 as a hazard. Liveness is Unity's null check. Name and path lookups use
    // the core ObjectDatabase index (built synchronously once if it never ran; exact paths also try GameObject.Find).
    // An id outlives a load, a respawn and a subscene that streamed out and in again: the table keeps the hierarchy
    // path of every id, a new object at the path of a retired id gets that id back when the core index (a pass begun
    // after the load) shows exactly that object there, and an id used before then is resolved through the same path
    // (one synchronous index pass at most twice). Same-named siblings make a path ambiguous: such ids are not carried
    // over. An id whose object is gone answers stale_id with a "gone" message.
    public static partial class Observer
    {
        static readonly IdTable ids = new IdTable(o => { var u = o as UObj; return u != null; });

        internal sealed class Latest
        {
            public int epoch, frame;
            public List<ObjInfo> objects = new List<ObjInfo>();
            public List<HazardInfo> hazards = new List<HazardInfo>();
            public string bodyId;
            public Vector3 bodyPos;
            public bool hasBody;
        }

        static volatile Latest latest;

        static int passAtLoad;

        internal static string IdFor(UObj o, string prefix, int sub)
        {
            if (o == null) return null;
            var comp = o as Component;
            GameObject go = comp != null ? comp.gameObject : o as GameObject;
            UObj key = go != null ? (UObj)go : o;
            if (prefix != "h" && prefix != "b") prefix = "o";
            long k = IdTable.Key(key.GetInstanceID(), IdTable.PrefixCode(prefix), sub);
            string have = ids.Existing(k, key);
            if (have != null) return have;
            return go != null ? AssignNew(k, go, prefix) : ids.Assign(k, key, prefix);
        }

        // a new entry (its own method: the two delegates are only allocated here, not on every IdFor)
        static string AssignNew(long k, GameObject go, string prefix)
        {
            return ids.Assign(k, go, prefix, () => PlayShared.PathOf(go.transform), path => SoleAtPath(path, false) == go);
        }

        // the id an object already has (o, then h, then b; sub 0), or null
        internal static string ExistingId(GameObject go)
        {
            if (go == null) return null;
            int iid = go.GetInstanceID();
            for (int p = 0; p < 3; p++)
            {
                string s = ids.Existing(IdTable.Key(iid, p, 0), go);
                if (s != null) return s;
            }
            return null;
        }

        // the id an object already has under one prefix (sub 0), or null
        internal static string ExistingId(GameObject go, string prefix)
        {
            if (go == null) return null;
            return ids.Existing(IdTable.Key(go.GetInstanceID(), IdTable.PrefixCode(prefix), 0), go);
        }

        // the one loaded object at a hierarchy path by the core object index; null when there is none or several, and
        // when the index has no finished pass that began after the last load. refresh = run that pass now (a long
        // frame; only for an id the caller is about to act on).
        static GameObject SoleAtPath(string path, bool refresh)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int synced = 0;
            for (int n = 0; n < 4; n++)
            {
                bool current = ObjectDatabase.Ready && !ObjectDatabase.Scanning && ObjectDatabase.pass > passAtLoad;
                if (current)
                {
                    var l = ObjectDatabase.ByPath(path);
                    if (l.Count == 1) return l[0].go;
                    if (l.Count > 1 || !refresh || synced > 0 || ObjectDatabase.Age < 0.25f) return null;
                }
                else if (!refresh || synced >= 2) return null;
                if (current) ObjectDatabase.MarkDirty("play: id lookup");
                ObjectDatabase.EnsureFresh(0f);
                synced++;
            }
            return null;
        }

        static UObj ResolveId(string id)
        {
            object obj;
            string path;
            long key;
            int entryEpoch;
            switch (ids.Lookup(id, out obj, out path, out key, out entryEpoch))
            {
                case IdLookup.Ok: return (UObj)obj;
                case IdLookup.Gone:
                {
                    var kept = obj as UObj;
                    if (kept != null && ids.Rebind(id, key, kept)) return kept;
                    bool beforeLoad = entryEpoch < ids.Epoch;
                    bool loading = false;
                    try { loading = GameManager.IsLoading(); } catch { }
                    if (loading) throw new McpException(McpErr.StaleId, id + " is from before the load that is running now", "wait_until {any:[{ready:true}]}; the id works again when its object is back, else call observe");
                    GameObject found = null;
                    try { found = SoleAtPath(path, true); } catch (Exception) { found = null; }
                    if (found != null)
                    {
                        long nk = IdTable.Key(found.GetInstanceID(), (int)((key >> 4) & 0xF), (int)(key & 0xF));
                        if (ids.Rebind(id, nk, found)) return found;
                    }
                    string where = string.IsNullOrEmpty(path) ? "" : " (" + StateNames.PathTail(path, 70) + " is not loaded now, or several objects share that path)";
                    throw new McpException(McpErr.StaleId, (beforeLoad ? id + " is from before the last load and its object is gone" : id + " was destroyed and is gone") + where, "call observe again for current ids");
                }
                case IdLookup.Stale: throw new McpException(McpErr.StaleId, id + " is from before the last load", "call observe again for current ids");
                case IdLookup.Destroyed: throw new McpException(McpErr.StaleId, id + " was destroyed", "call observe again for current ids");
                case IdLookup.NotFound: throw new McpException(McpErr.NotFound, "no object has id " + id, "use an id from the latest observe");
                default: throw new McpException(McpErr.BadArgs, "'" + id + "' is not an id (o12, h3, b1)");
            }
        }

        // ids carried over a load so far: given back to the same path by observe, and resolved again on use
        internal static long IdsCarried { get { return ids.Revived; } }

        static GameObject ToGo(UObj o)
        {
            var c = o as Component;
            if (c != null) return c.gameObject;
            return o as GameObject;
        }

        static GameObject ResolveSpec(string spec)
        {
            string s = spec == null ? "" : spec.Trim();
            if (s.Length == 0) throw new McpException(McpErr.BadArgs, "empty target", "pass an id from observe (o12) or an object path");
            if (IdTable.LooksLikeId(s))
            {
                var go = ToGo(ResolveId(s));
                if (go == null) throw new McpException(McpErr.StaleId, s + " was destroyed", "call observe again for current ids");
                return go;
            }
            string low = s.ToLowerInvariant();
            if (low == "boy" || low == "huddle")
            {
                Character ch = low == "boy" ? (Character)ScriptGlobals.boy : ScriptGlobals.huddle;
                if (ch == null) throw new McpException(McpErr.NotFound, "no " + low + " in the scene", low == "boy" ? "wait until the game is ready" : "the huddle exists only in the master scene once loaded");
                return ch.gameObject;
            }
            if (s[0] == '#')
            {
                int iid;
                if (!int.TryParse(s.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out iid)) throw new McpException(McpErr.BadArgs, "'" + s + "' is not #<instanceID>");
                var rec = ObjectDatabase.Get(iid);
                if (rec != null && rec.go != null) return rec.go;
                var known = ToGo(ids.FindByInstance(iid) as UObj);
                if (known != null) return known;
                throw new McpException(McpErr.NotFound, "no loaded GameObject with instance id " + iid, "instance ids change on every load; use observe ids or a path");
            }
            string path = s.TrimStart('/');
            if (path.Length == 0) throw new McpException(McpErr.BadArgs, "empty path");
            if (!ObjectDatabase.Ready) ObjectDatabase.EnsureFresh(60f);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var exact = Alive(ObjectDatabase.ByPath(path));
                if (exact.Count == 1) return exact[0].go;
                if (exact.Count > 1) throw Ambiguous(s, exact);
                if (path.IndexOf('/') >= 0)
                {
                    var f = GameObject.Find("/" + path);
                    if (f != null) return f;
                }
                string suffix = "/" + path;
                var hits = new List<ObjRecord>();
                foreach (var r in ObjectDatabase.all)
                {
                    if (r == null || r.path == null) continue;
                    if ((r.path == path || r.path.EndsWith(suffix, StringComparison.Ordinal)) && r.go != null) { hits.Add(r); if (hits.Count > 50) break; }
                }
                if (hits.Count == 1) return hits[0].go;
                if (hits.Count > 1) throw Ambiguous(s, hits);
                if (attempt == 0 && ObjectDatabase.Age > 2f) ObjectDatabase.EnsureFresh(2f);
                else break;
            }
            throw new McpException(McpErr.NotFound, "no loaded object matches '" + s + "'", "use an id from observe, an exact hierarchy path, a unique path suffix or a unique name; objects of unloaded areas cannot be found");
        }

        static List<ObjRecord> Alive(List<ObjRecord> l)
        {
            var r = new List<ObjRecord>();
            if (l != null) foreach (var x in l) if (x != null && x.go != null) r.Add(x);
            return r;
        }

        static McpException Ambiguous(string spec, List<ObjRecord> hits)
        {
            var e = new McpException(McpErr.Ambiguous, "'" + spec + "' matches " + (hits.Count > 50 ? "more than 50" : hits.Count.ToString(CultureInfo.InvariantCulture)) + " objects",
                "use one of the candidate ids or a longer path");
            var cands = new List<object>();
            for (int i = 0; i < hits.Count && cands.Count < 5; i++)
            {
                var c = new Json.Obj();
                c["id"] = IdFor(hits[i].go, "o", 0);
                c["path"] = hits[i].path;
                cands.Add(c);
            }
            e.data = new Json.Obj();
            e.data["candidates"] = cands;
            return e;
        }

        // ---------------------------------------------------------------- epoch and the latest observation
        internal static void OnLoadStarted()
        {
            ids.BumpEpoch();
            latest = null;
            Perception.ForgetBody();
            Perception.InvalidateAll();
        }

        internal static void OnLoadFinished()
        {
            Perception.ForgetBody();
            Perception.InvalidateAll();
            try
            {
                passAtLoad = ObjectDatabase.pass;
                ObjectDatabase.MarkDirty("play: load finished");
            }
            catch (Exception) { }
        }

        internal static ObjInfo LatestObject(GameObject go)
        {
            var l = latest;
            if (l == null || l.epoch != ids.Epoch || go == null) return null;
            foreach (var o in l.objects) if (o.go == go) return o;
            return null;
        }

        static List<ShotMark> Marks(int max)
        {
            var r = new List<ShotMark>();
            var l = latest;
            if (l == null || l.epoch != ids.Epoch || max <= 0) return r;
            var cam = Camera.main;
            if (cam == null) return r;
            float w = UnityEngine.Screen.width, h = UnityEngine.Screen.height;
            var pts = new List<KeyValuePair<float, KeyValuePair<string, Vector3>>>();
            if (l.hasBody && l.bodyId != null) pts.Add(Pt(0f, l.bodyId, l.bodyPos));
            foreach (var o in l.objects) if (o.id != null) pts.Add(Pt(o.dist, o.id, o.pos));
            foreach (var hz in l.hazards) if (hz.id != null && hz.kind != "shockwave") pts.Add(Pt(hz.dist, hz.id, hz.pos));
            pts.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var p in pts)
            {
                Vector3 v = cam.WorldToViewportPoint(p.Value.Value);
                if (v.z <= 0f || v.x < 0f || v.x > 1f || v.y < 0f || v.y > 1f) continue;
                r.Add(new ShotMark { id = p.Value.Key, label = p.Value.Key, x = v.x * w, y = (1f - v.y) * h });
                if (r.Count >= max) break;
            }
            return r;
        }

        static KeyValuePair<float, KeyValuePair<string, Vector3>> Pt(float d, string id, Vector3 p)
        {
            return new KeyValuePair<float, KeyValuePair<string, Vector3>>(d, new KeyValuePair<string, Vector3>(id, p));
        }
    }
}
