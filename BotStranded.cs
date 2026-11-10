using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Paris.Engine.Data;
using Paris.Engine.Scene;
using Paris.Game.Actor;
using Paris.Game.Data;

namespace CosmicBots
{
    /// <summary>
    /// stranded recovery. A bot cut off from the human by a hole (or making no progress toward a far human)
    /// recovers in this order:
    ///  a) gap jump - the jump arc is computed from the game's own numbers (GameSettings.JumpImpulse / SuperJumpImpulseSprint
    ///     / JumpGravityAcceleration, Player.GravityAcceleration, CharacterInfo.DoubleJumpImpulse, Player.CurrentWalkSpeed:
    ///     StateJump sets the horizontal speed to the WALK speed, also for sprint jumps, which only get the higher impulse and
    ///     cannot double jump). Flyers fly across and land on solid ground.
    ///  b) sacrifice - walk into the hole. The game (Player.StateFallInHole) costs GameSettings.FallInHoleDamage HP after
    ///     FallInHoleDuration and respawns at the player's ClimbOutPosition, which is the bot's OWN last floor spot (same
    ///     side of the hole!) unless we point it at the human's ClimbOutPosition first. Only when the hero survives the fall
    ///     (a KO'd last hero would mean a continue in Arcade, which are shared).
    ///  c) respawn - the game's own "crushed by the camera" recovery: TeleportToClimbOutPosition(forceGroundLevel, drop from
    ///     GameResolution.Y) at the human's ClimbOutPosition, then StateFall. No HP cost.
    /// </summary>
    public partial class BotController
    {
        private const float STRAND_TIME = 2.0f, STRAND_FAR = 250f, STRAND_CHECK = 0.25f;
        private const float STRAND_NOPIT_TIME = 6.0f;   // "no progress" on a level without holes needs 6 s

        /// <summary>no-pit levels: truly stuck = off-screen and > 320 px from the human, or > 420 px anywhere.</summary>
        private static bool NoPitFar(Player me, Vector2 pos, float leaderDist)
        {
            if (leaderDist > 420f) return true;
            if (leaderDist <= 320f) return false;
            float fl = 0f; try { fl = me.BodyComponent != null ? me.BodyComponent.FloorHeightIgnoringPits : 0f; } catch { }
            Vector2 c = ClampToCamera(me, pos, fl);
            return Math.Abs(c.X - pos.X) > 24f || Math.Abs(c.Y - pos.Y) > 24f;
        }
        private const float STRAND_BLOCKED_TIME = 3.0f;
        private float _strandBlkT; private Vector2 _strandBlkAnchor; private bool _pitFallSeen, _pitFallRedirected;
        private float _strandCutT, _strandNoProgT, _strandBest, _strandCheckT, _srCd, _srT, _srLastEnd = -100f;
        private bool _strandCut, _pitGuardOff, _srPresetClimb;
        private int _srPhase, _srLevel, _srDir;   // phase: 0 none, 1 approach, 2 sprint run-up, 3 airborne, 4 fly, 5 sacrifice walk, 6 in the hole
        private string _srKind = "";                 // single / double / sprint / fly / sacrifice
        private float _srLaneY, _srRimX, _srLandX, _srStandX;

        private bool StrandAirborne => _srPhase == 3 || _srPhase == 4;
        internal bool Stranded => _srPhase != 0;

        private static float FallDamage() { try { return GameSettings.Singleton.FallInHoleDamage; } catch { return 1f; } }

        /// <summary>A hole fall only costs HP here if the current hero clearly survives it (no KO -> no continue / game over risk).</summary>
        private static bool FallSafe(Player me)
        {
            try { int hp = me.HP, max = Math.Max(1, me.MaxHP); return !me.IsKO && hp - FallDamage() >= Math.Max(1f, max * 0.1f); }
            catch { return false; }
        }

