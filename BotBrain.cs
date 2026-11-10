using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Paris.Engine;
using Paris.Engine.Combat;
using Paris.Engine.GameObject;
using Paris.Engine.Player;
using Paris.Engine.Scene;
using Paris.Game;
using Paris.Game.Actor;
using Paris.Game.Data;

namespace CosmicBots
{
    /// <summary>What the bot knows about one enemy this frame.</summary>
    internal sealed class Foe
    {
        public Enemy E;
        public Vector2 P;          // ground position (X, depth Y)
        public float H;            // height above its floor (Z up)
        public bool Air;           // airborne / flying right now
        public bool Flyer;         // flying-type enemy currently in flight
        public bool Boss, Blocking, Invuln, Antic, Delivery, Armored;
        public bool FacingRight;
        public float Reach;
        public string State;
        public string Label;
        // boss parts. P/H/Air/Invuln describe the part we aim at; BodyP/BodyH the enemy's own position (threat reading)
        public Vector2 BodyP; public float BodyH;
        public BossPart Part;
        public float ExtraReach, LaneTol;
        public bool Skin, Counter;          // damaging skin up right now / rusher that counters light hits
        public float SkinReach, SkinDepth;
        public bool Rush, RushKnown; public Vector2 RushDir; public float RushSpeed, RushDepth;   // Annihilus Negative Rush (BotRush.cs)
        public void Reset() { Rush = RushKnown = false; RushDir = default(Vector2); RushSpeed = RushDepth = 0f; E = null; P = BodyP = default(Vector2); H = BodyH = Reach = ExtraReach = LaneTol = 0f; Part = null; Skin = Counter = false; SkinReach = SkinDepth = 0f; Air = Flyer = Boss = Blocking = Invuln = Antic = Delivery = Armored = FacingRight = false; State = Label = null; }
    }

    public partial class BotController
    {
        // ---------------------------------------------------------------- tunables (world units ~ pixels at 640x360)
        private const float ATTACK_RANGE_X = 46f, LANE_TOLERANCE = 10f, ENGAGE_RADIUS = 260f, FOLLOW_STOP = 22f,
            AIR_MIN_H = 18f, PERSONAL_SPACE = 30f;

        // ---------------------------------------------------------------- shared registry (spacing, target claiming, team specials)
        private static readonly List<BotController> Live = new List<BotController>();
        internal Vector2 RegPos; internal Enemy RegTarget; internal bool RegHoldingSpecial, RegHasSpecial; internal float RegSeen;

        // ---------------------------------------------------------------- state
        private readonly Random _rng = new Random(Environment.TickCount ^ 0x5bd1e995);
        private float _decideCd, _comboCd, _powerCd, _specialCd, _swapCd, _teamCd, _airCd, _flightCd, _defendCd, _idleCd = 6f, _idleT,
            _stuckTimer, _energyFullT, _noAirFoesT, _flightEnterT, _specialHoldT, _bossBackoffT, _lastSwapTime = -100f, _forcedMoveT, _bossBackoffCdUntil;
        private bool _engagePart; private string _defWhy; private Vector2 _engageAnchor;
        private bool _flightEntering, _specialHolding, _specialJoin, _wasHurt;
        // follow-jump gating + jump reason tagging + the human's ground position
        private float _followJumpCd, _leaderHighT, _jumpWhyT = -100f, _leaderAirT; private string _jumpWhy;
        private Player _leaderRef; private Vector2 _leaderGround; private int _stuckJumps; private Vector2 _stuckJumpPos;
        private void JumpWhy(string r) { _jumpWhy = r; _jumpWhyT = BotManager.Time; }
        private Vector2 _lastPos, _forcedMove;
        // per-frame buffers reused instead of allocated every frame
        private readonly List<Player> _humansBuf = new List<Player>(4);
        private readonly List<Foe> _foesBuf = new List<Foe>(16), _foePool = new List<Foe>(16);
        private int _foePoolUsed;
        private Foe RentFoe(Enemy e)
        {
            if (_foePoolUsed == _foePool.Count) _foePool.Add(new Foe());
            Foe f = _foePool[_foePoolUsed++];
            f.Reset(); f.E = e; f.P = e.Position2D;
            return f;
        }
        // steering state (anti-twitch): arrival hysteresis, smoothed goal/separation, slot side, facing hold
        private bool _arrX = true, _arrY = true, _goalInit, _lastFacingLeft;
        private Vector2 _goalSmooth, _sepSmooth;
        private int _slotSide;                 // -1 = left of the human, +1 = right; 0 = not chosen yet
        private float _slotSideT, _faceWrongT, _turnCd;
        private bool _wasFollowing;
        private int _side;                     // -1 = stand left of target, +1 = right
        private Enemy _sideTarget;
        private CharacterInfo _lastHero;
        private Player _lastMe;
        private string _lastState = "";
        private bool _lastFlying;
        private bool _cdStarted;


        // ---------------------------------------------------------------- plans (timed input sequences: combos, air attacks, ...)
        private sealed class Step { public float T; public Action Do; }
        private readonly List<Step> _plan = new List<Step>();
        private float _planT;
        private string _planName = "";
        private Enemy _planTarget;
        private bool _planChase;   // keep steering toward the plan target while it runs

        private void Plan(string name, Enemy target, bool chase)
        {
            _plan.Clear(); _planT = 0f; _planName = name; _planTarget = target; _planChase = chase;
        }
        private void At(float t, Action a) => _plan.Add(new Step { T = t, Do = a });
        private bool PlanBusy => _plan.Count > 0;
        private void AbortPlan() { _plan.Clear(); _planName = ""; Release(A_ATTACK); Release(A_SWAP); Release(A_POWER); }

        private void RunPlan(float dt)
        {
            if (_plan.Count == 0) return;
            _planT += dt;
            while (_plan.Count > 0 && _plan[0].T <= _planT)
            {
                Step s = _plan[0];
                _plan.RemoveAt(0);
                s.Do();
            }
            if (_plan.Count == 0) _planName = "";
        }

        // ---------------------------------------------------------------- skill / humanizing
        private float Aggr => Math.Max(0.25f, Config.Aggression);
        private int Skill => Math.Max(0, Math.Min(2, Config.BotSkill));
        private float ReactionTime => new[] { 0.36f, 0.22f, 0.12f }[Skill] / (float)Math.Sqrt(Aggr);
        private float DefendChance => new[] { 0.35f, 0.62f, 0.85f }[Skill];
        private float AntiAirChance => new[] { 0.45f, 0.75f, 0.95f }[Skill];
        private float EnderChance => new[] { 0.35f, 0.7f, 0.9f }[Skill];
        private float TeamChance => new[] { 0.2f, 0.45f, 0.65f }[Skill];
        private float Sloppy => new[] { 0.22f, 0.08f, 0.02f }[Skill];
        private float R() => (float)_rng.NextDouble();
        private float Jit(float v, float pct = 0.3f) => v * (1f - pct + 2f * pct * R());
        private bool Chance(float p) => R() < p;

