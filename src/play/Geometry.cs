using System;
using System.Collections.Generic;
using System.Diagnostics;
using InsideDev.McpExt;
using UnityEngine;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("NavTest")]

namespace InsideDev.Play
{
    // Geometry.cs (owner NAV, GridSlice / NavGeometry API and the geometry schema frozen; class name NavGeometry because
    // InsideDev.Geometry exists in the core): occupancy slice of the level at the body's depth plane. Cells are raycast /
    // overlap tested at z = boy.plane.posz with CollisionUtils.Layers.defaultCollisionMask (Ungrouped | Environment |
    // Item | Default), triggers ignored except water. At most about 2000 queries per call, split into main-thread chunks
    // of at most 2 ms. GridSlice is pure data (offline-testable).
    //   cell chars: '#' solid, '.' free, '~' water, 'H' ladder, '|' rope, 'B' body, '^' hazard, '?' not loaded
    // How a slice is built (SliceJob): every column is cast down and up and every row left and right, each ray going on
    // past the surfaces it hits (triggers skipped), plus an overlap test at each line start; NavScan turns the hits into
    // cells (a hit cell is solid, other cells are solid when the column and the row scan agree). Inside the boy's 2 m
    // collision plane the game's own 2D shapes (boy.plane.colPlane, the shapes the boy collides with) replace the ray
    // result. Then BoyWater bounds (~), Ladder spans (H), Rope bodies (|), Killer / DelayedKiller / HeadlessKillTrigger
    // bounds and Perception kill_zone / crusher hazards (^; moving enemies are left to observe), subscene culling bounds of inactive subscenes (? on free cells) and the body (B).
    public sealed class GridSlice
    {
        public float originX, originY, z, cell;
        public int cols, rows;
        // row-major from the bottom row (row 0 at originY)
        public char[] cells;

        public GridSlice(float originX, float originY, float z, float cell, int cols, int rows)
        {
            if (!(cell > 0f)) throw new ArgumentException("cell must be > 0");
            if (cols < 0 || rows < 0) throw new ArgumentException("cols and rows must be >= 0");
            this.originX = originX; this.originY = originY; this.z = z; this.cell = cell; this.cols = cols; this.rows = rows;
            cells = new char[cols * rows];
            for (int i = 0; i < cells.Length; i++) cells[i] = '.';
        }

        // outside the slice: '?'
        public char At(int col, int row)
        {
            if (col < 0 || row < 0 || col >= cols || row >= rows) return '?';
            return cells[row * cols + col];
        }

        public void Set(int col, int row, char c)
        {
            if (col < 0 || row < 0 || col >= cols || row >= rows) return;
            cells[row * cols + col] = c;
        }

        // world position -> cell; false when outside
        public bool CellOf(float x, float y, out int col, out int row)
        {
            col = ColOf(x); row = RowOf(y);
            return col >= 0 && row >= 0 && col < cols && row < rows;
        }

        // text rows, top row first
        public List<string> ToRows()
        {
            var l = new List<string>();
            for (int r = rows - 1; r >= 0; r--) l.Add(new string(cells, r * cols, cols));
            return l;
        }

        internal int ColOf(float x) { return (int)Math.Floor((x - originX) / cell); }
        internal int RowOf(float y) { return (int)Math.Floor((y - originY) / cell); }
        internal float CellX(int col) { return originX + (col + 0.5f) * cell; }
        internal float CellY(int row) { return originY + (row + 0.5f) * cell; }
        internal float TopY(int row) { return originY + (row + 1) * cell; }
        internal float Width { get { return cols * cell; } }
        internal float Height { get { return rows * cell; } }

        internal int Count(char c)
        {
            int n = 0;
            foreach (var x in cells) if (x == c) n++;
            return n;
        }
    }

    public static class NavGeometry
    {
        public const string Owner = "NAV";
        internal const int RayBudget = 1900, MaxCols = 256, MaxRows = 128;
        internal const double ChunkMs = 1.5;