        // ------------------------------------------------------------ jump physics (decompiled StateJump / StartJump / DoDoubleJump)
        /// <summary>Horizontal distance (px) a jump covers landing at the take-off height. kind: single / double / sprint.</summary>
        internal static float JumpReach(Player me, string kind)
        {
            try
            {
                GameSettings gs = GameSettings.Singleton;
                float gU = Math.Max(1f, Math.Abs(gs.JumpGravityAcceleration)), gD = Math.Max(1f, Math.Abs(me.GravityAcceleration));
                float ws = Math.Max(1f, me.CurrentWalkSpeed);
                float v1 = kind == "sprint" ? gs.SuperJumpImpulseSprint : gs.JumpImpulse;
                float tUp = v1 / gU, peak = v1 * v1 / (2f * gU);
                float t = tUp + (float)Math.Sqrt(2f * peak / gD);
                if (kind == "double")
                {
                    float v2 = me.CurrentCharacterInfo != null ? me.CurrentCharacterInfo.DoubleJumpImpulse : 0f;
                    float td = DoubleJumpAt(me);
                    float z = v1 * td - 0.5f * gU * td * td;
                    float peak2 = z + v2 * v2 / (2f * gU);
                    t = td + v2 / gU + (float)Math.Sqrt(2f * peak2 / gD);
                }
                return ws * t;
            }
            catch { return 0f; }
        }

        /// <summary>When to tap the double jump: still in StateJump (rising) and inside ExtraJumpDoubleTapTime.</summary>
        private static float DoubleJumpAt(Player me)
        {
            try
            {
                GameSettings gs = GameSettings.Singleton;
                float rise = gs.JumpImpulse / Math.Max(1f, gs.JumpGravityAcceleration);
                return Math.Max(0.12f, Math.Min(Math.Min(rise - 0.06f, gs.ExtraJumpDoubleTapTime - 0.06f), 0.34f));
            }
            catch { return 0.3f; }
        }

        private static float MyFloor(Player me) { try { return me.BodyComponent != null ? me.BodyComponent.FloorHeightIgnoringPits : 0f; } catch { return 0f; } }
        private static float HalfWidth(Player me) { try { return Math.Max(4f, me.BodyComponent.CollisionRect.Width * 0.5f); } catch { return 8f; } }

        // ------------------------------------------------------------ detection
        /// <summary>Can we walk (straight, or an L via either corner) to the human or one of the formation slots without a hole?</summary>
        private bool CanReachHuman(Player me, Vector2 pos, Vector2 lg)
        {
            float fl = MyFloor(me);
            Vector2[] targets = { lg, lg + new Vector2(-40f, 0f), lg + new Vector2(40f, 0f), lg + new Vector2(-66f, -22f), lg + new Vector2(66f, 22f), lg + new Vector2(-92f, 0f), lg + new Vector2(92f, 0f) };
            foreach (Vector2 t in targets)
            {
                if (!SpotWalkable(me, t, fl) && t != lg) continue;
                if (!PathHasPit(me, pos, t)) return true;
                Vector2 c1 = new Vector2(pos.X, t.Y), c2 = new Vector2(t.X, pos.Y);
                if (SpotWalkable(me, c1, fl) && !PathHasPit(me, pos, c1) && !PathHasPit(me, c1, t)) return true;
                if (SpotWalkable(me, c2, fl) && !PathHasPit(me, pos, c2) && !PathHasPit(me, c2, t)) return true;
            }
            return false;
        }