        // ================================================================ main think
        private void Think(float dt)
        {
            Info = FindMyInfo();
            Scene2d scene = Scene2d.Active;
            ActivePlayer = null;
            if (Info == null || scene == null || scene.Players == null) { Status = "waiting"; Intent = ""; Unregister(); return; }
            if (scene.Paused || (scene.HUD is Paris.Game.HUD.MainHUD hud && hud.Paused)) { Status = "paused"; return; }

            Player me = null, any = null;
            if (scene.Players.TryGetValue(Info, out List<IPlayer> mine) && mine != null)
                foreach (IPlayer ip in mine)
                    if (ip is Player p) { any = any ?? p; if (p.IsActivePlayerObject) { me = p; break; } }
            ActivePlayer = me ?? any;
            if (me == null) { Status = any != null && any.IsKO ? "KO" : "not spawned"; Intent = ""; Unregister(); return; }
            Owner = me;
            if (BotManager.CastleDoomMode && !_cdStarted) { _cdStarted = true; try { Start(); } catch { } } // spent the one-shot menu confirm so it can't pick a perk by accident
            if (me.IsKO) { Status = "KO"; Intent = ""; AbortPlan(); _holds.Clear(); Unregister(); return; }

            float now = BotManager.Time;
            ScanLevelPits(scene);
            Register(me, now);
            Tick(ref _decideCd, dt); Tick(ref _comboCd, dt); Tick(ref _powerCd, dt); Tick(ref _specialCd, dt); Tick(ref _swapCd, dt);
            Tick(ref _teamCd, dt); Tick(ref _airCd, dt); Tick(ref _flightCd, dt); Tick(ref _defendCd, dt); Tick(ref _idleCd, dt);
            Tick(ref _forcedMoveT, dt); Tick(ref _bossBackoffT, dt); Tick(ref _followJumpCd, dt);

            CharacterInfo ci = me.CurrentCharacterInfo;
            if (ci == null) { Status = "loading"; return; }
            TrackHeroAndStats(me, ci, dt, now);
            SuppressFrameStart();

            Vector2 pos = me.Position2D;
            bool onGround = me.IsOnGround;
            bool flying = me.IsFlying;
            float aggr = Aggr;
            _moveFromSteer = false; _lastDt = dt > 0f ? dt : _lastDt; _sprintWant = false;
            UpdateWallStuck(me, pos, onGround, flying, me.IsInStateHurt || me.IsInStateKnockdown, now);
            LearnAttackHeights(me, ci, onGround && !flying);

            // ---- humans
            Player leader = null, needy = null; float leaderDist = float.MaxValue, lowestHumanHp = 1f;
            var humans = _humansBuf; humans.Clear();
            foreach (KeyValuePair<PlayerInfo, List<IPlayer>> kv in scene.Players)
            {
                if (kv.Key == null || kv.Key.Controller is BotController || kv.Value == null) continue;
                foreach (IPlayer ip in kv.Value)
                {
                    if (!(ip is Player p) || !p.IsActivePlayerObject || p.IsKO) continue;
                    humans.Add(p);
                    if (p.HPRatio < lowestHumanHp) { lowestHumanHp = p.HPRatio; needy = p; }
                    float d = Vector2.Distance(p.Position2D, pos);
                    if (d < leaderDist) { leaderDist = d; leader = p; }
                }
            }

            // formation / follow distance use the human's ground position (last grounded spot while they jump)
            Vector2 leaderGround = LeaderGround(leader, dt);
            if (leader != null) leaderDist = Vector2.Distance(leaderGround, pos);
            if (leader != null) PitFallWatch(me, leader);                         // a hole fall respawns next to the human

            // ---- enemies
            Rectangle zone = scene.CurrentCamera != null ? scene.CurrentCamera.GetScreenZone() : Rectangle.Empty;
            var foes = _foesBuf; foes.Clear(); _foePoolUsed = 0;
            foreach (object o in scene.GetGameObjectsOfType<Enemy>())
            {
                if (!(o is Enemy e) || !e.Active || e.IsKO || e.IsInStateKO || !e.IsActiveInWave || e.IsInsideSpawner) continue;
                if (!TargetableEnemy(e)) continue;                                   // later-wave enemies waiting in plain sight
                Vector2 ep = e.Position2D;
                if (zone.Width > 0 && (ep.X < zone.Left - 16 || ep.X > zone.Right + 16)) continue; // off screen
                foes.Add(See(RentFoe(e)));
            }
            ApplyParts(me, ci, foes, pos, flying);                                   // bosses are fought through their hittable parts
            UpdateAdvance(scene);                                                    // GO arrow up -> only fight threats
            int near = 0, veryNear = 0, airFoes = 0, airNear = 0, groundNear = 0;
            foreach (Foe f in foes)
            {
                float dx = Math.Abs(f.P.X - pos.X), dy = Math.Abs(f.P.Y - pos.Y);
                if (dx < 90f && dy < 30f) near++;
                if (dx < 60f && dy < 18f) veryNear++;
                if (f.Air) { airFoes++; if (dx < 260f) airNear++; }
                else if (dx < 120f && dy < 40f) groundNear++;
            }
            if (airNear > 0) _noAirFoesT = 0f; else _noAirFoesT += dt;

            // ---- skin enemies (keep out of reach, no melee) and the burst disengage (BotSkin.cs)
            bool noAttack = SkinUpdate(me, ci, foes, pos, leader, leaderGround, onGround, me.IsInStateHurt || me.IsInStateKnockdown, now, dt);
            if (RushUpdate(me, ci, foes, pos, onGround, me.IsInStateHurt || me.IsInStateKnockdown, now, dt)) noAttack = true;   // Annihilus Negative Rush lane escape

            // ---- plan in progress (combo strings, air attacks, team moves)
            RunPlan(dt);

            // ---- special: keep holding for a team-up / release when it is time
            UpdateSpecialHold(me, humans, now, dt);
            if (TryJoinTeamSpecial(me, humans)) return;

            // ---- flight bookkeeping
            if (_flightEntering)
            {
                _flightEnterT += dt;
                if (me.IsFlying || _flightEnterT > 1.1f || (_flightEnterT > 0.25f && onGround)) { Release(A_JUMP); _flightEntering = false; }
            }

            // ---- defense (telegraphed attacks, projectiles, getting hit)
            // threat-driven parry / block / dodge / assist-defense (BotDefense.cs)
            bool hurt = me.IsInStateHurt || me.IsInStateKnockdown;
            _wasHurt = hurt;
            if (!hurt && _def == null && !StrandAirborne)
            {
                Threat threat = FindThreat(me, foes, scene, pos, now);
                if (threat != null) DecideDefense(me, ci, threat, pos, onGround, now);
            }
            _defWhy = null;
            if (RunDefense(me, ci, pos, onGround, hurt, now)) { if (_defWhy != null && foes.Count > 0) Suppress(_defWhy); if (_forcedMoveT > 0f) { _move = _forcedMove; _urgentMove = true; } return; }
            if (_rushEsc && !hurt) { RushApply(); return; }   // get out of Annihilus' charge lane (instant threat: no reaction roll / delay)

            // ---- stranded behind a hole / far from the human with no progress -> gap jump, sacrifice or respawn (BotStranded.cs)
            if (UpdateStranded(me, ci, leader, leaderGround, leaderDist, pos, onGround, flying, hurt, foes, dt, now)) return;

            // ---- tag team: tactical swaps
            if (!PlanBusy && !_specialHolding && TrySwap(me, ci, foes, airNear, near, now)) return;

            // ---- pick a target (claiming + priorities)
            Enemy rescueFrom = null;
            if (needy != null && lowestHumanHp < 0.3f)
            {
                float best = 120f;
                foreach (Foe f in foes) { float d = Vector2.Distance(f.P, needy.Position2D); if (d < best) { best = d; rescueFrom = f.E; } }
            }
            Foe target = PickTarget(foes, pos, leader, rescueFrom, ci, flying);
            if (noAttack) target = null;                                              // stepping out of a skin / burst escape
            RegTarget = target?.E;
            NoDamageWatch(me, target, pos, dt);

            // ---- specials (crowds, bosses, rescue, energy cap) and team specials
            RegHasSpecial = HasSpecial(me);
            if (me.Energy >= me.MaxEnergy && me.MaxEnergy > 0) _energyFullT += dt; else _energyFullT = 0f;
            if (!PlanBusy && !_specialHolding) TrySpecial(me, foes, target, near, needy, lowestHumanHp, rescueFrom, now, dt);

            // ---- situational Cosmic Assists (partner hero call-ins)
            if (!PlanBusy && !_specialHolding && TryAssist(me, foes, target, near, needy, lowestHumanHp, pos, dt)) return;

            // ---- flight decisions
            if (ci.Flying && !_flightEntering && !PlanBusy)
            {
                bool leaderFlies = leader != null && leader.IsFlying;
                if (!flying && _flightCd <= 0f && !me.IsInSpecialAttack)
                {
                    bool flyerClose = target != null && target.Flyer && Math.Abs(target.P.X - pos.X) < 240f;
                    if ((flyerClose || airNear >= 2 || leaderFlies) && onGround && Chance(new[] { 0.5f, 0.8f, 1f }[Skill]))
                    {
                        JumpWhy("take-off"); Hold(A_JUMP, 1.1f); _flightEntering = true; _flightEnterT = 0f; _flightCd = 2.5f;
                        Intent = "take off"; Stat("flight-try");
                    }
                }
                else if (flying && _flightCd <= 0f)
                {
                    bool land = !leaderFlies && _noAirFoesT > 2.0f && (groundNear > 0 || foes.Count == 0 || _noAirFoesT > 4f);
                    if (land && RectHasPit(me, pos)) { land = false; _flightCd = 0.5f; Stat("no-land-over-pit"); }   // only land over solid ground
                    if (land) { JumpWhy("land"); Tap(A_JUMP); _flightCd = 2.5f; Intent = "land"; }
                }
            }

            // ---- health pickups: leave them for humans who need them more
            Vector2 avoid = Vector2.Zero; Vector2? wantPickup = null;
            foreach (object o in scene.GetGameObjectsOfType<HealthPickup>())
            {
                if (!(o is HealthPickup hp) || !hp.Active) continue;
                Vector2 hpPos = hp.Position2D; float d = Vector2.Distance(hpPos, pos);
                bool humanNeedsIt = leader != null && lowestHumanHp < 0.999f && lowestHumanHp <= me.HPRatio + 0.05f;
                if (humanNeedsIt) { if (d < 55f && d > 0.01f) avoid += (pos - hpPos) / d * (55f - d) / 55f; }
                else if (me.HPRatio < 0.5f && d < 200f) wantPickup = hpPos;
            }
            Vector2 hazard = HazardAvoid(scene, pos, onGround);
            if (BotManager.CastleDoomMode) hazard += CastleDoom.TrapAvoid(scene, pos);   // Castle Doom traps (this mode only)
            Vector2 sep = Separation(me, pos, humans) + avoid;
            Foe nearestFoe = null; { float bd = 200f; foreach (Foe f in foes) { float d = Vector2.Distance(f.P, pos); if (d < bd) { bd = d; nearestFoe = f; } } }

            // ---- movement / fighting
            Vector2 goal = pos; bool engaging = false;
            // no leash. Bots chase any on-screen, reachable enemy; goals are clamped to the camera's collision box,
            // so they spread as far as the game's camera allows and fall back to formation when no reachable enemy is left.

            if (PlanBusy && _planTarget != null)
            {
                Status = "fighting"; Intent = _planName;
                if (_planChase && _planTarget != null && _planTarget.Active && !_planTarget.IsKO)
                {
                    Vector2 tp = _planTarget.Position2D;
                    if (_partPick != null && ReferenceEquals(_planTarget, _partEnemy)) tp = new Vector2(_partPick.CenterX, _partGroundY);   // chase the part, not the body
                    float dx = tp.X - pos.X;
                    if (Math.Abs(dx) > 18f) _move = new Vector2(Math.Sign(dx), 0f);
                    if (Math.Abs(tp.Y - pos.Y) > 4f) _move.Y = Math.Sign(tp.Y - pos.Y) * 0.6f;
                }
            }
            else if (target != null)
            {
                engaging = true;
                goal = Engage(me, ci, target, pos, onGround, flying, near, veryNear, aggr, now);
            }
            else if (wantPickup.HasValue) { goal = wantPickup.Value; Status = "getting health"; Intent = "health pickup"; }
            else if (leader != null) { goal = Follow(me, ci, leader, leaderGround, pos, leaderDist, onGround, flying, dt); }
            else { Status = "idle"; Intent = ""; }

            bool following = !engaging && !PlanBusy && !wantPickup.HasValue && leader != null;
            if (following != _wasFollowing) { _goalInit = false; _wasFollowing = following; _rmKind = -1; }
            if (_move == Vector2.Zero && _idleT <= 0f && !(PlanBusy && _planTarget != null))
                Steer(me, goal, following ? leaderGround : (engaging && _engagePart) ? _engageAnchor : goal, pos, sep, hazard, engaging, following, dt);
            else if (_move != Vector2.Zero) { _arrX = _arrY = false; }
            if (_idleT > 0f) { _idleT -= dt; if (!engaging) _move = Vector2.Zero; }
            // idle facing: face the human's way (or the nearest enemy), only after being "wrong" for a while
            // a human turning in mid-air (jump / landing) is not a reason to turn around
            if (following && _move == Vector2.Zero && _arrX && _arrY && onGround && !flying && (nearestFoe != null || leader.IsOnGround || leader.IsFlying))
            {
                bool wantLeft = nearestFoe != null ? nearestFoe.P.X < pos.X : leader.HorizontalFlipped;
                if (Math.Abs((nearestFoe != null ? nearestFoe.P.X : pos.X + (wantLeft ? -1f : 1f) * 50f) - pos.X) > 12f && wantLeft != me.HorizontalFlipped)
                {
                    _faceWrongT += dt;
                    if (_faceWrongT > 0.7f && _turnCd <= 0f) { _move = new Vector2(wantLeft ? -1f : 1f, 0f); _turnCd = 1.5f; _faceWrongT = 0f; _urgentMove = true; }
                }
                else _faceWrongT = 0f;
            }
            else _faceWrongT = 0f;
            Tick(ref _turnCd, dt);
            if (_forcedMoveT > 0f) { _move = _forcedMove; _urgentMove = true; }
            if (hazard != Vector2.Zero) _urgentMove = true;
            SprintIntent(me, pos, goal, engaging, following, leaderDist, onGround, flying, hurt, hazard != Vector2.Zero, dt);
            if (PlanBusy && _planTarget != null && _move != Vector2.Zero) _urgentMove = true;   // combo chase / anti-air positioning
            if (me.HorizontalFlipped != _lastFacingLeft) { _lastFacingLeft = me.HorizontalFlipped; Stats.FacingFlips++; if (following) Stats.IdleFacingFlips++; }

            // ---- unstick: moving but not progressing -> jump (or fly). Steered moves into walls are handled by the
            // per-axis wall-stuck logic (they stop instead); we still try a jump when a far-away human is out of reach.
            // "stuck" is judged on last frame's FINAL input (after pit guard / sprint), so waiting at a pit edge or a
            // sprint start frame is not "stuck"; while following, at most one such jump per 1.5 s and two per spot.
            bool wallHeld = (_blkSX != 0 || _blkSY != 0) && following && leaderDist > 180f;
            bool noProgress = _lastOut != Vector2.Zero && _forcedMoveT <= 0f && Vector2.DistanceSquared(_lastPos, pos) < 0.25f;
            if (Vector2.DistanceSquared(_stuckJumpPos, pos) > 24f * 24f) _stuckJumps = 0;
            bool mayJump = !following || (_followJumpCd <= 0f && _stuckJumps < 2);
            if ((noProgress || wallHeld) && onGround && !PlanBusy && mayJump)
            {
                _stuckTimer += dt;
                if (_stuckTimer > Jit(1.0f))
                {
                    if (ci.Flying && _flightCd <= 0f && !flying) { JumpWhy("fly-over"); Hold(A_JUMP, 1.1f); _flightEntering = true; _flightEnterT = 0f; _flightCd = 3f; Intent = "fly over obstacle"; }
                    else { JumpWhy(noProgress ? "stuck" : "wall-far"); Hold(A_JUMP, 0.2f); Intent = "jump obstacle"; if (ci.CanDoubleJump) { Plan("double jump over", null, false); At(0.22f, () => Tap(A_JUMP)); } }
                    _stuckTimer = 0f;
                    if (following) { _followJumpCd = 1.5f; if (_stuckJumps == 0) _stuckJumpPos = pos; _stuckJumps++; }
                }
            }
            else _stuckTimer = 0f;
            _lastPos = pos;
        }