        public static void Register()
        {
            PlayRegistry.Add(PlayShared.Def("geometry", "Level geometry", PlayShared.Both, Owner,
                "Occupancy slice of the level around the body (or a point) at its depth plane: text rows with # solid, . free, ~ water, H ladder, | rope, B body, ^ hazard, ? not loaded; plus ledges. Look ahead before move_to (pits, walls, ledges up), plan routes, debug blocked moves.",
                Schema.Obj().Enum("op", "slice, ledges or probe (one column under / over a point).", "slice", "slice", "ledges", "probe")
                    .Str("center", "Object id or path (default the body).").Num("x", "World x instead of center.", -1e5, 1e5).Num("y", "World y.", -1e5, 1e5)
                    .Num("width", "Slice width (m).", 16, 2, 64).Num("height", "Slice height (m).", 8, 2, 32).Num("cell", "Cell size (m).", 0.25, 0.1, 1).Build(),
                Schema.Obj().OutObj("origin", "World position of the bottom-left cell.", Schema.Obj().Num("x", "", -1e5, 1e5).Num("y", "", -1e5, 1e5).Num("z", "", -1e5, 1e5))
                    .Num("cell", "Cell size (m).", 0, 10).Int("cols", "", 0, 1000).Int("rows", "", 0, 1000)
                    .Arr("grid", "Text rows, top first.", Schema.Str(), 0, 400)
                    .Arr("ledges", "Grabbable ledges.", Schema.OutOf(Schema.Obj().Num("x", "", -1e5, 1e5).Num("y", "", -1e5, 1e5).Enum("side", "", null, "left", "right").Bool("climbable", "")), 0, 64)
                    .OutObj("probe", "op probe: ground / ceiling under / over the point.", Schema.Obj().Open()).BuildOutput()).ReadOnly(), Handle);
        }

        static ToolResult Handle(Json.Obj a, CallContext ctx)
        {
            string op = PlayShared.Str(a, "op", "slice");
            float width = (float)PlayShared.Num(a, "width", 16), height = (float)PlayShared.Num(a, "height", 8), cell = (float)PlayShared.Num(a, "cell", 0.25);
            var spot = ctx.Main(() => NavGame.Spot(a));
            var r = PlayShared.Env();
            if (op == "probe")
            {
                r["probe"] = ctx.Main(() => Probe(spot));
                return ToolResult.Ok(r);
            }
            var job = ctx.Main(() => new SliceJob(spot.x, spot.y, spot.z, width, height, cell, RayBudget));
            while (!ctx.Main(() => job.Step(ChunkMs))) ctx.ThrowIfCancelled();
            var g = job.grid;
            var o = new Json.Obj(); o["x"] = PlayShared.R(g.originX); o["y"] = PlayShared.R(g.originY); o["z"] = PlayShared.R(g.z);
            r["origin"] = o;
            r["cell"] = PlayShared.R(g.cell, 3); r["cols"] = (double)g.cols; r["rows"] = (double)g.rows;
            if (op == "slice") r["grid"] = PlayShared.L(g.ToRows());
            var caps = NavGame.Caps(null);
            var led = NavPlan.MergeLedges(job.GameLedges(g, caps), NavPlan.GridLedges(g, caps), spot.x, spot.y, Math.Max(width, height), 64);
            var ll = new List<object>();
            foreach (var e in led)
            {
                var q = new Json.Obj(); q["x"] = PlayShared.R(e.x); q["y"] = PlayShared.R(e.y); q["side"] = e.side; q["climbable"] = e.climbable; q["source"] = e.source;
                ll.Add(q);
            }
            r["ledges"] = ll;
            var c = new Json.Obj(); c["x"] = PlayShared.R(spot.x); c["y"] = PlayShared.R(spot.y); c["from"] = spot.from;
            r["center"] = c;
            r["rays"] = (double)job.rays;
            r["legend"] = "# solid . free ~ water H ladder | rope B body ^ hazard ? not loaded / not scanned; top row first; row 0 at origin.y";
            if (job.notes.Count > 0) r["notes"] = PlayShared.L(job.notes);
            return ToolResult.Ok(r);
        }

        // main thread (callers chunk it): slice centred on center
        public static GridSlice Slice(Vector3 center, float width, float height, float cell)
        {
            float z;
            if (!NavGame.BodyPlane(out z)) z = center.z;
            var job = new SliceJob(center.x, center.y, z, width, height, cell, RayBudget);
            while (!job.Step(double.MaxValue)) { }
            return job.grid;
        }