        /// <summary>
        /// Called by Think every frame (before swaps / targeting). Returns true while a recovery drives the bot this frame.
        /// </summary>
        private bool UpdateStranded(Player me, CharacterInfo ci, Player leader, Vector2 lg, float leaderDist, Vector2 pos, bool onGround, bool flying, bool hurt, List<Foe> foes, float dt, float now)
        {
            Tick(ref _srCd, dt);
            if (_srPhase != 0) return RunRecovery(me, ci, leader, lg, pos, onGround, flying, hurt, dt, now);
            _pitGuardOff = false;

            bool leaderOk = leader != null && !leader.IsKO && leader.IsOnGround && GameRefl.StateName(leader) != "StateFallInHole";
            bool roomExit = false; try { roomExit = me.IsDoingRoomExit || (leader != null && leader.IsDoingRoomExit); } catch { }

            // 3) blocked: target unreachable / no valid goal and the bot has not got anywhere for 3 s - even with foes
            // around (an unreachable enemy across a hole used to veto 1) and 2) while the bots stood still).
            bool blockedCtx = (now - _unreachWantT < 0.5f || now - _slotNoneT < 0.5f) && now - _largeEngageT > 1.0f;
            // on levels without holes nothing can strand a bot except being stuck far away / off-screen
            bool farEnough = LevelHasHoles ? leaderDist > 100f : NoPitFar(me, pos, leaderDist);
            bool blkOk = leader != null && !leader.IsKO && !roomExit && !hurt && (onGround || flying) && GameRefl.StateName(me) != "StateFallInHole" && farEnough;
            if (!blkOk || Vector2.Distance(pos, _strandBlkAnchor) > 32f) { _strandBlkAnchor = pos; _strandBlkT = 0f; }
            else if (blockedCtx) _strandBlkT += dt;
            else _strandBlkT = Math.Max(0f, _strandBlkT - dt * 0.5f);
            if (_strandBlkT >= STRAND_BLOCKED_TIME && _srCd <= 0f)
            {
                _strandBlkT = 0f; _strandBlkAnchor = pos; _strandCutT = 0f; _strandNoProgT = 0f; _strandBest = leaderDist;
                Stat("stranded"); Stat("stranded-blocked");
                if (now - _srLastEnd < 20f) _srLevel = Math.Min(2, _srLevel + 1); else _srLevel = 0;
                Respawn(me, leader, "blocked 3 s (target unreachable / no valid goal)");   // bots never path around holes: drop in next to the human
                return false;
            }

            bool calm = leaderOk && !roomExit && onGround && !hurt && !PlanBusy && _def == null && !_specialHolding && !FoeNear(me, pos, foes, 90f, 30f, true) && _forcedMoveT <= 0f
                        && !me.IsInSpecialAttack && GameRefl.StateName(me) != "StateFallInHole";
            if (!calm) { _strandCutT = 0f; _strandNoProgT = 0f; _strandBest = leaderDist; return false; }

            // 1) cut off by a hole (checked a few times per second; the path checks sample the heightmap)
            _strandCheckT -= dt;
            if (_strandCheckT <= 0f)
            {
                _strandCheckT = STRAND_CHECK;
                _strandCut = LevelHasPits && PathHasPit(me, pos, lg) && !PitAt(lg) && !CanReachHuman(me, pos, lg);
            }
            if (_strandCut) _strandCutT += dt; else _strandCutT = 0f;
            // 2) far away and not getting closer
            if (leaderDist > STRAND_FAR && (LevelHasHoles || NoPitFar(me, pos, leaderDist)) && !FoeNear(me, pos, foes, 200f, 80f, false))
            {
                if (leaderDist < _strandBest - 24f) { _strandBest = leaderDist; _strandNoProgT = 0f; }
                else _strandNoProgT += dt;
            }
            else { _strandBest = leaderDist; _strandNoProgT = 0f; }

            bool stranded = _strandCutT >= STRAND_TIME || _strandNoProgT >= (LevelHasHoles ? STRAND_TIME : STRAND_NOPIT_TIME);
            if (!stranded || _srCd > 0f) return false;

            string why = _strandCutT >= STRAND_TIME ? "hole" : "no progress";
            _strandCutT = 0f; _strandNoProgT = 0f; _strandBest = leaderDist;
            Stat("stranded");
            if (now - _srLastEnd < 20f) _srLevel = Math.Min(2, _srLevel + 1); else _srLevel = 0;   // still stranded right after a try: escalate
            Respawn(me, leader, why);   // bots never path around holes: drop in next to the human
            return false;
        }