        private static void Tick(ref float t, float dt) { if (t > 0f) t -= dt; }

        /// <summary>
        /// Anti-twitch steering. The game normalizes ANY non-zero stick input to full walk speed and flips facing on
        /// any non-zero X, so small corrections = full-speed single-frame steps + turn animations. So: smooth the goal,
        /// fold separation into the goal (dead zone + low-pass, not a direct push), and use per-axis arrive/leave hysteresis.
        /// </summary>
        private void Steer(Player me, Vector2 goal, Vector2 anchor, Vector2 pos, Vector2 sep, Vector2 hazard, bool engaging, bool following, float dt)
        {
            // separation: low-pass + dead zone, applied as a goal offset so two bots settle instead of push-pulling
            _sepSmooth = Vector2.Lerp(_sepSmooth, sep, 1f - (float)Math.Exp(-dt * 4f));
            Vector2 sepUse = _sepSmooth.Length() < 0.2f ? Vector2.Zero : _sepSmooth;
            Vector2 raw = goal;
            goal += sepUse * PERSONAL_SPACE;
            // keep the goal inside the camera box and out of walls (fallback toward the anchor / human lane)
            goal = MakeReachable(me, goal, anchor, pos);
            if (engaging && RegTarget != null && Vector2.Distance(goal, raw) > 70f && (!SpotWalkable(me, raw, me.BodyComponent != null ? me.BodyComponent.FloorHeightIgnoringPits : 0f) || PathHasPit(me, pos, raw)))
                MarkUnreachable(RegTarget, 2.0f);
            _steerEngaging = engaging;
            if (!_goalInit || Vector2.Distance(goal, _goalSmooth) > 160f) { _goalSmooth = goal; _goalInit = true; }
            else _goalSmooth = Vector2.Lerp(_goalSmooth, goal, 1f - (float)Math.Exp(-dt * (following ? 3f : 9f)));
            Vector2 g = _goalSmooth;
            float dx = g.X - pos.X, dy = g.Y - pos.Y;
            float arriveX = engaging ? 5f : 10f, leaveX = engaging ? 14f : 40f;
            float arriveY = engaging ? 3f : 5f,  leaveY = engaging ? 8f : 16f;
            if (_arrX) { if (Math.Abs(dx) > leaveX) _arrX = false; } else if (Math.Abs(dx) < arriveX) _arrX = true;
            if (_arrY) { if (Math.Abs(dy) > leaveY) _arrY = false; } else if (Math.Abs(dy) < arriveY) _arrY = true;
            Vector2 d = new Vector2(_arrX ? 0f : dx, _arrY ? 0f : dy);
            // wall-stuck memory: don't push into a remembered wall on that axis (the other axis stays free -> slide along it)
            float nowT = BotManager.Time;
            float bx = ApplyBlock(d.X, pos.X, g.X, ref _blkSX, ref _blkUntilX, _blkPosX, _blkGoalX, engaging, nowT);
            float by = ApplyBlock(d.Y, pos.Y, g.Y, ref _blkSY, ref _blkUntilY, _blkPosY, _blkGoalY, engaging, nowT);
            if (bx != d.X) { _arrX = true; _goalSmooth.X = pos.X; }
            if (by != d.Y) { _arrY = true; _goalSmooth.Y = pos.Y; }
            d = new Vector2(bx, by);
            if (hazard != Vector2.Zero) d += hazard * 80f;
            if (d.LengthSquared() < 1f) { _move = Vector2.Zero; return; }
            Vector2 dir = Vector2.Normalize(d);
            if (engaging && !_arrY && by != 0f && Math.Abs(dx) < 30f && Math.Abs(dy) > LANE_TOLERANCE) dir = new Vector2(dir.X * 0.3f, Math.Sign(dy));
            _move = dir;
            _moveFromSteer = hazard == Vector2.Zero;
        }

