using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Paris.Engine.Graphics.Playfield;
using Paris.Engine.Physic;
using Paris.Engine.Scene;
using Paris.Game.Actor;

namespace CosmicBots
{
    /// <summary>
    /// walkable-area awareness.
    ///  * Camera clamp: the game pushes every body with CollisionFlag.CAMERA inside camera.GetCollisionBounds()
    ///    (BeatEmUpCamera = bottom ScreenYNavigatableEstateFromBottom of the screen) inset by the body's
    ///    CameraBoundsOffsetHorizontal/Vertical, in screen space (Y - floor height). Goals are clamped inside that with a margin.
    ///  * Walls: Playfield heightmap tiles. A spot is blocked if any corner of our collision rect is outside the collision
    ///    layer, an empty (void) tile, a WALL/PIT tile, or a floor more than a step (8 px) above the reachable height.
    ///  * Per-axis stuck detection as a safety net when the map disagrees with us.
    /// </summary>
    public partial class BotController
    {
        private const float CAM_MARGIN = 10f, STEP_H = 8f;
        private const float STUCK_TIME = 0.28f, STUCK_EPS = 1f, BLOCK_TIME = 1.8f, BLOCK_TIME_COMBAT = 0.6f;

        // last frame's final (post-filter) input, for stuck detection
        private Vector2 _lastOut; private bool _lastOutSteer, _moveFromSteer, _lastOutEngaging;
        // per-axis push tracking + blocked memory
        private int _pushSX, _pushSY; private float _pushTX, _pushTY, _pushStartX, _pushStartY;
        private int _blkSX, _blkSY; private float _blkUntilX, _blkUntilY, _blkPosX, _blkPosY, _blkGoalX, _blkGoalY;
        // enemies we could not reach (wall-stuck while engaging / goal unwalkable)
        // one entry per enemy (was a single slot, so two enemies across a hole were re-marked alternately every frame)
        private readonly Dictionary<Enemy, float> _unreach = new Dictionary<Enemy, float>();
        private readonly List<Enemy> _unreachPrune = new List<Enemy>();
        private float _unreachWantT = -100f, _slotNoneT = -100f, _largeEngageT = -100f;   // last time the bot wanted to mark its target unreachable / had no valid slot
        private bool _clampMoved, _steerEngaging;

        private bool IsUnreachable(Enemy e) => e != null && _unreach.TryGetValue(e, out float t) && BotManager.Time < t;
        private void MarkUnreachable(Enemy e, float secs)
        {
            if (e == null) return;
            // bosses / big targets are never "unreachable": MakeReachable already walks us to the nearest reachable
            // spot next to them (Master Mold's arena used to trip the stranded detector this way)
            // but the part we aim at may be walled off: try another part of it for a while
            if (IsLargeTarget(e)) { _largeEngageT = BotManager.Time; if (ReferenceEquals(_partEnemy, e)) MarkPartBad(2.0f); return; }
            _unreachWantT = BotManager.Time;
            if (IsUnreachable(e)) return;                       // already skipped: keep its original 2 s, no re-mark / re-count
            if (_unreach.Count > 24) PruneUnreachable();
            _unreach[e] = BotManager.Time + secs; Stats.Add("unreachable-target");
        }
        private void PruneUnreachable()
        {
            _unreachPrune.Clear();
            foreach (var kv in _unreach) if (BotManager.Time >= kv.Value || kv.Key == null) _unreachPrune.Add(kv.Key);
            foreach (Enemy e in _unreachPrune) _unreach.Remove(e);
        }

