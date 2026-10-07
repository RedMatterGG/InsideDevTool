using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace InsideDev
{
    // Explorer search that puts the useful objects first.
    // The plain search matched words anywhere in the path, so "pig" returned every object of the pigFight level
    // (batched geometry, decals, bones ...). Here:
    //   - a NAME match is required (path-only matches are listed only when "path matches" is on)
    //   - results are ranked: how well the name matches (exact > starts with > word > inside), what the object does
    //     (character, game script, animation, sounds, state machine, trigger, signals), active, distance to the boy
    //   - clutter (batched/static geometry, decals, specular helpers, bone chains, spawned FX clones, pure render
    //     meshes) is hidden unless "clutter" is on; the count of hidden rows is shown
    //   - each row says in words what the object is: scripts, animation clips (and what is playing), sounds it uses,
    //     state machines, triggers, how far away it is and which parent it sits under
    //   - category chips narrow the list: animation / sounds / scripts / triggers / state machines / characters
    public static class SmartFind
    {
        public sealed class Hit { public ObjRecord r; public int score; public string text; public Color color; public int count = 1; public float nearest, farthest; public List<ObjRecord> members; }

        public const int Cap = 3000;
        public static int Truncated;   // rows left out by the cap in the last Run
        public enum Cat { All, Animation, Sounds, Scripts, Triggers, StateMachines, Characters }
        public static readonly string[] CatNames = { "all", "animation", "sounds", "scripts", "triggers", "state machines", "characters" };

        static readonly HashSet<string> engine = new HashSet<string>(StringComparer.Ordinal)
        {
            "Transform", "RectTransform", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "BoxCollider", "SphereCollider", "CapsuleCollider", "MeshCollider",
            "Rigidbody", "Light", "Camera", "Animation", "Animator", "AudioSource", "AudioListener", "ParticleSystem", "ParticleSystemRenderer", "LineRenderer",
            "TrailRenderer", "CharacterJoint", "HingeJoint", "FixedJoint", "ConfigurableJoint", "SpringJoint", "Cloth", "LODGroup", "Projector",
            // Playdead / plumbing scripts that say nothing about what the object does
            "MaterialInstance", "UnityAnimEventTrigger", "AnimationPreAwake", "BoundsCullerRuntime", "LateAwakeNode", "SubsceneNode", "SubsceneCuller",
            "SubsceneContentRoot", "PlayMakerFixedUpdate", "AkGameObj", "AkBank", "ColorBlendProbe", "LightCullingObject", "DecalProjector", "DecalRenderer",
            "BoyDepthObstruction", "IgnoreCollision", "AudioRagdollBody", "BuoyancyForce", "VfxParticleImpactLayermasked"
        };

        static bool Clutter(ObjRecord r)
        {
            string n = r.name;
            if (n.StartsWith("Batch_") || n.StartsWith("DECAL_") || n.StartsWith("LIGHT_Specular") || n.StartsWith("FX_Sunbeam") || n.Contains("(Clone)")) return true;
            if (r.pathLower.Contains("/bones/")) return true;
            // pure render mesh: only Transform + MeshFilter + a renderer, no children
            if (r.childCount == 0 && (r.kind == ObjKind.Renderer || r.kind == ObjKind.None) && r.compCount <= 3) return true;
            return false;
        }

        static int KindScore(ObjRecord r)
        {
            int s = 0; var k = r.kind;
            if ((k & ObjKind.Character) != 0 || r.Has("AnimcontrolledCharacter") || r.Has("AlbinoAI")) s += 30;
            if ((k & ObjKind.Script) != 0) s += 15;
            if ((k & ObjKind.Animation) != 0) s += 15;
            if ((k & ObjKind.Audio) != 0) s += 12;
            if ((k & ObjKind.StateMachine) != 0) s += 12;
            if ((k & ObjKind.Trigger) != 0) s += 8;
            if ((k & ObjKind.Signal) != 0) s += 8;
            if ((k & ObjKind.Savepoint) != 0) s += 6;
            if (k == ObjKind.Group) s -= 4;
            return s;
        }

        static bool InCat(ObjRecord r, Cat c)
        {
            switch (c)
            {
                case Cat.Animation: return (r.kind & ObjKind.Animation) != 0;
                case Cat.Sounds: return (r.kind & ObjKind.Audio) != 0 || SoundsOf(r).Count > 0;
                case Cat.Scripts: return GameScripts(r).Count > 0;
                case Cat.Triggers: return (r.kind & ObjKind.Trigger) != 0;
                case Cat.StateMachines: return (r.kind & ObjKind.StateMachine) != 0;
                case Cat.Characters: return (r.kind & ObjKind.Character) != 0 || r.Has("AnimcontrolledCharacter") || r.Has("AlbinoAI");
            }
            return true;
        }

        public static List<string> GameScripts(ObjRecord r)
        {
            var l = new List<string>();
            foreach (var c in r.comps) if (!engine.Contains(c) && c != "PlayMakerFSM" && c != "(missing)" && !l.Contains(c)) l.Add(c);
            return l;
        }

        // sound events this object references (sound library bindings) - cached per database pass
        static readonly Dictionary<int, List<string>> soundsByGo = new Dictionary<int, List<string>>();
        static int soundsVersion = -1;
        static readonly List<string> none = new List<string>();
        public static List<string> SoundsOf(ObjRecord r)
        {
            int v = SoundLibrary.defs.Count * 7919 + ObjectDatabase.completedPasses;
            if (v != soundsVersion)
            {
                soundsVersion = v; soundsByGo.Clear();
                foreach (var d in SoundLibrary.defs.Values)
                    foreach (var b in d.bindings)
                    {
                        if (b.go == null) continue;
                        int id = b.go.GetInstanceID(); List<string> l;
                        if (!soundsByGo.TryGetValue(id, out l)) soundsByGo[id] = l = new List<string>();
                        if (!l.Contains(d.name)) l.Add(d.name);
                    }
            }
            List<string> res; return soundsByGo.TryGetValue(r.id, out res) ? res : none;
        }

        public static string Describe(Hit h)
        {
            string t = Describe(h.r, h.count == 1);
            if (h.count > 1)
            {
                int k = h.r.path.LastIndexOf('/'); string par = k > 0 ? h.r.path.Substring(0, k) : ""; int k2 = par.LastIndexOf('/'); par = k2 >= 0 ? par.Substring(k2 + 1) : par;
                t = t.Replace(h.r.name + "   —", h.r.name + "  ×" + h.count + "   —") + "   " + h.nearest.ToString("0") + "–" + h.farthest.ToString("0") + " m   in " + par + "   (click = nearest)";
            }
            return t;
        }

        public static string Describe(ObjRecord r, bool withPlace)
        {
            var parts = new List<string>();
            if ((r.kind & ObjKind.Character) != 0) parts.Add("character");
            var gs = GameScripts(r);
            if (gs.Count > 0) parts.Add("script " + string.Join(", ", gs.Count > 3 ? gs.GetRange(0, 3).ToArray() : gs.ToArray()) + (gs.Count > 3 ? " +" + (gs.Count - 3) : ""));
            if (r.Has("PlayMakerFSM")) parts.Add("state machine");
            if ((r.kind & ObjKind.Animation) != 0 && r.go != null)
            {
                var an = r.go.GetComponent<Animation>();
                if (an != null && AnimSafe.CanWalkStates(an))
                {
                    int n = an.GetClipCount(); string playing = null;
                    if (an.isPlaying) foreach (AnimationState st in an) if (st != null && an.IsPlaying(st.name)) { playing = st.name; break; }
                    parts.Add(n == 0 ? "animation (no clips)" : "animation " + n + " clip" + (n == 1 ? "" : "s") + (playing != null ? ", playing " + playing : ""));
                }
                else if (r.go.GetComponent<Animator>() != null) parts.Add("animator");
                else parts.Add("animation");
            }
            var snd = SoundsOf(r);
            if (snd.Count > 0) parts.Add("sounds " + string.Join(", ", snd.Count > 2 ? snd.GetRange(0, 2).ToArray() : snd.ToArray()) + (snd.Count > 2 ? " +" + (snd.Count - 2) : ""));
            else if ((r.kind & ObjKind.Audio) != 0) parts.Add("audio");
            if ((r.kind & ObjKind.Trigger) != 0) parts.Add("trigger");
            if ((r.kind & ObjKind.Signal) != 0) parts.Add("signals");
            if ((r.kind & ObjKind.Savepoint) != 0) parts.Add("savepoint");
            if (parts.Count == 0) parts.Add(r.childCount > 0 ? "group of " + r.childCount : (r.kind & ObjKind.Renderer) != 0 ? "mesh" : "empty");
            var sb = new StringBuilder();
            sb.Append(r.activeSelf ? "" : "[off] ").Append(r.name).Append("   — ").Append(string.Join(" · ", parts.ToArray()));
            if (withPlace)
            {
                var ch = G.MainCharacter;
                if (ch != null) sb.Append("   ").Append(((Vector2)(r.pos - ch.pos3)).magnitude.ToString("0")).Append(" m");
                int k = r.path.LastIndexOf('/');
                if (k > 0) { string par = r.path.Substring(0, k); int k2 = par.LastIndexOf('/'); sb.Append("   in ").Append(k2 >= 0 ? par.Substring(k2 + 1) : par); }
            }
            return sb.ToString();
        }

        // ranked hits; hiddenClutter / pathOnly report what was left out
        public static List<Hit> Run(string query, Cat cat, bool clutter, bool pathMatches, out int hiddenClutter, out int hiddenPath)
        {
            hiddenClutter = hiddenPath = 0;
            var res = new List<Hit>();
            var words = new List<string>();
            foreach (var w in (query ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) words.Add(w);
            if (words.Count == 0) return res;
            var ch = G.MainCharacter; Vector3 boy = ch != null ? ch.pos3 : Vector3.zero;
            foreach (var r in ObjectDatabase.all)
            {
                if (r.go == null) continue;
                int nameScore = 0; bool ok = true, anyName = false;
                foreach (var w in words)
                {
                    int i = r.nameLower.IndexOf(w, StringComparison.Ordinal);
                    if (i >= 0)
                    {
                        anyName = true;
                        if (r.nameLower.Length == w.Length) nameScore += 100;
                        else if (i == 0) nameScore += 60;
                        else { char p = r.nameLower[i - 1]; nameScore += (p == '_' || p == ' ' || p == '-' || p == '.' || p == '@' || p == '/' || char.IsUpper(r.name[i])) ? 40 : 25; }   // VisualPig: "Pig" starts a word
                    }
                    else if (r.pathLower.IndexOf(w, StringComparison.Ordinal) < 0) { ok = false; break; }
                }
                if (!ok) continue;
                if (!InCat(r, cat)) continue;
                if (!anyName && !pathMatches) { hiddenPath++; continue; }
                if (!clutter && Clutter(r)) { hiddenClutter++; continue; }
                int score = nameScore + KindScore(r) + (r.activeInHierarchy ? 5 : 0);
                if (ch != null) score += Mathf.Max(0, 15 - (int)(((Vector2)(r.pos - boy)).magnitude / 4f));
                if (!anyName) score -= 50;
                res.Add(new Hit { r = r, score = score, nearest = ch != null ? ((Vector2)(r.pos - boy)).magnitude : 0f });
            }
            // copies (same name, same kind of object, same parent name - e.g. 17 background pigs "PigSS") become one row
            var groups = new Dictionary<string, Hit>();
            var merged = new List<Hit>();
            foreach (var h in res)
            {
                int k = h.r.path.LastIndexOf('/'); string par = k > 0 ? h.r.path.Substring(0, k) : "";
                int k2 = par.LastIndexOf('/'); par = k2 >= 0 ? par.Substring(k2 + 1) : par;
                string key = h.r.name + "|" + (int)h.r.kind + "|" + h.r.compCount + "|" + par;
                Hit g;
                if (groups.TryGetValue(key, out g))
                {
                    g.count++; if (g.members == null) g.members = new List<ObjRecord> { g.r }; g.members.Add(h.r);
                    if (h.nearest < g.nearest) { g.nearest = h.nearest; g.r = h.r; }   // the row selects the nearest copy
                    if (h.nearest > g.farthest) g.farthest = h.nearest;
                    if (h.score > g.score) g.score = h.score;
                    continue;
                }
                h.farthest = h.nearest; groups[key] = h; merged.Add(h);
            }
            res = merged;
            res.Sort((a, b) => b.score != a.score ? b.score.CompareTo(a.score) : string.CompareOrdinal(a.r.name, b.r.name));
            // rows beyond the cap are cut, and reported (Truncated) so nothing disappears silently
            Truncated = res.Count > Cap ? res.Count - Cap : 0;
            if (Truncated > 0) res.RemoveRange(Cap, Truncated);
            return res;
        }
    }
}