        // main thread: ledge points near center (GrabSearcher / edges), world coordinates
        public static List<Vector3> Ledges(Vector3 center, float radius)
        {
            float z;
            if (!NavGame.BodyPlane(out z)) z = center.z;
            float w = Mathf.Clamp(radius * 2f, 2f, 64f), h = Mathf.Clamp(radius * 2f, 2f, 32f);
            var job = new SliceJob(center.x, center.y, z, w, h, 0.25f, RayBudget);
            while (!job.Step(double.MaxValue)) { }
            var caps = NavGame.Caps(null);
            var res = new List<Vector3>();
            foreach (var e in NavPlan.MergeLedges(job.GameLedges(job.grid, caps), NavPlan.GridLedges(job.grid, caps), center.x, center.y, radius, 64))
                res.Add(new Vector3(e.x, e.y, z));
            return res;
        }

        // main thread: the ground under and the ceiling over a point (rays at the slice z, triggers skipped)
        static Json.Obj Probe(NavGame.SpotInfo s)
        {
            int mask = CollisionUtils.Layers.defaultCollisionMask;
            var o = new Json.Obj();
            o["x"] = PlayShared.R(s.x); o["y"] = PlayShared.R(s.y); o["z"] = PlayShared.R(s.z);
            var p = new Vector3(s.x, s.y, s.z);
            int rays = 0;
            var down = new List<float>(); var up = new List<float>();
            var downNames = new List<string>();
            SliceJob.Cast(p, Vector3.down, 40f, mask, down, downNames, true, 6, ref rays, 64);
            SliceJob.Cast(p, Vector3.up, 40f, mask, up, null, true, 1, ref rays, 64);
            bool inside = SliceJob.InsideAt(p, mask);
            o["inside_solid"] = inside;
            if (down.Count > 0)
            {
                o["ground_y"] = PlayShared.R(down[0]); o["drop"] = PlayShared.R(s.y - down[0]);
                if (downNames.Count > 0) o["ground"] = downNames[0];
                var all = new List<object>();
                foreach (var d in down) all.Add(PlayShared.R(d));
                o["surfaces_below"] = all;
            }
            else { o["ground_y"] = null; o["drop"] = null; }
            if (up.Count > 0) { o["ceiling_y"] = PlayShared.R(up[0]); o["headroom"] = PlayShared.R(up[0] - (down.Count > 0 ? down[0] : s.y)); }
            else o["ceiling_y"] = null;
            NavScene.Refresh();
            foreach (var w in NavScene.waters)
            {
                if (w == null) continue;
                var col = w.GetComponent<Collider>();
                if (col == null) continue;
                var b = col.bounds;
                if (s.x >= b.min.x && s.x <= b.max.x && s.y >= b.min.y - 3f && s.y <= b.max.y && s.z >= b.min.z - 0.5f && s.z <= b.max.z + 0.5f)
                {
                    o["water_surface_y"] = PlayShared.R(b.max.y); o["in_water"] = s.y <= b.max.y && s.y >= b.min.y; o["water"] = PlayShared.PathOf(w.transform);
                    break;
                }
            }
            if (!o.ContainsKey("in_water")) o["in_water"] = false;
            o["rays"] = (double)rays;
            return o;
        }
    }

    // main-thread game reads shared by the geometry tool and move_to. Never caches a body (re-created on respawn).
    internal static class NavGame
    {
        internal sealed class SpotInfo
        {
            public float x, y, z;
            public string from = "body";
        }

        internal static Character Active()
        {
            try { var c = Perception.ActiveCharacter(); if (c != null) return c; }
            catch (NotImplementedException) { }
            return ScriptGlobals.mainCharacter;
        }

        internal static float PlaneZ(Character ch)
        {
            var boy = ch as Boy;
            if (boy != null && boy.plane != null) return boy.plane.posz;
            var hud = ch as Huddle;
            if (hud != null && hud.plane != null) return hud.plane.posz;
            return ch != null ? ch.pos3.z : 0f;
        }

        internal static bool BodyPlane(out float z)
        {
            z = 0f;
            var ch = Active();
            if (ch == null) return false;
            z = PlaneZ(ch);
            return true;
        }