        /// <summary>Is this ground spot one our body can stand on, at roughly our reachable height? (true when unknown)</summary>
        internal static bool SpotWalkable(Player me, Vector2 spot, float refFloor)
        {
            try
            {
                Scene2d s = Scene2d.Active; Playfield pf = s?.Playfield;
                RigidBody b = me.BodyComponent as RigidBody;
                if (pf == null || b == null || pf.CollisionLayer == null || pf.Heightmap == null || !pf.Is3D) return true;
                Rectangle r = b.CollisionRect; r.Offset((int)spot.X, (int)spot.Y);
                Rectangle layer = pf.CollisionLayer.GetLayerRect();
                if (!layer.Contains(r)) return false;
                return TileOk(pf, new Vector2(r.Left, r.Top), refFloor) && TileOk(pf, new Vector2(r.Right - 1, r.Top), refFloor) &&
                       TileOk(pf, new Vector2(r.Left, r.Bottom - 1), refFloor) && TileOk(pf, new Vector2(r.Right - 1, r.Bottom - 1), refFloor) &&
                       TileOk(pf, new Vector2(r.Center.X, r.Center.Y), refFloor);
            }
            catch { return true; }
        }

        private static bool TileOk(Playfield pf, Vector2 p, float refFloor)
        {
            HeightmapInfo h = pf.GetHeightAtPos(p);
            if (h == null || ReferenceEquals(h, HeightmapInfo.Empty)) return false;   // void (the engine treats it as a wall)
            if (ReferenceEquals(h, HeightmapInfo.Invalid)) return true;               // no data -> don't judge
            if ((h.Flag & (PlayfieldFlag.WALL | PlayfieldFlag.PIT)) != 0) return false;
            if ((h.Flag & PlayfieldFlag.FLOOR) == 0) return false;
            return h.Height <= refFloor + STEP_H;
        }

        internal static float FloorAt(Player me, Vector2 spot, float fallback)
        {
            try
            {
                Playfield pf = Scene2d.Active?.Playfield;
                if (pf == null || !pf.Is3D) return fallback;
                HeightmapInfo h = pf.GetHeightAtPos(spot);
                if (h == null || ReferenceEquals(h, HeightmapInfo.Invalid) || ReferenceEquals(h, HeightmapInfo.Empty) || h.Height >= short.MaxValue) return fallback;
                return Math.Max(0f, h.Height);
            }
            catch { return fallback; }
        }

        /// <summary>Clamp a ground goal inside the game's camera collision box (same math as RigidBody.DoCameraCollision) with a margin.</summary>
        internal static Vector2 ClampToCamera(Player me, Vector2 spot, float floor)
        {
            try
            {
                GameCamera2d cam = Scene2d.Active?.CurrentCamera;
                RigidBody b = me.BodyComponent as RigidBody;
                if (cam == null || b == null || !cam.CameraCollisionsEnabled || (b.CollisionMask & CollisionFlag.CAMERA) == 0) return spot;
                Rectangle cb = cam.GetCollisionBounds();
                if (cb.Width <= 0 || cb.Height <= 0) return spot;
                Rectangle cr = b.CollisionRect;
                float l = cb.Left + b.CameraBoundsOffsetHorizontal + CAM_MARGIN - cr.X;
                float r = cb.Right - b.CameraBoundsOffsetHorizontal - CAM_MARGIN - (cr.X + cr.Width);
                if (l <= r) spot.X = MathHelper.Clamp(spot.X, l, r);
                // screen Y = Y - floor + rect.Y
                float t = cb.Top + b.CameraBoundsOffsetVertical + CAM_MARGIN - cr.Y + floor;
                float bt = cb.Bottom - b.CameraBoundsOffsetVertical - CAM_MARGIN - (cr.Y + cr.Height) + floor;
                bool top = b.CollideWithCameraBoundaryTop, bot = b.CollideWithCameraBoundaryBottom;
                if (top && bot && t > bt) spot.Y = (t + bt) * 0.5f;            // band thinner than the margins
                else { if (top && spot.Y < t) spot.Y = t; if (bot && spot.Y > bt) spot.Y = bt; }
            }
            catch { }
            return spot;
        }

