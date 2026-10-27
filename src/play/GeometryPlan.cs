using System;
using System.Collections.Generic;

namespace InsideDev.Play
{
    // GeometryPlan.cs (owner NAV): the pure half of navigation, no Unity or game calls, so qa/mcp/NAV tests it offline on
    // hand-made GridSlices. NavScan turns ray hits along one scan line into inside / outside cells and composes the grid;
    // NavRaster fills 2D collision shapes (the boy's colPlane) into it; NavPlan walks a GridSlice from the body towards
    // the goal and returns the maneuvers on the way (climb, jump_climb, jump_gap, drop, ladder, swim) or where and why it
    // is blocked (wall, obstacle, ledge_up, ledge_down, pit, water, hazard). Heights are feet heights in world metres.
    internal sealed class NavCaps
    {
        public float height = 1.33f, radius = 0.21f, headroom = 0.9f;
        // auto step (Boy obstruction SmallStep), walk down without an edge (BoyEdgeDetector needs 0.5), auto climb onto
        // obstructions (BoyUtils.GetObstructionPlateau box 0.8 m), jump + grab (BoyUtils.GetJumpHeightEstimate)
        public float stepUp = 0.35f, stepDown = 0.5f, climbAuto = 0.8f, jumpReach = 2.0f, safeDrop = 2.5f;
        public float runSpeed = 2.72f, jumpVx = 3.0f, jumpVy = 3.9f, gravity = 9.81f, rangeScale = 0.85f;
        public float wadeDepth = 0.9f, maxGap = 3.2f, walkGap = 0.3f;
        public bool jumpGaps = true, climb = true, canJump = true;

        // how far above the feet the hands catch a ledge: the jump reach minus the highest rise of the feet
        public float HandReach() { return Math.Max(0.5f, jumpReach - jumpVy * jumpVy / (2f * Math.Max(gravity, 0.1f))); }

        // horizontal reach of a running jump that lands dy metres above the takeoff feet (negative = lower)
        public float JumpRange(float dy)
        {
            float disc = jumpVy * jumpVy - 2f * gravity * dy;
            if (disc < 0f || gravity <= 0f) return 0f;
            float t = (jumpVy + (float)Math.Sqrt(disc)) / gravity;
            return jumpVx * t * rangeScale;
        }

        // the gap width a jump can clear onto a surface at landY (feet takeoff at h); ledges above h + 0.3 are caught by the hands
        public float GapReach(float h, float landY)
        {
            float dy = landY - h;
            if (dy > jumpReach) return 0f;
            if (dy > 0.3f) return JumpRange(Math.Max(dy - HandReach(), -3f));
            return JumpRange(dy);
        }
    }

    internal sealed class NavStep
    {
        // climb, jump_climb, jump_gap, drop, ladder, swim, block, arrive
        public string kind, reason;
        // where the maneuver starts (wall or edge x, feet y) and where it ends
        public float x, y, toX, toY, h;

        public string Describe()
        {
            string s = kind + " x=" + PlayShared.F(x) + " y=" + PlayShared.F(y);
            if (kind == "climb" || kind == "jump_climb" || kind == "drop") s += " h=" + PlayShared.F(h);
            if (kind == "jump_gap" || kind == "ladder" || kind == "swim") s += " to " + PlayShared.F(toX) + "," + PlayShared.F(toY);
            if (reason != null) s += " (" + reason + ")";
            return s;
        }
    }

    internal sealed class NavRoute
    {
        public readonly List<NavStep> steps = new List<NavStep>();
        // arrive | block | open (the slice or the loaded area ends before the goal)
        public string end = "open", reason;
        public float endX, endY, dir = 1f;
        public bool unknownAhead, yIgnored;

        public NavStep Block { get { var s = Last; return s != null && s.kind == "block" ? s : null; } }
        public NavStep Last { get { return steps.Count > 0 ? steps[steps.Count - 1] : null; } }

        // the first step whose start lies ahead of x (within behind metres behind it)
        public NavStep Next(float x, float behind)
        {
            foreach (var s in steps) if ((s.x - x) * dir > -behind) return s;
            return null;
        }