        internal static bool BodyCenter(out Vector3 p)
        {
            p = Vector3.zero;
            var ch = Active();
            if (ch == null) return false;
            var boy = ch as Boy;
            p = boy != null ? new Vector3(boy.pos.x, boy.pos.y, PlaneZ(boy)) : ch.pos3;
            return true;
        }

        internal static float HalfHeight(Character ch)
        {
            var boy = ch as Boy;
            if (boy != null && boy.properties != null) return boy.properties.height * 0.5f;
            var hud = ch as Huddle;
            if (hud != null) { try { return hud.height * 0.5f; } catch (Exception) { return 0.9f; } }
            return 0.665f;
        }

        // the centre of a target: union of its non-trigger collider bounds, else its renderer bounds, else its position
        internal static Bounds TargetBounds(GameObject go, List<Collider> colliders)
        {
            bool any = false;
            var b = new Bounds(go.transform.position, Vector3.zero);
            foreach (var c in go.GetComponentsInChildren<Collider>())
            {
                if (c == null || c.isTrigger || !c.enabled) continue;
                if (colliders != null) colliders.Add(c);
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            if (any) return b;
            foreach (var c in go.GetComponentsInChildren<Collider>())
            {
                if (c == null || !c.enabled) continue;
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            if (any) return b;
            var rd = go.GetComponentInChildren<Renderer>();
            if (rd != null) return rd.bounds;
            return b;
        }

        internal static SpotInfo Spot(Json.Obj a)
        {
            var s = new SpotInfo();
            Vector3 body;
            bool hasBody = BodyCenter(out body);
            float z;
            bool hasPlane = BodyPlane(out z);
            if (PlayShared.Has(a, "center"))
            {
                var go = Observer.ResolveTarget(PlayShared.Str(a, "center", ""));
                var b = TargetBounds(go, null);
                s.x = b.center.x; s.y = b.center.y; s.z = hasPlane ? z : b.center.z; s.from = PlayShared.Str(a, "center", "");
                return s;
            }
            if (PlayShared.Has(a, "x"))
            {
                s.x = (float)PlayShared.Num(a, "x", 0);
                s.y = PlayShared.Has(a, "y") ? (float)PlayShared.Num(a, "y", 0) : (hasBody ? body.y : 0f);
                s.z = hasPlane ? z : 0f; s.from = "point";
                return s;
            }
            if (!hasBody) throw new McpException(McpErr.NotReady, "no controlled body (loading or title)", "give x and y, or wait_until {ready:true}");
            s.x = body.x; s.y = PlayShared.Has(a, "y") ? (float)PlayShared.Num(a, "y", 0) : body.y; s.z = body.z;
            return s;
        }

        // the planner's numbers for the controlled body (BoyProperties, BoyUtils.GetJumpHeightEstimate); defaults without a boy
        internal static NavCaps Caps(Character ch)
        {
            var caps = new NavCaps();
            var boy = ch as Boy;
            if (boy == null) return caps;
            try
            {
                var p = boy.properties;
                if (p != null)
                {
                    caps.height = p.height; caps.radius = p.radius;
                    if (p.run != null && p.run.maxSpeedFlat > 0f) caps.runSpeed = p.run.maxSpeedFlat;
                    if (p.jump != null && p.jump.takeoffVelocityRun != null) { caps.jumpVx = p.jump.takeoffVelocityRun.x; caps.jumpVy = p.jump.takeoffVelocityRun.y; }
                    if (p.jump != null && !p.jump.allow) caps.canJump = false;
                }
                float gm = boy.gravity.magnitude;
                if (gm > 0.1f) caps.gravity = gm;
                float jr = BoyUtils.GetJumpHeightEstimate(boy);
                if (jr > 0.5f && jr < 5f) caps.jumpReach = jr;
            }
            catch (Exception) { }
            return caps;
        }
    }

    // FindObjectsOfType results for the overlays, one type per main-thread chunk, reused for 3 s (realtime) unless an
    // entry was destroyed; streaming adds objects without a load, so the cache is short-lived instead of per load.
    internal static class NavScene
    {
        internal const int Kinds = 7;
        static float at = -100f;
        internal static readonly List<Ladder> ladders = new List<Ladder>();
        internal static readonly List<Rope> ropes = new List<Rope>();
        internal static readonly List<BoyWater> waters = new List<BoyWater>();
        internal static readonly List<Killer> killers = new List<Killer>();
        internal static readonly List<DelayedKiller> delayed = new List<DelayedKiller>();
        internal static readonly List<HeadlessKillTrigger> headless = new List<HeadlessKillTrigger>();
        internal static readonly List<SubsceneCuller> cullers = new List<SubsceneCuller>();

        internal static bool Fresh()
        {
            if (Time.realtimeSinceStartup - at > 3f) return false;
            foreach (var x in ladders) if (x == null) return false;
            foreach (var x in ropes) if (x == null) return false;
            foreach (var x in waters) if (x == null) return false;
            return true;
        }

        static void Fill<T>(List<T> l) where T : UnityEngine.Object
        {
            l.Clear();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(typeof(T))) { var t = o as T; if (t != null) l.Add(t); }
        }

        internal static void Scan(int k)
        {
            switch (k)
            {
                case 0: Fill(ladders); break;
                case 1: Fill(ropes); break;
                case 2: Fill(waters); break;
                case 3: Fill(killers); break;
                case 4: Fill(delayed); break;
                case 5: Fill(headless); break;
                default: Fill(cullers); at = Time.realtimeSinceStartup; break;
            }
        }

        internal static void Refresh()
        {
            if (Fresh()) return;
            for (int k = 0; k < Kinds; k++) Scan(k);
        }
    }