        // fallback-slot hysteresis. The chosen candidate kind sticks until it has been invalid for > 0.3 s; better
        // kinds are only re-tried every 0.3 s (not every frame), so slots don't flip sides/lanes frame to frame.
        private const float RM_BAD_TIME = 0.3f, RM_EVAL_TIME = 0.3f;
        private const int RM_NONE = 8;
        private int _rmKind = -1; private float _rmBadT, _rmEvalT;
        private static readonly string[] RmStat = { null, "slot-collapsed", "slot-collapsed", "slot-collapsed", "slot-swapped", "slot-swapped", "slot-collapsed", "slot-collapsed", "slot-none" };

        /// <summary>Candidate goal of a fallback kind: 0 = goal (camera-clamped), 1-3 collapse depth toward the anchor lane,
        /// 4-5 mirrored X side, 6-7 shrink X toward the anchor in its lane, 8 = stay put.</summary>
        private static Vector2 ReachCand(Player me, int kind, Vector2 goal, Vector2 anchor, Vector2 pos, float myFloor)
        {
            Vector2 d = goal - anchor;
            switch (kind)
            {
                case 0: return ClampToCamera(me, goal, FloorAt(me, goal, myFloor));
                case 1: case 2: case 3: return ClampToCamera(me, new Vector2(goal.X, anchor.Y + d.Y * (3 - kind) / 3f), myFloor);
                case 4: return ClampToCamera(me, new Vector2(anchor.X - d.X, goal.Y), myFloor);
                case 5: return ClampToCamera(me, new Vector2(anchor.X - d.X, anchor.Y), myFloor);
                case 6: case 7: return ClampToCamera(me, new Vector2(anchor.X + d.X * (8 - kind) / 3f, anchor.Y), myFloor);
                default: return pos;
            }
        }

        /// <summary>
        /// Make a goal reachable: camera clamp, then if the spot is in a wall try collapsing it toward <paramref name="anchor"/>
        /// (the human's lane for formation), the mirrored X side, the anchor itself, finally stay put. Sticky (see above).
        /// </summary>
        private Vector2 MakeReachable(Player me, Vector2 goal, Vector2 anchor, Vector2 pos)
        {
            float myFloor = 0f; try { myFloor = me.BodyComponent != null ? me.BodyComponent.FloorHeightIgnoringPits : 0f; } catch { }
            _rmEvalT -= _lastDt;
            if (_rmKind >= 0)
            {
                Vector2 cur = ReachCand(me, _rmKind, goal, anchor, pos, myFloor);
                bool ok = _rmKind == RM_NONE || Reach(me, cur, pos, myFloor);
                _rmBadT = ok ? 0f : _rmBadT + _lastDt;
                bool tryBetter = _rmKind > 0 && _rmEvalT <= 0f;
                if ((ok || _rmBadT <= RM_BAD_TIME) && !tryBetter) return RmResult(cur, goal);
                _rmEvalT = RM_EVAL_TIME;
                int limit = ok ? _rmKind : RM_NONE;   // still valid: only switch to a better kind
                for (int k = 0; k < limit; k++)
                {
                    if (!ok && k == _rmKind) continue;
                    Vector2 c = ReachCand(me, k, goal, anchor, pos, myFloor);
                    if (Reach(me, c, pos, myFloor)) { RmSwitch(k); return RmResult(c, goal); }
                }
                if (ok) return RmResult(cur, goal);
                RmSwitch(RM_NONE); return RmResult(pos, goal);
            }
            _rmEvalT = RM_EVAL_TIME;
            for (int k = 0; k < RM_NONE; k++)
            {
                Vector2 c = ReachCand(me, k, goal, anchor, pos, myFloor);
                if (Reach(me, c, pos, myFloor)) { RmSwitch(k); return RmResult(c, goal); }
            }
            RmSwitch(RM_NONE); return RmResult(pos, goal);
        }

        private void RmSwitch(int k)
        {
            if (k != _rmKind && RmStat[k] != null) Stats.Add(RmStat[k]);
            _rmKind = k; _rmBadT = 0f;
        }