        /// <summary>Any foe within (rx, ry)? reachableOnly: ignore foes across a hole (the bot cannot fight those anyway).</summary>
        private static bool FoeNear(Player me, Vector2 pos, List<Foe> foes, float rx, float ry, bool reachableOnly)
        {
            for (int i = 0; i < foes.Count; i++)
            {
                Vector2 p = foes[i].P;
                if (Math.Abs(p.X - pos.X) >= rx || Math.Abs(p.Y - pos.Y) >= ry) continue;
                if (reachableOnly && LevelHasPits && !foes[i].Air && PathHasPit(me, pos, p)) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// every hole fall (any reason, avoidance on or off) respawns next to the human: the game would put the bot back
        /// on its OWN last solid spot (same side of the hole). If the fall damage would KO the hero, the bot is dropped in next
        /// to the human right away instead (no HP cost, never a KO). Runs every frame from Think.
        /// </summary>
        private void PitFallWatch(Player me, Player leader)
        {
            bool inPit = false, inHole = false;
            try { inPit = me.IsInsidePit; inHole = GameRefl.StateName(me) == "StateFallInHole"; } catch { }
            if (!inPit && !inHole) { _pitFallSeen = false; _pitFallRedirected = false; return; }
            if (me.IsKO || leader == null || leader.IsKO) return;
            if (!_pitFallSeen)
            {
                _pitFallSeen = true;
                Stat("pit-fall");
                if (!inHole && !FallSafe(me))
                {
                    Respawn(me, leader, $"fell into a hole with HP {me.HP}/{me.MaxHP} (fall damage {FallDamage():0} would KO)", "pit-fall-respawn");
                    _pitFallSeen = false;
                    return;
                }
            }
            if (SetClimbOut(me, leader) && !_pitFallRedirected) { _pitFallRedirected = true; Stat("pit-fall-redirect"); Log.Verbose($"[bot P{Info.PlayerID + 1}] fell into a hole: respawn moved to the human's last solid spot"); }
        }

        // ------------------------------------------------------------ planning
        /// <summary>Find the narrowest crossing toward the human: lane (Y), take-off centre X, safe landing centre X.</summary>
        private bool PlanGap(Player me, Vector2 pos, Vector2 lg, out float laneY, out float rimX, out float landX, out float need)
        {
            laneY = pos.Y; rimX = landX = pos.X; need = float.MaxValue;
            int dir = Math.Sign(lg.X - pos.X);
            if (dir == 0 || Math.Abs(lg.X - pos.X) < 24f) return false;
            float fl = MyFloor(me), hw = HalfWidth(me);
            bool found = false;
            for (int i = 0; i <= 16; i++)
            {
                float y = pos.Y + (i == 0 ? 0f : ((i + 1) / 2) * 8f * (i % 2 == 1 ? -1f : 1f));
                Vector2 laneStart = new Vector2(pos.X, y);
                if (i > 0)
                {
                    Vector2 cl = ClampToCamera(me, laneStart, fl);
                    if (Math.Abs(cl.Y - y) > 1f || !SpotWalkable(me, laneStart, fl) || RectHasPit(me, laneStart) || PathHasPit(me, pos, laneStart)) continue;
                }
                // march toward the human: rim = last footprint-clear centre, landing = first clear + walkable centre past the hole
                float x = pos.X, rim = float.NaN, land = float.NaN; bool inPit = false;
                for (int s = 0; s < 120; s++)
                {
                    x += dir * 4f;
                    if ((x - lg.X) * dir > 120f) break;
                    bool pit = RectHasPit(me, new Vector2(x, y));
                    if (!inPit) { if (pit) { inPit = true; rim = x - dir * 4f; } }
                    else if (!pit && SpotWalkable(me, new Vector2(x, y), fl)) { land = x; break; }
                }
                if (float.IsNaN(rim) || float.IsNaN(land)) continue;
                float landSafe = land + dir * Math.Max(10f, hw * 0.5f);
                float n = Math.Abs(landSafe - rim);
                // the landing has to lead on to the human (no second hole straight after)
                Vector2 lp = new Vector2(landSafe, y);
                if (RectHasPit(me, lp) || (PathHasPit(me, lp, lg) && PathHasPit(me, lp, new Vector2(lg.X, y)))) continue;
                float score = n + Math.Abs(y - pos.Y) * 0.25f;
                if (score < need) { need = n; laneY = y; rimX = rim; landX = landSafe; found = true; }
            }
            return found;
        }

        private void StartRecovery(Player me, CharacterInfo ci, Player leader, Vector2 lg, Vector2 pos, string why, float now)
        {
            _srT = 0f; _srKind = ""; _srPresetClimb = false;
            bool fallSafe = FallSafe(me);
            bool gap = PlanGap(me, pos, lg, out float laneY, out float rimX, out float landX, out float need);
            _srDir = Math.Sign(lg.X - pos.X); if (_srDir == 0) _srDir = 1;
            string info = gap ? $"gap {need:0}px (lane y {laneY:0})" : "no single-hole crossing found";
            float rS = JumpReach(me, "single"), rD = ci.CanDoubleJump ? JumpReach(me, "double") : 0f, rSp = JumpReach(me, "sprint");
            float margin = fallSafe ? 0.85f : 0.7f;   // only near-certain jumps when a fall would KO the hero

            if (gap && _srLevel == 0)
            {
                string kind = null;
                if (ci.Flying) kind = "fly";
                else if (need <= rS * margin) kind = "single";
                else if (rD > 0f && need <= rD * margin) kind = "double";
                else if (need <= rSp * margin) kind = "sprint";
                if (kind != null)
                {
                    _srKind = kind; _srLaneY = laneY; _srRimX = rimX; _srLandX = landX;
                    _srStandX = kind == "sprint" ? rimX - _srDir * Math.Max(60f, RunSpeed(me) * 0.55f) : rimX;
                    _srPresetClimb = fallSafe;   // a missed jump then respawns on the human's side
                    _srPhase = kind == "fly" ? 4 : 1;
                    Stat("stranded-gapjump");
                    Log.Verbose($"[stranded P{Info.PlayerID + 1}] {why}, {info}: gap jump ({kind}; reach single {rS:0} / double {rD:0} / sprint {rSp:0} px)");
                    return;
                }
            }
            if (gap && _srLevel <= 1 && fallSafe && !ci.Flying && SetClimbOut(me, leader))
            {
                _srKind = "sacrifice"; _srLaneY = laneY; _srRimX = rimX; _srLandX = landX; _srStandX = rimX;
                _srPresetClimb = true; _srPhase = 1;
                Stat("stranded-sacrifice");
                Log.Verbose($"[stranded P{Info.PlayerID + 1}] {why}, {info}: walking into the hole (fall damage {FallDamage():0}, HP {me.HP}/{me.MaxHP}; respawn at the human's climb-out spot)");
                return;
            }
            Respawn(me, leader, why + ", " + info + (fallSafe ? "" : ", a fall could KO the hero"));
        }

        /// <summary>Point the game's hole respawn at the human's last solid spot (the setter only accepts on-camera spots).</summary>
        private static bool SetClimbOut(Player me, Player leader)
        {
            try
            {
                if (leader == null) return false;
                Vector3 c = leader.ClimbOutPosition;
                if (float.IsNaN(c.X) || c.X < -1e6f) return false;
                me.ClimbOutPosition = c;
                return Vector3.DistanceSquared(me.ClimbOutPosition, c) < 1f;
            }
            catch { return false; }
        }

        /// <summary>(c) The game's own camera-crush recovery, at the human's climb-out spot. No HP cost.</summary>
        private void Respawn(Player me, Player leader, string why, string stat = "stranded-respawn")
        {
            _srPhase = 0; _pitGuardOff = false; _srLastEnd = BotManager.Time; _srCd = 8f;
            try
            {
                SetClimbOut(me, leader);
                float drop = 180f; try { drop = EngineSettings.Singleton.GameResolution.Y; } catch { }
                me.TeleportToClimbOutPosition(true, drop);
                if (me.BodyComponent != null)
                {
                    me.BodyComponent.CameraCollision = Vector3.Zero;
                    me.BodyComponent.PlayfieldCollision = Vector3.Zero;
                    me.BodyComponent.Displacement.Speed = Vector3.Zero;
                }
                GameRefl.ChangeState(me, "StateFall");
                AbortPlan(); _forcedMoveT = 0f; _goalInit = false; _rmKind = -1; _blkSX = _blkSY = 0;
                Stat(stat);
                Log.Verbose($"[stranded P{Info.PlayerID + 1}] {why}: respawned next to the human (drop-in, no HP cost)");
            }
            catch (Exception ex) { Log.Warn($"[stranded P{Info.PlayerID + 1}] respawn failed: {ex.Message}"); }
        }

        private void EndRecovery(string result)
        {
            if (_srPhase != 0) Log.Verbose($"[stranded P{Info.PlayerID + 1}] {_srKind}: {result}");
            _srPhase = 0; _pitGuardOff = false; _srPresetClimb = false; _srLastEnd = BotManager.Time; _srCd = 4f;
            _forcedMoveT = 0f; _goalInit = false; _rmKind = -1; _blkSX = _blkSY = 0;
        }

        // ------------------------------------------------------------ execution
        private bool RunRecovery(Player me, CharacterInfo ci, Player leader, Vector2 lg, Vector2 pos, bool onGround, bool flying, bool hurt, float dt, float now)
        {
            _srT += dt;
            string st = GameRefl.StateName(me);
            if (_srPresetClimb && leader != null && _srPhase >= 3) SetClimbOut(me, leader);
            if (st == "StateFallInHole") { _srPhase = 6; _pitGuardOff = true; }
            if (me.IsKO || leader == null) { EndRecovery("aborted (KO / no human)"); return false; }
            Status = "stranded"; _urgentMove = true;
            switch (_srPhase)
            {
                case 1: // walk to the take-off spot in the chosen lane (pit guard still on)
                {
                    _pitGuardOff = false;
                    if (_srT > 5f || (hurt && _srT > 0.5f)) { EndRecovery("approach timed out"); return false; }
                    Vector2 stand = new Vector2(_srStandX, _srLaneY), d = stand - pos;
                    if (Math.Abs(d.X) <= 3f && Math.Abs(d.Y) <= 3f && onGround)
                    {
                        _srT = 0f;
                        if (_srKind == "sacrifice") { _srPhase = 5; _pitGuardOff = true; Intent = "stranded: into the hole"; _move = new Vector2(_srDir, 0f); return true; }
                        if (_srKind == "sprint") { _srPhase = 2; _pitGuardOff = true; return RunRecovery(me, ci, leader, lg, pos, onGround, flying, hurt, 0f, now); }
                        GapJump(me, ci); return true;
                    }
                    _move = new Vector2(Math.Abs(d.X) > 3f ? Math.Sign(d.X) : 0f, Math.Abs(d.Y) > 3f ? Math.Sign(d.Y) * (Math.Abs(d.Y) > 8f ? 1f : 0.5f) : 0f);
                    Intent = "stranded: to the " + (_srKind == "sacrifice" ? "edge" : "take-off spot");
                    return true;
                }
                case 2: // sprint run-up (double tap via SprintOutput), jump at the rim
                {
                    _pitGuardOff = true;
                    _sprintWant = true; _sprintGoalX = _srLandX + _srDir * 400f; _sprintGoalY = _srLaneY; _sprintReason = "gap";
                    _move = new Vector2(_srDir, 0f);
                    float toRim = (_srRimX - pos.X) * _srDir;
                    if (toRim <= 6f)
                    {
                        if (st == "StateSprint") { GapJump(me, ci); return true; }
                        // no sprint by the rim: stop and use a walking jump if it reaches, else give up this try
                        _move = Vector2.Zero; _sprintWant = false;
                        if (Math.Abs(_srLandX - pos.X) <= JumpReach(me, "single") * 0.85f) { GapJump(me, ci); return true; }
                        EndRecovery("sprint did not start before the rim"); return true;
                    }
                    if (_srT > 3f) { _move = Vector2.Zero; EndRecovery("run-up timed out"); return true; }
                    Intent = "stranded: run-up";
                    return true;
                }
                case 3: // airborne: keep pushing across; done once back on the ground
                {
                    _pitGuardOff = true;
                    _move = new Vector2(_srDir, 0f);
                    if (_srT > 0.25f && onGround)
                    {
                        bool across = (pos.X - _srRimX) * _srDir > 8f && !RectHasPit(me, pos);
                        EndRecovery(across ? "landed across the gap" : "landed short (still this side)");
                        return false;
                    }
                    if (_srT > 3f) { EndRecovery("air time out"); return false; }
                    Intent = "stranded: gap jump";
                    return true;
                }
                case 4: // flyer: take off, fly across, land on solid ground past the hole
                {
                    _pitGuardOff = true;
                    if (_srT > 7f) { EndRecovery("flight timed out"); return false; }
                    if (!flying)
                    {
                        if (!_flightEntering && onGround && _srT < 2f) { JumpWhy("gap-jump"); Hold(A_JUMP, 1.1f); _flightEntering = true; _flightEnterT = 0f; _flightCd = 1.0f; }
                        else if (_srT > 0.3f && onGround && !_flightEntering)
                        {
                            bool across0 = (pos.X - _srRimX) * _srDir > 8f && !RectHasPit(me, pos);
                            if (across0) { EndRecovery("flew across and landed"); return false; }
                        }
                        _move = _flightEntering ? new Vector2(_srDir, 0f) : Vector2.Zero;
                        Intent = "stranded: take off";
                        return true;
                    }
                    Vector2 tgt = new Vector2(_srLandX + _srDir * 16f, _srLaneY), d = tgt - pos;
                    bool past = (pos.X - _srLandX) * _srDir >= 0f && !RectHasPit(me, pos);
                    if (past && _flightCd <= 0f) { JumpWhy("land"); Tap(A_JUMP); _flightCd = 2.5f; _move = Vector2.Zero; Intent = "stranded: land"; return true; }
                    _move = past ? Vector2.Zero : (d.LengthSquared() > 1f ? Vector2.Normalize(d) : Vector2.Zero);
                    Intent = "stranded: fly across";
                    return true;
                }
                case 5: // sacrifice: walk into the hole (pit guard off for this bot only)
                {
                    _pitGuardOff = true;
                    SetClimbOut(me, leader);
                    _move = onGround || me.Position.Z > -32f ? new Vector2(_srDir, 0f) : Vector2.Zero;
                    if (_srT > 3f) { EndRecovery("could not get into the hole"); Respawn(me, leader, "sacrifice failed"); return true; }
                    Intent = "stranded: into the hole";
                    return true;
                }
                case 6: // falling / in the hole: the game respawns us at ClimbOutPosition after FallInHoleDuration
                {
                    _pitGuardOff = true;
                    _move = Vector2.Zero;
                    if (st == "StateFallInHole") { if (_srPresetClimb) SetClimbOut(me, leader); _srT = Math.Min(_srT, 0.1f); Intent = "stranded: respawning"; return true; }
                    if (_srT > 1f) { EndRecovery("respawned by the game (" + (CanReachHuman(me, pos, lg) ? "with the human" : "still cut off") + ")"); return false; }
                    return true;
                }
            }
            EndRecovery("unknown phase");
            return false;
        }

        private void GapJump(Player me, CharacterInfo ci)
        {
            _srPhase = 3; _srT = 0f; _pitGuardOff = true;
            JumpWhy("gap-jump");
            Hold(A_JUMP, _srKind == "sprint" ? 0.15f : 0.2f);
            _move = new Vector2(_srDir, 0f);
            if (_srKind == "double") { Plan("gap double jump", null, false); At(DoubleJumpAt(me), () => { JumpWhy("gap-jump"); Tap(A_JUMP); }); }
            Intent = "stranded: gap jump (" + _srKind + ")";
        }
    }
}