        private void MoveFromForced() { if (_forcedMoveT > 0f) _move = _forcedMove; }

        // ================================================================ perception
        private static Foe See(Foe f)
        {
            Enemy e = f.E;
            float floor = 0f;
            try { if (e.BodyComponent != null) floor = e.BodyComponent.FloorHeightIgnoringPits; } catch { }
            f.H = e.Position.Z - floor;
            bool fly = false; try { fly = e.IsFlying; } catch { }
            f.Flyer = fly;
            f.Air = fly || f.H > AIR_MIN_H;
            f.Boss = e is Boss;
            f.State = GameRefl.StateName(e);
            f.Blocking = f.State.IndexOf("Block", StringComparison.Ordinal) >= 0;
            try { f.Invuln = e.IsInvulnerable; } catch { }
            try { f.Armored = e.IsCurrentlyArmored; } catch { }
            AttackPhase ph = e.CurrentAttackPhase;
            bool attackingState = f.State.IndexOf("Attack", StringComparison.Ordinal) >= 0 || f.State.IndexOf("Charge", StringComparison.Ordinal) >= 0 ||
                                  f.State.IndexOf("Grab", StringComparison.Ordinal) >= 0 || f.State.IndexOf("Dive", StringComparison.Ordinal) >= 0 ||
                                  f.State.IndexOf("Strafe", StringComparison.Ordinal) >= 0;
            f.Antic = ph == AttackPhase.Antic && (attackingState || f.Boss || f.State.Length == 0);
            f.Delivery = ph == AttackPhase.Delivery && attackingState;
            f.FacingRight = !e.HorizontalFlipped;
            try { f.Reach = Math.Min(160f, Math.Max(30f, e.CurrentAttackReachX)); } catch { f.Reach = 50f; }
            f.Label = e.GetType().Name;
            f.BodyP = f.P; f.BodyH = f.H;
            SkinRead(f, BotManager.Time);
            RushRead(f);
            return f;
        }