    // one slice, built in chunks on the main thread (Step returns true when done)
    internal sealed class SliceJob
    {
        public readonly GridSlice grid;
        public int rays;
        public readonly int budget;
        public readonly List<string> notes = new List<string>();
        readonly int mask;
        readonly int[] colOrder, rowOrder;
        readonly bool[] colKnown, rowKnown, colIn, rowIn, hit;
        readonly List<float[]> shapeX = new List<float[]>(), shapeY = new List<float[]>();
        readonly List<float> shapeR = new List<float>();
        readonly List<NavLedge> gameLedges = new List<NavLedge>();
        bool hasBody, hasPlane;
        float bodyX, bodyFeet, bodyHeight = 1.33f, bodyRadius = 0.21f;
        int phase, index;
        readonly List<float> fwd = new List<float>(), back = new List<float>();

        public SliceJob(float cx, float cy, float z, float width, float height, float cell, int budget)
        {
            float c = NavPlan.FitCell(width, height, cell, NavGeometry.MaxCols, NavGeometry.MaxRows);
            if (Math.Abs(c - cell) > 1e-4f) notes.Add("cell raised to " + PlayShared.F(c) + " m to keep the slice under " + NavGeometry.MaxCols + " x " + NavGeometry.MaxRows + " cells");
            int cols = Math.Max(1, (int)Math.Ceiling(width / c - 1e-4)), rows = Math.Max(1, (int)Math.Ceiling(height / c - 1e-4));
            grid = new GridSlice(cx - cols * c * 0.5f, cy - rows * c * 0.5f, z, c, cols, rows);
            this.budget = budget;
            mask = CollisionUtils.Layers.defaultCollisionMask;
            colKnown = new bool[cols]; rowKnown = new bool[rows];
            colIn = new bool[cols * rows]; rowIn = new bool[cols * rows]; hit = new bool[cols * rows];
            colOrder = Order(cols); rowOrder = Order(rows);
            CaptureBody();
        }

        static int[] Order(int n)
        {
            var o = new int[n];
            int mid = n / 2, k = 0;
            for (int d = 0; k < n; d++)
            {
                if (mid + d < n) o[k++] = mid + d;
                if (d > 0 && mid - d >= 0 && k < n) o[k++] = mid - d;
            }
            return o;
        }