        public NavStep Find(string kind)
        {
            foreach (var s in steps) if (s.kind == kind) return s;
            return null;
        }

        public List<object> Describe(int max)
        {
            var l = new List<object>();
            foreach (var s in steps) { if (l.Count >= max) break; l.Add(s.Describe()); }
            if (end == "open" && l.Count < max) l.Add("open x=" + PlayShared.F(endX) + (unknownAhead ? " (not loaded ahead)" : ""));
            return l;
        }
    }

    internal sealed class NavLedge
    {
        public float x, y;
        // the side the body hangs on: left = grabbed facing right (GrabSearcher.GrabVertex.facing +1)
        public string side;
        public bool climbable;
        public string source;
    }

    internal static class NavScan
    {
        // inside flags of the n cells of one scan line (cell size cell, s from the line start in scan direction) from
        // the entry hits of the forward ray (fwd, into solids) and of the backward ray (back, out of solids when walking
        // forward), startInside from an overlap test at the line start. Depth counting: a back hit with no open solid
        // means the line started inside a solid the overlap test missed (mesh colliders), so the start depth grows.
        internal static bool[] FillLine(int n, float cell, List<float> fwd, List<float> back, bool startInside)
        {
            var ev = new List<KeyValuePair<float, int>>();
            if (fwd != null) foreach (var s in fwd) ev.Add(new KeyValuePair<float, int>(s, 1));
            if (back != null) foreach (var s in back) ev.Add(new KeyValuePair<float, int>(s, -1));
            ev.Sort((p, q) => p.Key != q.Key ? p.Key.CompareTo(q.Key) : q.Value.CompareTo(p.Value));
            int run = startInside ? 1 : 0, min = run;
            foreach (var e in ev) { run += e.Value; if (run < min) min = run; }
            int depth = (startInside ? 1 : 0) + (min < 0 ? -min : 0);
            var res = new bool[n];
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                float c = (i + 0.5f) * cell;
                while (k < ev.Count && ev[k].Key <= c) { depth += ev[k].Value; k++; }
                res[i] = depth > 0;
            }
            return res;
        }