        private Foe PickTarget(List<Foe> foes, Vector2 pos, Player leader, Enemy rescueFrom, CharacterInfo ci, bool flying)
        {
            Foe best = null; float bestScore = float.MaxValue; float aggr = Aggr;
            bool canReachHigh = ci.Flying || ci.CanDoubleJump;
            foreach (Foe f in foes)
            {
                float dx = Math.Abs(f.P.X - pos.X), dy = Math.Abs(f.P.Y - pos.Y);
                // no distance cutoff (was ENGAGE_RADIUS*aggr+60 from the bot plus a 230*aggr leash from the human);
                // candidates are already limited to on-screen enemies, scoring still prefers the human's fight.
                float score = dx + dy * 2.5f;
                if (leader != null)
                {
                    float dl = Vector2.Distance(f.P, leader.Position2D);
                    score += dl * 0.25f;                                                            // stay near the humans' fight
                    if (dl > ENGAGE_RADIUS) score += (dl - ENGAGE_RADIUS) * 0.15f;                  // far ones only when nothing closer
                }
                if (f.Skin && !f.Boss && !(_canShootSkin && f.E != rescueFrom)) continue;       // never melee a skin-active enemy (ranged heroes shoot it)
                if (IsUnreachable(f.E) && f.E != rescueFrom) continue;                          // walled off a moment ago
                if (IsIgnored(f.E) && f.E != rescueFrom && !f.Antic && !f.Delivery) continue;   // hit it, no damage (unless it is swinging)
                if (_advance && f.E != rescueFrom && !IsThreat(f, pos, leader)) continue;       // GO is up, move on
                if (leader != null && Vector2.Distance(f.P, leader.Position2D) < 110f) score -= 40f; // on the human right now
                if (f.Antic || f.Delivery) score -= 20f;                                        // threatening someone right now
                if (f.Invuln) score += 400f;                                                    // can't hurt it right now
                if (f.Air && f.H > 110f && !canReachHigh) score += 250f;                         // too high to reach
                if (f.Air && (flying || ci.Flying)) score -= 40f;                                // flyers handle the air
                if (f.E == rescueFrom) score -= 300f;                                           // protect a struggling human
                if (f.E == RegTarget) score -= 25f;                                             // stickiness, no target flicker
                int claimed = 0;
                foreach (BotController b in Live) if (b != this && b.RegTarget == f.E) claimed++;
                if (claimed > 0 && foes.Count > 1) score += 90f * claimed;                      // spread out over enemies
                if (score < bestScore) { bestScore = score; best = f; }
            }
            return best;
        }

        private static Vector2 HazardAvoid(Scene2d scene, Vector2 pos, bool onGround)
        {
            Vector2 push = Vector2.Zero;
            foreach (object o in scene.GetGameObjectsOfType<Hazard>())
            {
                if (!(o is Hazard hz) || !hz.Active) continue;
                string st = GameRefl.StateName(hz);
                if (st != "StateWarning" && st != "StateSpinOn") continue;
                float r = (hz.AttackRadius > 0f ? hz.AttackRadius : 36f) + 18f;
                Vector2 hp = hz.Position2D; float d = Vector2.Distance(hp, pos);
                if (d < r && d > 0.01f) push += (pos - hp) / d * (r - d) / r * 1.5f;
            }
            return push;
        }

        private Vector2 Separation(Player me, Vector2 pos, List<Player> humans)
        {
            Vector2 push = Vector2.Zero;
            void Away(Vector2 other, float w)
            {
                Vector2 d = pos - other; float len = d.Length();
                if (len >= PERSONAL_SPACE) return;
                if (len < 0.5f) d = new Vector2(Info.PlayerID % 2 == 0 ? 1f : -1f, Info.PlayerID - 2f); // deterministic split when exactly stacked
                else d /= len;
                push += d * (PERSONAL_SPACE - len) / PERSONAL_SPACE * w;
            }
            foreach (BotController b in Live) if (b != this && b.ActivePlayer != null && !b.ActivePlayer.IsKO) Away(b.RegPos, 1f);
            foreach (Player h in humans) Away(h.Position2D, 0.6f);
            return push;
        }

        // ================================================================ following (formation)
        private Vector2 Follow(Player me, CharacterInfo ci, Player leader, Vector2 lp, Vector2 pos, float leaderDist, bool onGround, bool flying, float dt)
        {
            Status = "following";
            // lp = the human's ground position (last grounded spot while they are in a jump)
            bool leaderGrounded = leader.IsOnGround || leader.IsFlying;
            int k = Math.Max(0, Math.Min(2, Info.PlayerID - 1));
            // formation side: behind the human, but only switch sides once the human has stayed turned for ~0.6 s
            // turning in mid-air (jumping / landing) never moves the slot to the other side
            int wantSide = leader.HorizontalFlipped ? 1 : -1;
            if (_slotSide == 0) { _slotSide = wantSide; _slotSideT = 0f; }
            else if (!leaderGrounded) { }
            else if (wantSide != _slotSide) { _slotSideT += dt; if (_slotSideT > 0.6f) { _slotSide = wantSide; _slotSideT = 0f; } }
            else _slotSideT = 0f;
            // formation slot: staggered behind the human, spread across depth (static offsets; smoothing + hysteresis in Steer)
            float sx = 40f + 26f * k;
            float sy = new[] { -22f, 22f, 0f }[k];
            Vector2 spot = new Vector2(lp.X + _slotSide * sx, lp.Y + sy);
            Intent = "formation slot " + (k + 1);

            // bots no longer copy the human's jumps. The only follow-jump is a ledge climb: the human has stood on
            // higher ground (more than a step, low enough to jump onto) close by for 0.5 s. Gaps: wait at the edge.
            // Being stuck is handled by the unstick logic in Think. At most one follow-jump per 1.5 s.
            if (TryLedgeJump(me, ci, lp, pos, onGround, flying, leaderGrounded, dt)) { _move = _forcedMove; return pos; }
            if (_forcedMoveT > 0f) { _move = _forcedMove; return pos; }

            // occasional idle moment when nothing is happening
            if (_idleCd <= 0f && leaderDist < 120f)
            {
                _idleCd = Jit(new[] { 9f, 13f, 18f }[Skill]);
                if (Chance(0.6f)) { _idleT = Jit(0.9f); Intent = "idle"; }
            }
            return spot;
        }

        /// <summary>the human's ground position. Jumping humans are tracked at their last grounded spot (flying ones live).</summary>
        private Vector2 LeaderGround(Player leader, float dt)
        {
            if (leader == null) { _leaderRef = null; return Vector2.Zero; }
            bool grounded = leader.IsOnGround || leader.IsFlying;
            if (leader != _leaderRef) { _leaderRef = leader; _leaderGround = leader.Position2D; _leaderAirT = 0f; }
            if (grounded) { _leaderGround = leader.Position2D; _leaderAirT = 0f; }
            else
            {
                _leaderAirT += dt;
                if (_leaderAirT > 2.0f) _leaderGround = leader.Position2D;   // long fall / knock-away: don't hang on to a stale spot
            }
            return _leaderGround;
        }

        /// <summary>jump up onto a ledge the (grounded) human stands on. Debounced to one per 1.5 s.</summary>
        private bool TryLedgeJump(Player me, CharacterInfo ci, Vector2 lp, Vector2 pos, bool onGround, bool flying, bool leaderGrounded, float dt)
        {
            if (!onGround || flying || PlanBusy || !leaderGrounded || _forcedMoveT > 0f) { _leaderHighT = 0f; return false; }
            float myFloor = 0f; try { myFloor = me.BodyComponent != null ? me.BodyComponent.FloorHeightIgnoringPits : 0f; } catch { }
            float rise = FloorAt(me, lp, myFloor) - myFloor;
            float maxRise = ci.CanDoubleJump ? 80f : 44f;
            bool ledge = rise > STEP_H && rise < maxRise && Math.Abs(lp.X - pos.X) < 110f && Math.Abs(lp.Y - pos.Y) < 40f;
            if (!ledge) { _leaderHighT = 0f; return false; }
            _leaderHighT += dt;
            if (_leaderHighT < 0.5f || _followJumpCd > 0f || PathHasPit(me, pos, lp)) return false;
            float dir = Math.Sign(lp.X - pos.X); if (dir == 0f) dir = me.HorizontalFlipped ? -1f : 1f;
            Plan("ledge jump", null, false);
            JumpWhy("follow-ledge"); Hold(A_JUMP, 0.2f);
            _forcedMove = new Vector2(dir, Math.Abs(lp.Y - pos.Y) > 8f ? Math.Sign(lp.Y - pos.Y) * 0.5f : 0f); _forcedMoveT = 0.5f;
            if (ci.CanDoubleJump && rise > 40f) At(0.24f, () => Tap(A_JUMP));
            _followJumpCd = 1.5f; _leaderHighT = 0f; Intent = "ledge jump";
            return true;
        }