        private Vector2 RmResult(Vector2 c, Vector2 goal)
        {
            if (_rmKind == RM_NONE)   // no valid goal (stranded detector); not while fighting a boss / big target
            {
                if (_steerEngaging && IsLargeTarget(RegTarget)) { _largeEngageT = BotManager.Time; Suppress("no-slot"); } else _slotNoneT = BotManager.Time;
            }
            _clampMoved = _rmKind != 0 || Vector2.DistanceSquared(c, goal) > 4f;
            return c;
        }

        /// <summary>standable AND the straight walk there doesn't cross a hole.</summary>
        private static bool Reach(Player me, Vector2 spot, Vector2 from, float floor) => SpotWalkable(me, spot, floor) && !PathHasPit(me, from, spot);

        /// <summary>Per-axis stuck detection on last frame's input. Call once per Think with the current ground position.</summary>
        private void UpdateWallStuck(Player me, Vector2 pos, bool onGround, bool flying, bool hurt, float now)
        {
            bool eligible = _lastOutSteer && onGround && !flying && !hurt && !PlanBusy && _forcedMoveT <= 0f && (_lastState == "StateWalk" || _lastState == "StateSprint");
            AxisStuck(eligible, Math.Sign(_lastOut.X), pos.X, ref _pushSX, ref _pushTX, ref _pushStartX, ref _blkSX, ref _blkUntilX, ref _blkPosX, ref _blkGoalX, _goalSmooth.X, true, now);
            AxisStuck(eligible, Math.Sign(_lastOut.Y), pos.Y, ref _pushSY, ref _pushTY, ref _pushStartY, ref _blkSY, ref _blkUntilY, ref _blkPosY, ref _blkGoalY, _goalSmooth.Y, false, now);
        }

        private float _lastDt = 1f / 60f;

        private void AxisStuck(bool eligible, int s, float p, ref int pushS, ref float pushT, ref float start, ref int blkS, ref float blkUntil, ref float blkPos, ref float blkGoal, float goal, bool isX, float now)
        {
            if (!eligible || s == 0) { pushS = 0; pushT = 0f; return; }
            if (s != pushS) { pushS = s; pushT = 0f; start = p; return; }
            if (Math.Abs(p - start) >= STUCK_EPS) { pushT = 0f; start = p; return; }
            pushT += _lastDt;
            if (pushT < STUCK_TIME) return;
            // blocked on this axis
            blkS = s; blkPos = p; blkGoal = goal;
            blkUntil = now + (_lastOutEngaging ? BLOCK_TIME_COMBAT : BLOCK_TIME);
            pushS = 0; pushT = 0f;
            Stats.WallStuck++;
            if (isX) { _arrX = true; _goalSmooth.X = p; } else { _arrY = true; _goalSmooth.Y = p; }
            if (_lastOutEngaging && RegTarget != null) MarkUnreachable(RegTarget, 2.0f);
        }

        /// <summary>Remove pushes into a remembered wall. Extends the block while the goal is still beyond it and nothing changed.</summary>
        private float ApplyBlock(float comp, float pos, float goal, ref int blkS, ref float blkUntil, float blkPos, float blkGoal, bool engaging, float now)
        {
            if (blkS == 0) return comp;
            if (now >= blkUntil)
            {
                bool same = Math.Abs(pos - blkPos) < 4f && Math.Abs(goal - blkGoal) < 24f && Math.Sign(goal - pos) == blkS;
                if (same && !engaging) blkUntil = now + BLOCK_TIME;   // nothing changed: don't re-bump the wall
                else { blkS = 0; return comp; }
            }
            if (Math.Abs(pos - blkPos) > 12f) { blkS = 0; return comp; }  // we moved away (e.g. slid along the wall)
            return Math.Sign(comp) == blkS ? 0f : comp;
        }
    }
}