        void CaptureBody()
        {
            var ch = NavGame.Active();
            if (ch == null) return;
            var boy = ch as Boy;
            float hh = NavGame.HalfHeight(ch);
            Vector3 c;
            if (!NavGame.BodyCenter(out c)) return;
            hasBody = true;
            bodyX = c.x; bodyFeet = c.y - hh; bodyHeight = hh * 2f;
            if (boy != null && boy.properties != null) bodyRadius = boy.properties.radius;
            if (boy == null || boy.plane == null || boy.plane.colPlane == null || Math.Abs(boy.plane.posz - grid.z) > 0.01f) return;
            var shapes = boy.plane.colPlane.GetShapes(CollisionUtils.Type.Solid);
            if (shapes == null) return;
            for (int i = 0; i < shapes.size; i++)
            {
                var sh = shapes.raw[i];
                if (sh == null || sh.box2dShape == null) continue;
                if (sh.box2dShape.convexBox2dShapes != null && sh.box2dShape.convexBox2dShapes.Length > 0)
                {
                    foreach (var cs in sh.box2dShape.convexBox2dShapes) AddShape(cs);
                }
                else AddShape(sh.box2dShape);
            }
            hasPlane = true;
            var gs = boy.plane.colPlane.grabSearcher;
            if (gs == null || gs.vertices == null) return;
            var vs = gs.vertices;
            for (int i = 0; i < vs.size; i++)
            {
                var gv = vs.raw[i];
                if (gv == null) continue;
                gameLedges.Add(new NavLedge { x = gv.pos.x, y = gv.pos.y, side = gv.facing > 0f ? "left" : "right", source = "game" });
            }
        }

        void AddShape(CollisionUtils.Box2dShape s)
        {
            if (s == null || s.vertices == null || s.vertices.Length == 0) return;
            var xs = new float[s.vertices.Length]; var ys = new float[s.vertices.Length];
            for (int i = 0; i < xs.Length; i++) { xs[i] = s.vertices[i].x; ys[i] = s.vertices[i].y; }
            shapeX.Add(xs); shapeY.Add(ys); shapeR.Add(s.radius);
        }

        // the game's grab vertices (climbable from the grid's headroom over the ledge)
        public List<NavLedge> GameLedges(GridSlice g, NavCaps caps)
        {
            foreach (var e in gameLedges)
            {
                int c = g.ColOf(e.side == "left" ? e.x + g.cell * 0.5f : e.x - g.cell * 0.5f);
                e.climbable = !NavPlan.BandSolid(g, c, e.y, e.y + caps.headroom);
            }
            return gameLedges;
        }

        // main thread: one chunk; true when the slice is complete
        public bool Step(double budgetMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < budgetMs)
            {
                if (phase == 0)
                {
                    if (index >= colOrder.Length) { phase = 1; index = 0; continue; }
                    if (rays + 8 > budget) { notes.Add("ray budget reached: " + (colOrder.Length - index) + " columns not scanned"); phase = 1; index = 0; continue; }
                    ScanColumn(colOrder[index++]);
                }
                else if (phase == 1)
                {
                    if (index >= rowOrder.Length) { phase = 2; index = 0; continue; }
                    if (rays + 8 > budget) { notes.Add("ray budget reached: " + (rowOrder.Length - index) + " rows not scanned"); phase = 2; index = 0; continue; }
                    ScanRow(rowOrder[index++]);
                }
                else if (phase == 2)
                {
                    if (index == 0 && NavScene.Fresh()) { phase = 3; continue; }
                    if (index >= NavScene.Kinds) { phase = 3; continue; }
                    NavScene.Scan(index++);
                    return false;
                }
                else if (phase == 3) { Compose(); phase = 4; return false; }
                else if (phase == 4) { Overlay(); phase = 5; return false; }
                else
                {
                    Unloaded();
                    if (hasBody) NavRaster.Box(grid, bodyX - bodyRadius, bodyFeet + 0.02f, bodyX + bodyRadius, bodyFeet + bodyHeight, 'B', ".~H|");
                    return true;
                }
            }
            return false;
        }

        // multi-hit ray: goes on past each surface (from inside a collider PhysX does not hit it again); triggers skipped
        internal static void Cast(Vector3 o, Vector3 d, float len, int mask, List<float> coords, List<string> names, bool vertical, int maxHits, ref int rays, int budget)
        {
            float travelled = 0f;
            int n = 0, guard = 0;
            while (travelled < len && n < maxHits && rays < budget && guard++ < 40)
            {
                RaycastHit h;
                rays++;
                if (!Physics.Raycast(o + d * travelled, d, out h, len - travelled, mask)) break;
                travelled += h.distance + 0.005f;
                if (h.collider == null || h.collider.isTrigger) continue;
                coords.Add(vertical ? h.point.y : h.point.x);
                if (names != null) names.Add(PlayShared.PathOf(h.collider.transform));
                n++;
            }
        }