        // ================================================================ engaging / fighting
        private Vector2 Engage(Player me, CharacterInfo ci, Foe t, Vector2 pos, bool onGround, bool flying, int near, int veryNear, float aggr, float now)
        {
            Vector2 tp = t.P;
            float dx = tp.X - pos.X, dy = tp.Y - pos.Y;

            // flank: choose the side of the target with fewer allies; go to its back if it is blocking
            if (_sideTarget != t.E) { _sideTarget = t.E; _side = pos.X <= tp.X ? -1 : 1; }
            int left = 0, right = 0;
            foreach (BotController b in Live) if (b != this && b.RegTarget == t.E) { if (b.RegPos.X < tp.X) left++; else right++; }
            foreach (KeyValuePair<PlayerInfo, List<IPlayer>> kv in Scene2d.Active.Players)
            {
                if (kv.Key == null || kv.Key.Controller is BotController || kv.Value == null) continue;
                foreach (IPlayer ip in kv.Value)
                    if (ip is Player h && h.IsActivePlayerObject && !h.IsKO && Vector2.Distance(h.Position2D, tp) < 90f) { if (h.Position2D.X < tp.X) left++; else right++; }
            }
            if (_side < 0 && left >= right + 1 && right == 0) _side = 1;
            else if (_side > 0 && right >= left + 1 && left == 0) _side = -1;
            if (t.Blocking && Skill > 0) _side = t.FacingRight ? -1 : 1; // its back

            bool part = t.Part != null;
            _engagePart = part || IsLargeTarget(t.E);
            // fallback anchor for MakeReachable: the part's own ground spot (mirror / shrink around it), else ourselves
            _engageAnchor = part && _partGroundOk ? tp : pos;
            float reachX = ATTACK_RANGE_X + t.ExtraReach;                     // wide boss parts are hittable anywhere along the box
            float standX = t.Air && !flying ? 26f : ATTACK_RANGE_X * 0.8f;
            if (part) standX = Math.Min(standX, Math.Max(12f, t.ExtraReach * 0.5f + 14f));
            Vector2 goal = new Vector2(tp.X + _side * standX, tp.Y);

            // can't hurt it: keep a respectful distance and wait
            if (t.Invuln)
            {
                Status = "engaging"; Intent = "wait (invulnerable)"; Suppress(part ? "part-not-hittable" : "invuln-wait");
                return part ? goal : new Vector2(tp.X + _side * 80f, tp.Y);   // a part: wait right there, it opens up again
            }

            // skin up -> no melee, shoot it from outside its reach
            if (t.Skin && !t.Boss && part == false) return EngageSkinRanged(me, ci, t, pos, onGround, flying);

            // boss wind-up: back off while it is ready to swing at close range, then punish.
            // only near the boss body (multi-part bosses swing with their parts: defense handles those), one backoff
            // per wind-up and at most one every 2.5 s, so a boss that is always winding up no longer keeps the bots away
            if (t.Boss && !flying && !part)
            {
                float bdx = Math.Abs(t.BodyP.X - pos.X);
                bool windUp = (t.Antic && bdx < 90f) || (t.E.IsReadyToAttack && bdx < 70f && !PlanBusy && Chance(0.006f * (Skill + 1)));
                if (windUp && Skill > 0 && _bossBackoffT <= 0f && now >= _bossBackoffCdUntil) { _bossBackoffT = Jit(0.8f); _bossBackoffCdUntil = now + 2.5f; }
                if (_bossBackoffT > 0f) { Status = "engaging"; Intent = "spacing (boss wind-up)"; Suppress("boss-backoff"); return new Vector2(tp.X + _side * 105f, tp.Y); }
            }
            else if (part) _bossBackoffT = 0f;

            float lane = LANE_TOLERANCE + (Skill == 0 ? 4f : 0f);
            if (part) lane = Math.Max(lane, t.LaneTol);
            bool aligned = Math.Abs(dy) <= lane;
            bool facingRight = !me.HorizontalFlipped;
            bool facing = (dx > 0f) == facingRight || Math.Abs(dx) <= 6f;

            // ---- airborne target
            if (t.Air && !flying)
            {
                Status = "anti-air";
                if (aligned && Math.Abs(dx) < 70f + t.ExtraReach * 0.5f && onGround && _airCd <= 0f && !PlanBusy)
                {
                    if (!facing) { _move = new Vector2(Math.Sign(dx), 0f); Intent = "turn to air target"; return pos; }
                    if (Chance(part ? Math.Max(0.9f, AntiAirChance) : AntiAirChance)) StartAntiAir(me, ci, t, dx);
                    _airCd = Jit((part ? 0.5f : 0.9f) / aggr);
                    return pos;
                }
                // ranged power attack at things out of reach
                if (aligned && facing && Math.Abs(dx) < 220f && t.H > 100f && !ci.Flying && me.Ammo > 0 && _powerCd <= 0f)
                {
                    Tap(A_POWER); _powerCd = Jit(2.5f); Intent = "power at high target";
                }
                Intent = "position under " + t.Label;
                return goal;
            }

            // ---- in flight: fight at altitude
            if (flying)
            {
                Status = "flying";
                bool inR = Math.Abs(dx) <= reachX && aligned;
                if (inR && !PlanBusy && _comboCd <= 0f)
                {
                    if (!facing) { _move = new Vector2(Math.Sign(dx), 0f); return pos; }
                    StartCombo(me, ci, t, near, true);
                    return pos;
                }
                Intent = "fly to " + t.Label;
                return goal;
            }

            // ---- ground target
            bool inRange = Math.Abs(dx) <= reachX && aligned;
            if (!inRange) { Status = "engaging"; Intent = "approach " + t.Label; return goal; }
            Status = "fighting";
            if (!facing) { _move = new Vector2(Math.Sign(dx), 0f); Intent = "turn"; return pos; }
            if (PlanBusy || _comboCd > 0f) { Intent = _comboCd > 0f ? "reset" : _planName; return pos; }
            if (_decideCd > 0f) return pos;
            _decideCd = Jit(ReactionTime * 0.5f);

            // counter-rushers absorb light hits and answer with their skin attack: heavy swing first
            if (TryCounterHeavy(ci, t)) return pos;

            // jump-in air combo now and then (feels human, hits enemies behind the first)
            if (onGround && _airCd <= 0f && Chance(new[] { 0.08f, 0.14f, 0.18f }[Skill]) && !t.Boss)
            {
                StartJumpIn(me, ci, t, dx); _airCd = Jit(3f / aggr);
                return pos;
            }
            StartCombo(me, ci, t, near, false);
            return pos;
        }