        // grid chars from the two scans: a cell is solid when a ray hit lies in it, or when both the column and the row
        // scan say inside (one-sided surfaces fill a whole line, the other axis cuts that back). A line that was not
        // scanned, or saw no hit and started outside, does not vote. Neither axis known: '?'.
        internal static char[] Compose(int cols, int rows, bool[] colKnown, bool[] colIn, bool[] rowKnown, bool[] rowIn, bool[] hit)
        {
            var cells = new char[cols * rows];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    bool ck = colKnown[c], rk = rowKnown[r];
                    char ch;
                    if (hit[i]) ch = '#';
                    else if (ck && rk) ch = colIn[i] && rowIn[i] ? '#' : '.';
                    else if (ck) ch = colIn[i] ? '#' : '.';
                    else if (rk) ch = rowIn[i] ? '#' : '.';
                    else ch = '?';
                    cells[i] = ch;
                }
            return cells;
        }

        // index of the cell that holds the solid side of a surface hit at coordinate v: solidAbove = the solid lies at
        // larger coordinates (a bottom face hit from below, a left face hit going +x)
        internal static int HitCell(float v, float origin, float cell, int n, bool solidAbove)
        {
            float f = (v - origin) / cell;
            int i = solidAbove ? (int)Math.Floor(f + 1e-4) : (int)Math.Ceiling(f - 1e-4) - 1;
            return i < 0 ? 0 : (i >= n ? n - 1 : i);
        }
    }

    internal static class NavRaster
    {
        internal static bool InPolygon(float[] xs, float[] ys, float px, float py)
        {
            bool inside = false;
            int n = xs.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if ((ys[i] > py) != (ys[j] > py) && px < (xs[j] - xs[i]) * (py - ys[i]) / (ys[j] - ys[i]) + xs[i]) inside = !inside;
            }
            return inside;
        }

        internal static float SegDist(float ax, float ay, float bx, float by, float px, float py)
        {
            float dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            float t = l2 > 1e-9f ? ((px - ax) * dx + (py - ay) * dy) / l2 : 0f;
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            float qx = ax + t * dx - px, qy = ay + t * dy - py;
            return (float)Math.Sqrt(qx * qx + qy * qy);
        }

        // true when the cell centre (px, py) is inside the shape: a circle (1 vertex + radius), a capsule (2), a polygon (3+)
        internal static bool Inside(float[] xs, float[] ys, float radius, float px, float py)
        {
            int n = xs.Length;
            if (n == 0) return false;
            if (n == 1) { float dx = px - xs[0], dy = py - ys[0]; return dx * dx + dy * dy <= radius * radius; }
            if (n == 2) return SegDist(xs[0], ys[0], xs[1], ys[1], px, py) <= Math.Max(radius, 0.01f);
            if (InPolygon(xs, ys, px, py)) return true;
            if (radius <= 0f) return false;
            for (int i = 0, j = n - 1; i < n; j = i++) if (SegDist(xs[j], ys[j], xs[i], ys[i], px, py) <= radius) return true;
            return false;
        }

        // sets every cell whose centre is inside the shape to c; returns the number of cells set
        internal static int Fill(GridSlice g, float[] xs, float[] ys, float radius, char c)
        {
            if (xs == null || ys == null || xs.Length == 0 || xs.Length != ys.Length) return 0;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < xs.Length; i++)
            {
                minX = Math.Min(minX, xs[i]); maxX = Math.Max(maxX, xs[i]); minY = Math.Min(minY, ys[i]); maxY = Math.Max(maxY, ys[i]);
            }
            float pad = Math.Max(radius, 0.01f);
            int c0 = g.ColOf(minX - pad), c1 = g.ColOf(maxX + pad), r0 = g.RowOf(minY - pad), r1 = g.RowOf(maxY + pad);
            c0 = Math.Max(c0, 0); r0 = Math.Max(r0, 0); c1 = Math.Min(c1, g.cols - 1); r1 = Math.Min(r1, g.rows - 1);
            int set = 0;
            for (int r = r0; r <= r1; r++)
                for (int cc = c0; cc <= c1; cc++)
                    if (Inside(xs, ys, radius, g.CellX(cc), g.CellY(r))) { g.Set(cc, r, c); set++; }
            return set;
        }

        // marks the cells along a segment (ladders, ropes) that are not solid
        internal static void Line(GridSlice g, float ax, float ay, float bx, float by, char c)
        {
            float len = (float)Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int n = Math.Max(1, (int)Math.Ceiling(len / (g.cell * 0.5f)));
            for (int i = 0; i <= n; i++)
            {
                float t = (float)i / n;
                int col, row;
                if (g.CellOf(ax + (bx - ax) * t, ay + (by - ay) * t, out col, out row) && g.At(col, row) != '#') g.Set(col, row, c);
            }
        }

        // marks the non-solid cells whose centre lies inside an axis-aligned box; over = the chars that may be replaced
        internal static void Box(GridSlice g, float minX, float minY, float maxX, float maxY, char c, string over)
        {
            int c0 = Math.Max(g.ColOf(minX), 0), c1 = Math.Min(g.ColOf(maxX), g.cols - 1);
            int r0 = Math.Max(g.RowOf(minY), 0), r1 = Math.Min(g.RowOf(maxY), g.rows - 1);
            for (int r = r0; r <= r1; r++)
                for (int cc = c0; cc <= c1; cc++)
                {
                    float x = g.CellX(cc), y = g.CellY(r);
                    if (x < minX || x > maxX || y < minY || y > maxY) continue;
                    if (over.IndexOf(g.At(cc, r)) >= 0) g.Set(cc, r, c);
                }
        }
    }

    internal static class NavPlan
    {
        const int MaxSteps = 24;

        internal static bool Solid(GridSlice g, int c, int r) { return g.At(c, r) == '#'; }

        // any solid cell of column c that overlaps the open interval (y0, y1)
        internal static bool BandSolid(GridSlice g, int c, float y0, float y1)
        {
            if (y1 < y0) return false;
            int r0 = Math.Max(g.RowOf(y0 + 1e-4f), 0), r1 = Math.Min(g.RowOf(y1 - 1e-4f), g.rows - 1);
            for (int r = r0; r <= r1; r++) if (Solid(g, c, r)) return true;
            return false;
        }

        internal static bool BandHas(GridSlice g, int c, float y0, float y1, char ch)
        {
            int r0 = Math.Max(g.RowOf(y0), 0), r1 = Math.Min(g.RowOf(y1), g.rows - 1);
            for (int r = r0; r <= r1; r++) if (g.At(c, r) == ch) return true;
            return false;
        }

        // the column holds no information in [y0, y1]: every cell there is '?' (not loaded or not scanned)
        internal static bool Unknown(GridSlice g, int c, float y0, float y1)
        {
            int r0 = Math.Max(g.RowOf(y0), 0), r1 = Math.Min(g.RowOf(y1), g.rows - 1);
            if (r1 < r0) return false;
            for (int r = r0; r <= r1; r++) if (g.At(c, r) != '?') return false;
            return true;
        }

        // free cells for headroom over a surface; rows above the slice are unknown, so a top too close to the slice top fails
        static bool Headroom(GridSlice g, int c, int topRow, float headroom)
        {
            int k = (int)Math.Ceiling(headroom / g.cell - 1e-4);
            if (topRow + k >= g.rows) return false;
            for (int r = topRow + 1; r <= topRow + k; r++) if (Solid(g, c, r)) return false;
            return true;
        }

        // standing surface in column c within [h - down, h + up] with headroom, closest to h (ties: the higher one)
        internal static float Support(GridSlice g, int c, float h, float up, float down, float headroom, out bool found)
        {
            found = false;
            float best = 0f, bestD = float.MaxValue;
            for (int r = 0; r < g.rows; r++)
            {
                if (!Solid(g, c, r) || Solid(g, c, r + 1)) continue;
                float top = g.TopY(r);
                if (top < h - down - 1e-4f || top > h + up + 1e-4f) continue;
                if (!Headroom(g, c, r, headroom)) continue;
                float d = Math.Abs(top - h);
                if (d < bestD - 1e-4f || (Math.Abs(d - bestD) <= 1e-4f && top > best)) { best = top; bestD = d; found = true; }
            }
            return best;
        }

        // the lowest standing surface above h + stepUp in column c that is at most maxH above h (the top of a wall)
        internal static float WallTop(GridSlice g, int c, float h, float stepUp, float maxH, float headroom, out bool found)
        {
            found = false;
            int r0 = Math.Max(g.RowOf(h + stepUp), 0);
            for (int r = r0; r < g.rows; r++)
            {
                float top = g.TopY(r);
                if (top > h + maxH + 1e-4f) break;
                if (Solid(g, c, r) && !Solid(g, c, r + 1) && Headroom(g, c, r, headroom)) { found = true; return top; }
            }
            return 0f;
        }

        // first solid top at or below y in column c; water / hazard when such cells lie on the way down; '?' ends the search
        internal static float FloorBelow(GridSlice g, int c, float y, out bool found, out bool water, out bool hazard)
        {
            found = water = hazard = false;
            int r = Math.Min((int)Math.Floor((y - g.originY) / g.cell + 1e-4) - 1, g.rows - 1);
            for (; r >= 0; r--)
            {
                char ch = g.At(c, r);
                if (ch == '#') { found = true; return g.TopY(r); }
                if (ch == '~') water = true;
                else if (ch == '^') hazard = true;
                else if (ch == '?') return 0f;
            }
            return 0f;
        }

        // depth of water standing on a surface at y in column c
        internal static float WaterDepth(GridSlice g, int c, float y)
        {
            int r = g.RowOf(y + g.cell * 0.5f);
            int n = 0;
            for (; r >= 0 && r < g.rows && g.At(c, r) == '~'; r++) n++;
            return n * g.cell;
        }

        // boundary x where the body enters column c moving in dir
        internal static float EdgeX(GridSlice g, int c, int dir) { return dir > 0 ? g.originX + c * g.cell : g.originX + (c + 1) * g.cell; }

        static NavStep Add(NavRoute r, string kind, float x, float y, float toX, float toY, float h, string reason)
        {
            var s = new NavStep { kind = kind, x = x, y = y, toX = toX, toY = toY, h = h, reason = reason };
            r.steps.Add(s);
            return s;
        }

        static NavRoute End(NavRoute r, string end, string reason, float x, float y)
        {
            r.end = end; r.reason = reason; r.endX = x; r.endY = y;
            if (end == "block") Add(r, "block", x, y, x, y, 0f, reason);
            return r;
        }

        // landing for a jump over the gap that starts at column nc: the first column within maxGap with a surface at
        // most 1 m lower and at most jumpReach higher, free air over the gap, and a width the jump clears
        internal static bool FindLanding(GridSlice g, int nc, int dir, float h, NavCaps caps, out int landCol, out float landY, out float width)
        {
            landCol = -1; landY = 0f; width = 0f;
            float ex = EdgeX(g, nc, dir);
            for (int k = 1; ; k++)
            {
                int c = nc + dir * k;
                if (c < 0 || c >= g.cols) return false;
                float w = Math.Abs(EdgeX(g, c, dir) - ex);
                if (w > caps.maxGap) return false;
                if (BandSolid(g, nc + dir * (k - 1), h + caps.stepUp, h + caps.height + 0.4f)) return false;
                bool f;
                float s = Support(g, c, h, caps.stepUp, 1.0f, caps.headroom, out f);
                if (f && s < h - caps.stepDown - 1e-3f)
                {
                    bool pf, pw, ph;
                    float pfY = FloorBelow(g, nc + dir * (k - 1), h - caps.stepDown, out pf, out pw, out ph);
                    if (pf && Math.Abs(pfY - s) < 0.3f) f = false;
                }
                if (!f && BandSolid(g, c, h + caps.stepUp, h + caps.headroom))
                {
                    s = WallTop(g, c, h, caps.stepUp, caps.jumpReach, caps.headroom, out f);
                    if (!f) return false;
                }
                if (!f)
                {
                    if (Unknown(g, c, h - 2f, h + 2f)) return false;
                    continue;
                }
                if (w + 0.2f > caps.GapReach(h, s)) return false;
                landCol = c; landY = s; width = w;
                return true;
            }
        }

        // the route from feet (x, feetY) to goalX over the slice. hasY + goalY: the goal height, used for a ladder at the
        // goal and to allow deep water when the goal is in it.
        internal static NavRoute Plan(GridSlice g, float x, float feetY, float goalX, bool hasY, float goalY, NavCaps caps)
        {
            int dir = goalX >= x ? 1 : -1;
            var r = new NavRoute { dir = dir, endX = x, endY = feetY };
            int c = g.ColOf(x);
            if (c < 0 || c >= g.cols) return End(r, "open", "outside", x, feetY);
            float h = feetY;
            bool f;
            float s0 = Support(g, c, h, 0.45f, 0.45f, caps.headroom * 0.5f, out f);
            if (f) h = s0;
            int gc = g.ColOf(goalX);
            int goalRow = hasY ? g.RowOf(goalY + 0.3f) : -1;
            bool goalWater = hasY && g.At(gc, goalRow) == '~';
            int guard = 0;
            while (guard++ < g.cols + 4)
            {
                if (c == gc) return Arrive(g, r, goalX, h, hasY, goalY, caps);
                int nc = c + dir;
                if (nc < 0 || nc >= g.cols) return End(r, "open", "slice_end", EdgeX(g, nc, dir), h);
                if (r.steps.Count >= MaxSteps) return End(r, "open", "route_cap", EdgeX(g, nc, dir), h);
                float ex = EdgeX(g, nc, dir);
                if (Unknown(g, nc, h - 2f, h + 2f)) { r.unknownAhead = true; return End(r, "open", "not_loaded", ex, h); }
                if (BandHas(g, nc, h + 0.05f, h + caps.height, '^')) return End(r, "block", "hazard", ex, h);
                if (BandSolid(g, nc, h + caps.stepUp, h + caps.headroom))
                {
                    bool wf;
                    float top = WallTop(g, nc, h, caps.stepUp, Math.Max(caps.jumpReach, caps.climbAuto), caps.headroom, out wf);
                    if (!wf) return End(r, "block", "wall", ex, h);
                    float wh = top - h;
                    if (wh <= caps.climbAuto)
                    {
                        if (!caps.climb) return End(r, "block", "obstacle", ex, h);
                        Add(r, "climb", ex, h, ex + dir * 0.3f, top, wh, null);
                    }
                    else if (wh <= caps.jumpReach && caps.canJump)
                    {
                        if (!caps.climb || !caps.jumpGaps) return End(r, "block", "ledge_up", ex, h);
                        Add(r, "jump_climb", ex, h, ex + dir * 0.3f, top, wh, null);
                    }
                    else return End(r, "block", "wall", ex, h);
                    h = top; c = nc;
                    continue;
                }
                bool sf;
                float sup = Support(g, nc, h, caps.stepUp, caps.stepDown, caps.headroom, out sf);
                if (sf)
                {
                    if (WaterDepth(g, nc, sup) > caps.wadeDepth)
                    {
                        if (goalWater) { Add(r, "swim", ex, h, goalX, goalY, 0f, null); return End(r, "arrive", null, goalX, goalY); }
                        return End(r, "block", "water", ex, h);
                    }
                    h = sup; c = nc;
                    continue;
                }
                bool ff, fw, fh;
                float floorY = FloorBelow(g, nc, h - caps.stepDown, out ff, out fw, out fh);
                float drop = ff ? h - floorY : float.PositiveInfinity;
                int lc = -1; float landY = 0f, gw = 0f;
                bool jumpable = caps.canJump && FindLanding(g, nc, dir, h, caps, out lc, out landY, out gw);
                bool safe = ff && !fw && !fh && drop <= caps.safeDrop;
                if (jumpable && gw <= caps.walkGap + 1e-4f && landY <= h + caps.stepUp && landY >= h - caps.stepDown)
                {
                    h = landY; c = lc;
                    continue;
                }
                if (jumpable && caps.jumpGaps && !(safe && drop <= caps.climbAuto))
                {
                    float le = EdgeX(g, lc, dir);
                    Add(r, "jump_gap", ex, h, le + dir * 0.3f, landY, gw, null);
                    h = landY; c = lc;
                    continue;
                }
                if (safe)
                {
                    Add(r, "drop", ex, h, ex + dir * 0.3f, floorY, drop, null);
                    h = floorY; c = nc;
                    continue;
                }
                if (fw)
                {
                    if (goalWater) { Add(r, "swim", ex, h, goalX, goalY, 0f, null); return End(r, "arrive", null, goalX, goalY); }
                    return End(r, "block", "water", ex, h);
                }
                if (fh) return End(r, "block", "hazard", ex, h);
                if (!ff) return End(r, "block", "pit", ex, h);
                return End(r, "block", jumpable ? "pit" : "ledge_down", ex, h);
            }
            return End(r, "open", "guard", x, feetY);
        }

        static NavRoute Arrive(GridSlice g, NavRoute r, float goalX, float h, bool hasY, float goalY, NavCaps caps)
        {
            if (hasY && Math.Abs(goalY - h) > 0.6f)
            {
                int lc = LadderNear(g, goalX, Math.Min(h, goalY), Math.Max(h, goalY), 0.8f);
                if (lc >= 0)
                {
                    float lx = g.CellX(lc);
                    Add(r, "ladder", lx, h, lx, goalY, goalY - h, null);
                    return End(r, "arrive", null, lx, goalY);
                }
                r.yIgnored = true;
            }
            return End(r, "arrive", null, goalX, h);
        }

        // a column with ladder cells within reach metres of x whose ladder reaches from lo to hi (1 m slack at both ends)
        internal static int LadderNear(GridSlice g, float x, float lo, float hi, float reach)
        {
            int best = -1;
            float bestD = float.MaxValue;
            int c0 = Math.Max(g.ColOf(x - reach), 0), c1 = Math.Min(g.ColOf(x + reach), g.cols - 1);
            for (int c = c0; c <= c1; c++)
            {
                float yMin = float.MaxValue, yMax = float.MinValue;
                for (int r = 0; r < g.rows; r++) if (g.At(c, r) == 'H') { yMin = Math.Min(yMin, g.CellY(r)); yMax = Math.Max(yMax, g.CellY(r)); }
                if (yMin > yMax || yMin > lo + 1.0f || yMax < hi - 1.0f) continue;
                float d = Math.Abs(g.CellX(c) - x);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        // grabbable corners of the slice: a solid top whose neighbour on one side is open with at least 0.5 m to hang in
        internal static List<NavLedge> GridLedges(GridSlice g, NavCaps caps)
        {
            var l = new List<NavLedge>();
            int hang = Math.Max(1, (int)Math.Ceiling(0.5f / g.cell - 1e-4));
            int head = Math.Max(1, (int)Math.Ceiling(caps.headroom / g.cell - 1e-4));
            for (int c = 0; c < g.cols; c++)
                for (int r = 0; r < g.rows - 1; r++)
                {
                    if (!Solid(g, c, r) || Solid(g, c, r + 1) || g.At(c, r + 1) == '?') continue;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        int n = c + side;
                        if (n < 0 || n >= g.cols) continue;
                        if (g.At(n, r) == '#' || g.At(n, r) == '?' || g.At(n, r + 1) == '#') continue;
                        bool room = true;
                        for (int k = 1; k <= hang; k++) { char ch = g.At(n, r - k); if (r - k < 0 || ch == '#' || ch == '?') { room = false; break; } }
                        if (!room) continue;
                        bool climb = true;
                        for (int k = 1; k <= head; k++) if (r + k < g.rows && Solid(g, c, r + k)) { climb = false; break; }
                        l.Add(new NavLedge { x = side < 0 ? g.originX + c * g.cell : g.originX + (c + 1) * g.cell, y = g.TopY(r), side = side < 0 ? "left" : "right", climbable = climb, source = "grid" });
                    }
                }
            return l;
        }

        // ledges of a (exact, game) and b (grid) within radius of (cx, cy), duplicates of a in b dropped, nearest first
        internal static List<NavLedge> MergeLedges(List<NavLedge> a, List<NavLedge> b, float cx, float cy, float radius, int max)
        {
            var all = new List<NavLedge>();
            if (a != null) all.AddRange(a);
            if (b != null)
                foreach (var e in b)
                {
                    bool dup = false;
                    if (a != null) foreach (var q in a) if (q.side == e.side && Math.Abs(q.x - e.x) < 0.3f && Math.Abs(q.y - e.y) < 0.3f) { dup = true; break; }
                    if (!dup) all.Add(e);
                }
            var res = new List<NavLedge>();
            foreach (var e in all) if (Dist(e, cx, cy) <= radius) res.Add(e);
            res.Sort((p, q) => Dist(p, cx, cy).CompareTo(Dist(q, cx, cy)));
            if (res.Count > max) res.RemoveRange(max, res.Count - max);
            return res;
        }

        static float Dist(NavLedge e, float cx, float cy) { return (float)Math.Sqrt((e.x - cx) * (e.x - cx) + (e.y - cy) * (e.y - cy)); }

        // grid size for a request: cell grows (0.05 m steps) until cols <= maxCols and rows <= maxRows
        internal static float FitCell(float width, float height, float cell, int maxCols, int maxRows)
        {
            float c = Math.Max(cell, 0.05f);
            while (Math.Ceiling(width / c - 1e-4) > maxCols || Math.Ceiling(height / c - 1e-4) > maxRows) c += 0.05f;
            return (float)Math.Round(c, 3);
        }
    }
}
