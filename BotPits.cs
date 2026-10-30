using System;
using Microsoft.Xna.Framework;
using Paris.Engine.Graphics.Playfield;
using Paris.Engine.Physic;
using Paris.Engine.Scene;
using Paris.Game.Actor;

namespace CosmicBots
{
    /// <summary>
    /// v0.2.4: pit awareness. Holes in levels are collision tiles with mask PlayfieldFlag.PIT; Playfield.GenerateHeightMap
    /// turns them into heightmap cells with Flag = PIT and Height = HeightmapInfo.PIT_BOTTOM_HEIGHT (-768). RigidBody marks a
    /// body IsInsidePit when its centre tile is PIT and it sinks below PitDetectionHeight; the game only stops this for
    /// ground attacks / dodges / air attacks (Player.PreventPitFalloff), NOT for walking, sprinting or jumping.
    /// Generic: any level's PIT cells (or any cell far below the floor) count as a hole.
    /// </summary>
    public partial class BotController
    {
        private const float PIT_STEP = 6f, DEEP_HOLE = -128f;
        private static object _scannedPf; private static bool _levelHasPits;
        private bool _pitPrevX, _pitPrevY;

        /// <summary>Pit avoidance is permanently off (bots ignore holes; a fall respawns them next to the human).</summary>
        internal static bool PitsOn => false;
        internal static bool LevelHasPits => PitsOn;
        /// <summary>v0.2.9: the level really has holes (regardless of Avoid holes on/off).</summary>
        internal static bool LevelHasHoles => _levelHasPits;

        /// <summary>Once per playfield: find out whether this level has pit cells and log it.</summary>
        internal static void ScanLevelPits(Scene2d scene)
        {
            Playfield pf = scene?.Playfield;
            if (pf == null || ReferenceEquals(pf, _scannedPf)) return;
            _scannedPf = pf; _levelHasPits = false;
            try
            {
                HeightmapInfo[][] hm = pf.Heightmap;
                if (!pf.Is3D || hm == null || pf.CollisionLayer == null) { Log.Verbose($"[level] '{scene.Name}': no 3D heightmap -> pit avoidance off"); return; }
                int pits = 0, deep = 0, cells = 0, rows = hm.Length, cols = 0;
                foreach (HeightmapInfo[] row in hm)
                {
                    if (row == null) continue; cols = Math.Max(cols, row.Length);
                    foreach (HeightmapInfo h in row)
                    {
                        if (h == null) continue; cells++;
                        if ((h.Flag & PlayfieldFlag.PIT) != 0) pits++;
                        else if (h.Height <= DEEP_HOLE) deep++;
                    }
                }
                _levelHasPits = pits + deep > 0;
                Log.Verbose($"[level] '{scene.Name}': heightmap {cols}x{rows} (tile {pf.CollisionLayer.TileScale}px), pit cells={pits}, deep-hole cells={deep}" +
                         (_levelHasPits ? " -> holes present" : " -> no pits"));
            }
            catch (Exception ex) { Log.Warn("[level] pit scan failed: " + ex.Message); }
        }

        internal static bool PitAt(Vector2 p)
        {
            try
            {
                Playfield pf = Scene2d.Active?.Playfield;
                if (pf == null) return false;
                HeightmapInfo h = pf.GetHeightAtPos(p);
                if (h == null || ReferenceEquals(h, HeightmapInfo.Invalid) || ReferenceEquals(h, HeightmapInfo.Empty)) return false;
                return (h.Flag & PlayfieldFlag.PIT) != 0 || h.Height <= DEEP_HOLE;
            }
            catch { return false; }
        }

        /// <summary>Does our body footprint at <paramref name="spot"/> touch a hole?</summary>
        internal static bool RectHasPit(Player me, Vector2 spot)
        {
            if (!PitsOn) return false;
            Rectangle r; try { r = me.BodyComponent.CollisionRect; } catch { return PitAt(spot); }
            r.Offset((int)spot.X, (int)spot.Y);
            return PitAt(new Vector2(r.Center.X, r.Center.Y)) || PitAt(new Vector2(r.Left, r.Top)) || PitAt(new Vector2(r.Right - 1, r.Top)) ||
                   PitAt(new Vector2(r.Left, r.Bottom - 1)) || PitAt(new Vector2(r.Right - 1, r.Bottom - 1));
        }

        /// <summary>Does walking from a to b cross a hole (footprint sampled along the segment)?</summary>
        internal static bool PathHasPit(Player me, Vector2 a, Vector2 b)
        {
            if (!PitsOn) return false;
            float len = Vector2.Distance(a, b);
            int n = Math.Min(24, Math.Max(1, (int)(len / 12f)));
            bool onEdge = RectHasPit(me, a);   // already touching the rim: let the first ~20 px (stepping away) through
            for (int i = 1; i <= n; i++)
            {
                if (onEdge && len * i / n < 20f) continue;
                if (RectHasPit(me, Vector2.Lerp(a, b, i / (float)n))) return true;
            }
            return false;
        }