        // ---------------------------------------------------------------- combos
        private void StartCombo(Player me, CharacterInfo ci, Foe t, int near, bool inAir)
        {
            int chain = Math.Max(2, Math.Min(6, GameRefl.StandardAttackCount(me)));
            int hits = chain;
            if (Skill == 0) hits = 2 + _rng.Next(Math.Max(1, chain - 1));
            else if (Skill == 1 && Chance(0.3f)) hits = chain - 1;
            if (Chance(Sloppy)) hits = Math.Max(1, hits - 2);
            float gap = (Skill == 0 ? 0.19f : 0.14f);
            Plan(inAir ? "air string" : $"combo x{hits}", t.E, false);
            float at = 0f;
            for (int i = 0; i < hits; i++) { At(at, () => Tap(A_ATTACK)); at += Jit(gap, 0.2f); }

            // ender
            string ender = inAir ? "" : PickEnder(me, ci, t, near, hits >= chain - 1);
            float e = at + 0.02f;
            switch (ender)
            {
                case "power": At(e, () => { Tap(A_POWER); Intent = "ender: power"; }); _powerCd = Jit(1.5f); break;
                case "launcher":
                    At(e, () => { JumpWhy("ender-launcher"); Tap(A_JUMP); Tap(A_ATTACK); Intent = "ender: launcher"; });
                    At(e + 0.32f, () => Tap(A_ATTACK)); At(e + 0.5f, () => Tap(A_ATTACK)); _airCd = 1.5f; break;
                case "heavy": At(e, () => { Hold(A_ATTACK, ci.HeavyAttackStartChargingHoldDuration + 0.15f); Intent = "ender: heavy"; }); break;
                case "team-attack": At(e, () => { Hold(A_SWAP, 0.06f); Tap(A_ATTACK); Intent = "ender: team attack"; Stat("team-attack"); Stat("assist"); }); _teamCd = Jit(6f); break;
                case "team-power": At(e, () => { Hold(A_SWAP, 0.06f); Tap(A_POWER); Intent = "ender: team power"; Stat("team-power"); Stat("assist"); }); _teamCd = Jit(7f); break;
                case "team-launcher": At(e, () => { JumpWhy("team-launcher"); Hold(A_SWAP, 0.06f); Tap(A_JUMP); Intent = "ender: team launcher"; Stat("team-launcher"); Stat("assist"); }); _teamCd = Jit(8f); break;
            }
            float pause = new[] { 0.45f, 0.28f, 0.18f }[Skill] / Aggr;
            At(e + (ender.Length > 0 ? 0.45f : 0.1f), () => { _comboCd = Jit(pause, 0.5f); });
            Stat(ender.Length > 0 ? "ender-" + ender : "combo");
        }

        private string PickEnder(Player me, CharacterInfo ci, Foe t, int near, bool fullChain)
        {
            if (!fullChain || !Chance(EnderChance)) return "";
            var opts = new List<KeyValuePair<string, float>>();
            if (me.Ammo > 0 && _powerCd <= 0f) opts.Add(new KeyValuePair<string, float>("power", near >= 2 || t.Armored || t.Blocking ? 3f : 1.5f));
            bool hasRising = ci.AttackRising != null && ci.AttackRising.GetAttack() != null;
            if (hasRising && !t.Boss && !t.Armored) opts.Add(new KeyValuePair<string, float>("launcher", 1.5f));
            bool hasHeavy = ci.HeavySwingAttack != null && ci.HeavySwingAttack.GetAttack() != null;
            if (hasHeavy && (t.Blocking || t.Armored)) opts.Add(new KeyValuePair<string, float>("heavy", 2f));
            if (_teamCd <= 0f && Chance(TeamChance) && PartnerReady(me))
            {
                Player partner = me.SecondaryPlayer; CharacterInfo pci = partner?.CurrentCharacterInfo;
                if (pci != null)
                {
                    if (pci.TeamHelpAttackAction != null && pci.TeamHelpAttackAction.EnergyCost <= me.Energy) opts.Add(new KeyValuePair<string, float>("team-attack", 2f));
                    if (pci.TeamHelpPowerAttackAction != null && pci.TeamHelpPowerAttackAction.EnergyCost <= me.Energy && near >= 2) opts.Add(new KeyValuePair<string, float>("team-power", 1.5f));
                    bool launcher = ci.TeamLauncherAttack != null && ci.TeamLauncherAttack.GetAttack() != null;
                    if (launcher && pci.TeamHelpLauncherAction != null && pci.TeamHelpLauncherAction.EnergyCost <= me.Energy && !t.Boss) opts.Add(new KeyValuePair<string, float>("team-launcher", 1f));
                }
            }
            opts.Add(new KeyValuePair<string, float>("", 1f));
            float sum = 0f; foreach (var o in opts) sum += o.Value;
            float r = R() * sum;
            foreach (var o in opts) { r -= o.Value; if (r <= 0f) return o.Key; }
            return "";
        }