        // a non-trigger collider overlaps the point (convex / primitive colliders; mesh interiors are not detected)
        internal static bool InsideAt(Vector3 p, int mask)
        {
            var cs = Physics.OverlapSphere(p, 0.004f, mask);
            if (cs == null) return false;
            foreach (var c in cs) if (c != null && !c.isTrigger) return true;
            return false;
        }

        void ScanColumn(int c)
        {
            var g = grid;
            float x = g.CellX(c), top = g.originY + g.Height, len = g.Height + 0.01f;
            fwd.Clear(); back.Clear();
            rays++;
            bool start = InsideAt(new Vector3(x, top - 0.005f, g.z), mask);
            var down = new List<float>(); var up = new List<float>();
            Cast(new Vector3(x, top, g.z), Vector3.down, len, mask, down, null, true, 16, ref rays, budget);
            Cast(new Vector3(x, g.originY, g.z), Vector3.up, len, mask, up, null, true, 16, ref rays, budget);
            foreach (var y in down) { fwd.Add(top - y); hit[NavScan.HitCell(y, g.originY, g.cell, g.rows, false) * g.cols + c] = true; }
            foreach (var y in up) { back.Add(top - y); hit[NavScan.HitCell(y, g.originY, g.cell, g.rows, true) * g.cols + c] = true; }
            if (down.Count == 0 && up.Count == 0 && !start) return;
            var inside = NavScan.FillLine(g.rows, g.cell, fwd, back, start);
            for (int i = 0; i < g.rows; i++) colIn[(g.rows - 1 - i) * g.cols + c] = inside[i];
            colKnown[c] = true;
        }

        void ScanRow(int r)
        {
            var g = grid;
            float y = g.CellY(r), left = g.originX, right = g.originX + g.Width, len = g.Width + 0.01f;
            fwd.Clear(); back.Clear();
            rays++;
            bool start = InsideAt(new Vector3(left + 0.005f, y, g.z), mask);
            var rt = new List<float>(); var lt = new List<float>();
            Cast(new Vector3(left, y, g.z), Vector3.right, len, mask, rt, null, false, 16, ref rays, budget);
            Cast(new Vector3(right, y, g.z), Vector3.left, len, mask, lt, null, false, 16, ref rays, budget);
            foreach (var x in rt) { fwd.Add(x - left); hit[r * g.cols + NavScan.HitCell(x, g.originX, g.cell, g.cols, true)] = true; }
            foreach (var x in lt) { back.Add(x - left); hit[r * g.cols + NavScan.HitCell(x, g.originX, g.cell, g.cols, false)] = true; }
            if (rt.Count == 0 && lt.Count == 0 && !start) return;
            var inside = NavScan.FillLine(g.cols, g.cell, fwd, back, start);
            for (int i = 0; i < g.cols; i++) rowIn[r * g.cols + i] = inside[i];
            rowKnown[r] = true;
        }

        void Compose()
        {
            var g = grid;
            var composed = NavScan.Compose(g.cols, g.rows, colKnown, colIn, rowKnown, rowIn, hit);
            Array.Copy(composed, g.cells, composed.Length);
            if (hasPlane)
            {
                float clear = Boy.colPlaneRadius - 0.2f;
                float cy = bodyFeet + bodyHeight * 0.5f;
                for (int r = 0; r < g.rows; r++)
                    for (int c = 0; c < g.cols; c++)
                    {
                        float dx = g.CellX(c) - bodyX, dy = g.CellY(r) - cy;
                        if (dx * dx + dy * dy <= clear * clear && g.At(c, r) == '#') g.Set(c, r, '.');
                    }
                for (int i = 0; i < shapeX.Count; i++) NavRaster.Fill(g, shapeX[i], shapeY[i], shapeR[i], '#');
            }
        }