        /// <summary>Leading edge of the footprint moved <paramref name="d"/> px along dir: any hole there?</summary>
        private static bool LeadingEdgePit(Player me, Vector2 pos, int sx, int sy, float look)
        {
            Rectangle r; try { r = me.BodyComponent.CollisionRect; } catch { r = new Rectangle(-8, -4, 16, 8); }
            r.Offset((int)pos.X, (int)pos.Y);
            float ex = sx > 0 ? r.Right - 1 : sx < 0 ? r.Left : r.Center.X;
            float ey = sy > 0 ? r.Bottom - 1 : sy < 0 ? r.Top : r.Center.Y;
            Vector2 dir = new Vector2(sx, sy); if (sx != 0 && sy != 0) dir.Normalize();
            for (float d = PIT_STEP; d <= look + 0.01f; d += PIT_STEP)
            {
                Vector2 o = dir * d;
                if (sx != 0 && sy == 0)
                { if (PitAt(new Vector2(ex + o.X, r.Top)) || PitAt(new Vector2(ex + o.X, r.Center.Y)) || PitAt(new Vector2(ex + o.X, r.Bottom - 1))) return true; }
                else if (sy != 0 && sx == 0)
                { if (PitAt(new Vector2(r.Left, ey + o.Y)) || PitAt(new Vector2(r.Center.X, ey + o.Y)) || PitAt(new Vector2(r.Right - 1, ey + o.Y))) return true; }
                else if (PitAt(new Vector2(ex + o.X, ey + o.Y)) || PitAt(new Vector2(r.Center.X + o.X, r.Center.Y + o.Y))) return true;
            }
            return false;
        }

        /// <summary>
        /// Final per-frame guard on the move input (after FilterMove): look ~1-2 steps ahead on each axis (a whole jump's
        /// travel when airborne or jumping) and drop any axis that would step into a hole, so the bot slides along the edge.
        /// Flying (or about to fly) bots are exempt.
        /// </summary>
        private void PitGuard()
        {
            Player me = ActivePlayer;
            if (!PitsOn || me == null || _move == Vector2.Zero) { _pitPrevX = _pitPrevY = false; return; }
            if (_pitGuardOff) { _pitPrevX = _pitPrevY = false; return; }   // v0.2.7: this bot is gap-jumping / walking into the hole on purpose
            try
            {
                if (me.IsFlying || _flightEntering || me.IsKO) { _pitPrevX = _pitPrevY = false; return; }
                bool air = !me.IsOnGround || _down.Contains(A_JUMP);
                float ws = 0f; try { ws = me.CurrentWalkSpeed; } catch { }
                float look = air ? MathHelper.Clamp(ws * 0.6f, 30f, 100f) : MathHelper.Clamp(ws * 0.2f, 12f, 40f);
                Vector2 pos = me.Position2D;
                if (_inSprint && _sprintOwned && !air)
                {
                    // v0.2.5: sprint covers ~2x walk speed and the game has no pit fall-off guard in StateSprint,
                    // so look a whole stop distance ahead and leave the sprint (depth-only input -> walk) before the rim.
                    float rs = RunSpeed(me);
                    float sl = MathHelper.Clamp(rs * 0.45f, 40f, 110f);
                    if (LeadingEdgePit(me, pos, _sprintDir, 0, sl))
                    {
                        float sy0 = Math.Sign(_sprintGoalY - pos.Y); if (sy0 == 0f) sy0 = 1f;
                        if (LeadingEdgePit(me, pos, 0, (int)sy0, 16f)) sy0 = -sy0;
                        _move = LeadingEdgePit(me, pos, 0, (int)sy0, 16f) ? new Vector2(-_sprintDir, 0f) : new Vector2(0f, sy0 * 0.5f);
                        _sprintCd = 2f; _sprintWant = false;
                        if (!_pitPrevX) Stats.PitAvoid++;
                        _pitPrevX = true;
                        return;
                    }
                    look = Math.Max(look, sl);
                }
                int sx = Math.Sign(_move.X), sy = Math.Sign(_move.Y);
                bool bx = sx != 0 && LeadingEdgePit(me, pos, sx, 0, look);
                bool by = sy != 0 && LeadingEdgePit(me, pos, 0, sy, look);
                if (!bx && !by && sx != 0 && sy != 0 && LeadingEdgePit(me, pos, sx, sy, look))
                { if (Math.Abs(_move.X) < Math.Abs(_move.Y)) bx = true; else by = true; }
                if (bx) _move.X = 0f;
                if (by) _move.Y = 0f;
                if ((bx && !_pitPrevX) || (by && !_pitPrevY)) Stats.PitAvoid++;
                _pitPrevX = bx; _pitPrevY = by;
                if (bx || by)
                {
                    // hold still on that axis at the edge (same idea as wall-stuck)
                    if (bx) { _arrX = true; _goalSmooth.X = pos.X; }
                    if (by) { _arrY = true; _goalSmooth.Y = pos.Y; }
                }
            }
            catch { }
        }
    }
}