        private bool PartnerReady(Player me)
        {
            try
            {
                if (Info.HeroLimit <= 1 || me.SecondaryPlayer == null || me.SecondaryPlayer.IsKO) return false;
                return me.HeroReadyForTeamSwapOrHelp((Info.CurrentHeroIndex + 1) % Info.HeroLimit);
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- jumping attacks
        private void StartAntiAir(Player me, CharacterInfo ci, Foe t, float dx)
        {
            bool hasRising = ci.AttackRising != null && ci.AttackRising.GetAttack() != null;
            float dir = Math.Sign(dx);
            if (hasRising && Math.Abs(dx) < 44f && t.H < 90f && Chance(0.6f))
            {
                // rising attack: jump + attack on the same frame while grounded
                Plan("rising attack", t.E, true);
                At(0f, () => { JumpWhy("anti-air-rising"); Tap(A_JUMP); Tap(A_ATTACK); });
                At(0.34f, () => Tap(A_ATTACK));
                At(0.5f, () => Tap(A_ATTACK));
                Stat("anti-air-rising");
                return;
            }
            // jump toward it (short hold: never long enough to trigger flight by accident), double jump if it is high, then air attacks
            Plan("jump attack", t.E, true);
            At(0f, () => { JumpWhy("anti-air"); Hold(A_JUMP, 0.16f); _forcedMove = new Vector2(dir, 0f); _forcedMoveT = 0.4f; });
            bool dbl = ci.CanDoubleJump && t.H > 55f;
            if (dbl) At(0.24f, () => Tap(A_JUMP));
            float a = dbl ? 0.34f : 0.2f;
            At(a, () => Tap(A_ATTACK));
            At(a + 0.15f, () => Tap(A_ATTACK));
            if (me.Ammo > 0 && Chance(0.25f)) At(a + 0.32f, () => Tap(A_POWER));
            Stat(dbl ? "anti-air-double" : "anti-air-jump");
        }

        private void StartJumpIn(Player me, CharacterInfo ci, Foe t, float dx)
        {
            float dir = Math.Sign(dx);
            Plan("jump-in air combo", t.E, true);
            At(0f, () => { JumpWhy("jump-in"); Hold(A_JUMP, 0.14f); _forcedMove = new Vector2(dir, 0f); _forcedMoveT = 0.35f; });
            At(0.22f, () => Tap(A_ATTACK));
            At(0.5f, () => Tap(A_ATTACK)); // lands into the ground string
            At(0.64f, () => Tap(A_ATTACK));
            At(0.8f, () => { _comboCd = Jit(0.3f / Aggr); });
            Stat("jump-in");
        }

        // ================================================================ defense
        private static bool BossOpen(Foe t)
        {
            try { return t.E.IsStaggered || t.E.IsInStateHurt || t.E.IsInStateKnockdown; } catch { return false; }
        }

        /// <summary>A boss winding up near this bot or a human (specials are a safe answer to it).</summary>
        private bool BigThreatIncoming(List<Foe> foes, Player me)
        {
            foreach (Foe f in foes)
            {
                if (!f.Boss || !f.Antic) continue;
                if (Vector2.Distance(f.P, me.Position2D) < 150f) return true;
                foreach (Player h in _humansBuf) if (Vector2.Distance(f.P, h.Position2D) < 150f && h.HPRatio < 0.5f) return true;
            }
            return false;
        }

        // ================================================================ tag team
        private bool TrySwap(Player me, CharacterInfo ci, List<Foe> foes, int airNear, int near, float now)
        {
            if (_swapCd > 0f || me.IsInStateHurt || !me.IsOnGround) return false;
            Player partner = me.SecondaryPlayer;
            if (partner == null || partner.IsKO || !PartnerReady(me)) return false;
            if (me.Energy < GameSettings.Singleton.HeroSwapEnergyCost) return false;
            CharacterInfo pci = partner.CurrentCharacterInfo;
            string why = null;
            if (me.HPRatio < 0.3f && partner.HPRatio > me.HPRatio + 0.2f) why = "low HP";
            else if (airNear >= 2 && !ci.Flying && pci != null && pci.Flying && Skill > 0) why = "flyer partner vs air enemies";
            else if (near > 0 && now - _lastSwapTime > Jit(45f) && partner.HPRatio >= me.HPRatio - 0.05f && Chance(0.5f)) why = "variety";
            if (why == null) { _swapCd = 1f; return false; }
            Tap(A_SWAP);
            _swapCd = Jit(why == "low HP" ? 8f : 15f); _lastSwapTime = now;
            Status = "swapping"; Intent = "tag out (" + why + ")";
            Stat("swap-" + why.Split(' ')[0]);
            return true;
        }

        // ================================================================ specials
        private bool HasSpecial(Player me)
        {
            int cost = GameRefl.SpecialCost(me);
            if (cost < 0) return false;
            try { return me.HasEnergyRequired(cost, true); } catch { return false; }
        }

        private void TrySpecial(Player me, List<Foe> foes, Foe target, int near, Player needy, float lowestHumanHp, Enemy rescueFrom, float now, float dt)
        {
            bool has = RegHasSpecial;
            if (!has || _specialCd > 0f || me.IsInSpecialAttack || foes.Count == 0) return;

            bool singleWeak = foes.Count == 1 && foes[0].E.HPRatio < 0.35f && !foes[0].Boss;
            string why = null;
            int crowd = new[] { 4, 3, 3 }[Skill];
            if (near >= crowd) why = "crowd";
            else if (target != null && target.Boss && target.E.HPRatio > 0.12f && Math.Abs(target.P.X - me.Position2D.X) < 90f && Chance(0.03f * (Skill + 1))) why = "boss";
            else if (rescueFrom != null && needy != null && lowestHumanHp < 0.3f && Vector2.Distance(needy.Position2D, me.Position2D) < 180f) why = "rescue";
            else if (target != null && target.Boss && Math.Abs(target.P.X - me.Position2D.X) < 110f && BossOpen(target) && Chance(0.05f * (Skill + 1))) why = "boss staggered";
            else if (BigThreatIncoming(foes, me) && Chance(0.01f * (Skill + 1))) why = "big threat";
            else if (_energyFullT > 6f && near >= 1 && !singleWeak) why = "meter full";
            if (why == null) return;
            if (singleWeak && why != "rescue") return;

            // start a team-up if another bot also has a special ready, else fire alone
            bool partnerBot = false;
            foreach (BotController b in Live) if (b != this && b.RegHasSpecial && b.ActivePlayer != null && !b.ActivePlayer.IsKO) partnerBot = true;
            if (partnerBot && why != "rescue") BeginSpecialHold(false, "team special (" + why + ")");
            else { Hold(A_SPECIAL, 0.05f); _specialCd = 6f; Intent = "special (" + why + ")"; Stat("special-" + why.Split(' ')[0]); }
        }

        private void BeginSpecialHold(bool join, string why)
        {
            AbortPlan();
            _specialHolding = true; _specialJoin = join; _specialHoldT = 0f;
            _holds[A_SPECIAL] = 99f; Tap(A_SPECIAL);
            RegHoldingSpecial = !join;
            Intent = why; Stat(join ? "teamspecial-join" : "teamspecial-start");
        }

        private void UpdateSpecialHold(Player me, List<Player> humans, float now, float dt)
        {
            if (!_specialHolding) return;
            _specialHoldT += dt;
            bool done = me.IsInSpecialAttack || me.WasSpecialAlreadyReleased;
            bool release;
            if (_specialJoin)
            {
                // keep holding while someone else is charging; they trigger the team special for everyone ready
                bool other = false;
                foreach (Player h in humans) if (GameRefl.TeamUpReady(h)) other = true;
                foreach (BotController b in Live) if (b != this && b.RegHoldingSpecial) other = true;
                release = done || (!other && _specialHoldT > 0.35f) || _specialHoldT > 4f;
            }
            else
            {
                float need = GameSettings.Singleton.SpecialTeamUpHoldDuration + 0.45f; // give joiners time to become ready
                release = done || _specialHoldT > need;
            }
            if (release)
            {
                _holds.Remove(A_SPECIAL);
                _specialHolding = false; RegHoldingSpecial = false; _specialCd = 6f;
            }
            else { Status = "special"; Intent = _specialJoin ? "holding for team special" : "charging team special"; }
        }

        // ================================================================ registry + stats
        private void Register(Player me, float now)
        {
            if (!Live.Contains(this)) Live.Add(this);
            RegPos = me.Position2D; RegSeen = now;
            Live.RemoveAll(b => b == null || BotManager.Time - b.RegSeen > 1.5f || b.Info == null || b.Info.Controller != b);
        }

        private void Unregister()
        {
            Live.Remove(this);
            RegTarget = null; RegHoldingSpecial = false; RegHasSpecial = false;
            _specialHolding = false;
        }

        public readonly BotStats Stats = new BotStats();

        private void Stat(string key) => Stats.Add(key);

        private void TrackHeroAndStats(Player me, CharacterInfo ci, float dt, float now)
        {
            if (_lastHero != ci)
            {
                if (_lastHero != null && _lastMe != null && _lastMe.PlayerInfo == me.PlayerInfo) Stats.Add("hero-swaps");
                _lastHero = ci;
                AbortPlan(); _flightEntering = false;
            }
            if (_lastMe != me) { _lastMe = me; _lastState = ""; _lastFlying = me.IsFlying; }
            string st = GameRefl.StateName(me);
            if (st != _lastState)
            {
                switch (st)
                {
                    case "StateJump": Stats.Add("jumps"); Stats.Add("jump-reason:" + (_jumpWhy != null && now - _jumpWhyT < 0.6f ? _jumpWhy : "untagged")); break;
                    case "StateAirAttack": Stats.Add("air-attacks"); break;
                    case "StateAttackRising": Stats.Add("rising-attacks"); break;
                    case "StateLauncher": Stats.Add("team-launchers"); break;
                    case "StateAttack": if (_lastState != "StateAttack") Stats.Add("combos"); break;
                    case "StateAttackFinisher": Stats.Add("finishers"); break;
                    case "StateSpecialAttack": Stats.Add(me.HasSpecialTeamUpBonus ? "team-specials" : "specials"); break;
                    case "StateDodge": Stats.Add("dodges"); break;
                    case "StateBlock": Stats.Add("blocks"); break;
                    case "StateParry": Stats.Add("parries"); break;
                    case "StateHeavySwingAttack": Stats.Add("heavy"); break;
                    case "StateGrab": case "StateAirGrab": Stats.Add("grabs"); break;
                    default: if (st.IndexOf("PowerAttack", StringComparison.Ordinal) >= 0) Stats.Add("power"); break;
                }
                _lastState = st;
            }
            if (me.IsFlying) { Stats.FlightSeconds += dt; if (!_lastFlying) Stats.Add("flights"); }
            _lastFlying = me.IsFlying;
            Stats.Hero = BotManager.HeroName(Paris.Game.System.CharacterManager.Singleton.Characters.IndexOf(ci));
        }

    }

    /// <summary>Per-bot counters (kept as a no-op shell: nothing is recorded or logged in the release build).</summary>
    public class BotStats
    {
        public float FlightSeconds;
        public int Reversals, FacingFlips, IdleFacingFlips, WallStuck, PitAvoid;
        public string Hero = "?";
        internal const string JumpPrefix = "jump-reason:", SuppPrefix = "supp:", HitByPrefix = "hitby:", NdrPrefix = "ndr:", ToPrefix = "to:";
        public void Add(string k) { }
        public void AddTime(string k, float dt) { }
    }
}