        void Overlay()
        {
            var g = grid;
            float z = g.z, x0 = g.originX, y0 = g.originY, x1 = g.originX + g.Width, y1 = g.originY + g.Height;
            foreach (var w in NavScene.waters)
            {
                if (w == null || !w.isActiveAndEnabled) continue;
                var col = w.GetComponent<Collider>();
                if (col == null) continue;
                var b = col.bounds;
                if (z < b.min.z - 0.5f || z > b.max.z + 0.5f || b.max.x < x0 || b.min.x > x1 || b.max.y < y0 || b.min.y > y1) continue;
                NavRaster.Box(g, b.min.x, b.min.y, b.max.x, b.max.y, '~', ".?");
            }
            foreach (var l in NavScene.ladders)
            {
                if (l == null || !l.isActiveAndEnabled) continue;
                var p = l.transform.position;
                if (Math.Abs(p.z - z) > 1.5f) continue;
                float h;
                try { h = l.height; } catch (Exception) { continue; }
                var u = l.up3 * (h * 0.5f);
                NavRaster.Line(g, p.x - u.x, p.y - u.y, p.x + u.x, p.y + u.y, 'H');
            }
            foreach (var rp in NavScene.ropes)
            {
                if (rp == null || !rp.isActiveAndEnabled || rp.visualOnly) continue;
                var bodies = rp.bodies;
                if (bodies == null || bodies.Length == 0) continue;
                Vector3 prev = Vector3.zero;
                bool have = false;
                foreach (var rb in bodies)
                {
                    if (rb == null) continue;
                    var p = rb.position;
                    if (Math.Abs(p.z - z) > 1.5f) { have = false; continue; }
                    if (have) NavRaster.Line(g, prev.x, prev.y, p.x, p.y, '|');
                    prev = p; have = true;
                }
            }
            foreach (var k in NavScene.killers) if (k != null && k.isActiveAndEnabled && k.getActiveState()) KillBox(k.GetComponent<Collider>());
            foreach (var k in NavScene.delayed) if (k != null && k.isActiveAndEnabled) KillBox(k.GetComponent<Collider>());
            foreach (var k in NavScene.headless) if (k != null && k.isActiveAndEnabled && k.isActive) KillBox(k.GetComponent<Collider>());
            try
            {
                var hz = Perception.Hazards(new Vector3(g.originX + g.Width * 0.5f, g.originY + g.Height * 0.5f, z), Math.Max(g.Width, g.Height) * 0.75f, 8);
                if (hz != null)
                    foreach (var h in hz)
                        if (h != null && h.go != null && (h.kind == "kill_zone" || h.kind == "crusher"))
                            NavRaster.Box(g, h.pos.x - 0.3f, h.pos.y - 0.3f, h.pos.x + 0.3f, h.pos.y + 0.6f, '^', ".~");
            }
            catch (Exception) { }
        }

        void KillBox(Collider col)
        {
            if (col == null || !col.enabled) return;
            var b = col.bounds;
            var g = grid;
            if (g.z < b.min.z - 0.5f || g.z > b.max.z + 0.5f) return;
            NavRaster.Box(g, b.min.x, b.min.y, b.max.x, b.max.y, '^', ".~");
        }

        void Unloaded()
        {
            var g = grid;
            var active = new bool[g.cols * g.rows]; var inactive = new bool[g.cols * g.rows];
            bool anyInactive = false;
            foreach (var cu in NavScene.cullers)
            {
                if (cu == null) continue;
                Bounds b;
                bool on;
                try { b = cu.CullingBounds; on = cu.IsActive; } catch (Exception) { continue; }
                if (b.size == Vector3.zero || g.z < b.min.z - 2f || g.z > b.max.z + 2f) continue;
                if (b.max.x < g.originX || b.min.x > g.originX + g.Width || b.max.y < g.originY || b.min.y > g.originY + g.Height) continue;
                var mark = on ? active : inactive;
                int c0 = Math.Max(g.ColOf(b.min.x), 0), c1 = Math.Min(g.ColOf(b.max.x), g.cols - 1), r0 = Math.Max(g.RowOf(b.min.y), 0), r1 = Math.Min(g.RowOf(b.max.y), g.rows - 1);
                for (int r = r0; r <= r1; r++) for (int c = c0; c <= c1; c++) { mark[r * g.cols + c] = true; if (!on) anyInactive = true; }
            }
            if (!anyInactive) return;
            int n = 0;
            for (int i = 0; i < g.cells.Length; i++) if (inactive[i] && !active[i] && g.cells[i] == '.') { g.cells[i] = '?'; n++; }
            if (n > 0) notes.Add(n + " free cells lie in subscenes that are not loaded (?)");
        }
    }
}
