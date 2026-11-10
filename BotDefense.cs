using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Paris.Engine;
using Paris.Engine.Combat;
using Paris.Engine.Graphics.Animations;
using Paris.Engine.Scene;
using Paris.Game.Actor;
using Paris.Game.Attacks;
using Paris.Game.Damage;
using Paris.Game.Data;

namespace CosmicBots
{
    /// <summary>
    /// threat-driven defense. Every frame the bot looks for incoming hits using the enemy's real attack state
    /// (current attack, attack phase, and the frames of its attack animation: time until the first frame that carries
    /// an attack/grab box) and hostile projectiles (velocity -> ETA). One response is chosen per threat:
    ///   parry  - press block so the hit lands inside the game's parry window (BlockParryWindowDuration, 0.4 s after the
    ///            block starts); on success the game enters StateParry and the bot mashes Attack for the parry chain.
    ///   block  - hold block through unparryable / multi-hit attacks, release right after (no turtling).
    ///   dodge  - Dodge-type heroes, and anything unblockable / grab / shield-breaking: timed for the perfect-dodge
    ///            window, away from the attacker unless a hole is there (then through it, or a hop).
    ///   assist - the partner hero's "Cosmic Assist" defense (TeamSwap + Dodge/Block/Parry, TeamHelp.Defense).
    ///   jump   - hop over low projectiles / grabs.
    /// Outcomes are counted from Harmony postfixes on Character.Block/Parry/DodgeIncomingAttack and InternalHurt.
    /// parry timing: the game parries when the hit lands on the block's ShieldBox while Player._blockStartTimer <=
    /// BlockParryWindowDuration (0.4 s) and attacker / defender face each other. The bot now (a) aims the press so the block
    /// is ~0.15-0.25 s old at impact, never less than the time the shield box needs to appear (BlockStartAnim frames + input
    /// latency), (b) re-reads the attacker's animation every frame and re-times the press (and the hold) when the swing clip
    /// starts, (c) re-arms once if the block would be too old at impact, (d) holds through the impact, (e) only reacts to
    /// swings whose hit boxes actually reach this bot, and (f) logs why a parry attempt failed (too early / too late /
    /// no hit / through guard / other attacker).
    /// </summary>
    public partial class BotController
    {
        internal sealed class Threat
        {
            public object Src;               // Enemy or Projectile
            public Enemy From;               // attacker (null for projectiles)
            public float X, H;               // attacker / projectile X and height
            public float HitAt;              // predicted time of impact (BotManager.Time)
            public bool CanBlock = true, CanParry = true, Breaker, Grab, Proj, Multi, Boss, Low, Skin;   // Skin = standing inside a skin enemy's reach
            public bool Guess;               // timing guessed (wind-up clip without hit boxes), not read from frames
            public MeleeAttackStats Atk;     // the attack this threat was read from (re-timing / attack-over check)
            public int Hits = 1; public float HitGap;
            public string Kind => Grab ? "grab" : Proj ? "projectile" : Boss ? "boss" : "melee";
        }

        private sealed class DefenseAction
        {
            public string How;               // parry / block / dodge / jump / assist / retreat
            public Threat T;
            public float PressAt, ReleaseAt, ResolveAt;
            public bool Pressed, NeedFace, Hurt, Parried, Blocked, Dodged;
            public float Dir;                // dodge / jump / retreat X direction
            // parry bookkeeping
            public float Lead;               // wanted press-to-impact time
            public float PressedAt = -10f, DecidedAt, ShieldUp, BlockTimerAtHit = -1f, HurtAt = -1f;
            public int Retimed, Rearms;
            public bool OwnAttackAtPress, AttackOver, OtherAttacker, GaveUp;
            public string StateAtHurt = "";
        }

        private DefenseAction _def;
        private readonly Dictionary<object, float> _threatSeen = new Dictionary<object, float>();
        private readonly Dictionary<Enemy, MeleeAttackStats> _threatAttack = new Dictionary<Enemy, MeleeAttackStats>();
        private float _lastHurtAt = -10f, _parriedAt = -10f;

        // parry lead = time from pressing block to the hit landing. Centre / spread per skill (Easy / Normal / Hard);
        // Normal and Hard are clamped into [shield-up + 30 ms, window - 60 ms]; Easy may still miss either way.
        private float ParryLeadCentre => new[] { 0.24f, 0.20f, 0.18f }[Skill];
        private float ParryLeadSpread => new[] { 0.17f, 0.06f, 0.03f }[Skill];
        private const float FRAME = 1f / 60f;
        private static readonly Dictionary<CharacterInfo, float> _shieldUp = new Dictionary<CharacterInfo, float>();
        private int _parryFailLogs;
        private float DodgeLeadMin => new[] { 0.00f, 0.04f, 0.06f }[Skill];
        private float DodgeLeadMax => new[] { 0.40f, 0.24f, 0.17f }[Skill];
        private const float THREAT_HORIZON = 0.9f;

        // ---------------------------------------------------------------- outcome hooks (called from GamePatches postfixes)
        internal static void OnDefenseEvent(Character victim, int kind)
        {
            try
            {
                if (!(victim is Player p) || !(p.PlayerInfo?.Controller is BotController b)) return;
                b.DefenseEvent(kind, p);
            }
            catch { }
        }

        /// <summary>kind: 0 hurt, 4 blocked, 5 parried, 9 dodged (same numbers as AttackResult).</summary>
        private void DefenseEvent(int kind, Player p)
        {
            float now = BotManager.Time;
            switch (kind)
            {
                case 0:
                {
                    Stat("hits-taken"); _lastHurtAt = now;
                    GameObject2d by = null; try { by = p.LastAttacker; } catch { }
                    if (by is Projectile pr) { try { by = pr.Owner; } catch { } }
                    _lastHurtBy = by as Enemy;
                    if (_def != null && !_def.Hurt)
                    {
                        _def.HurtAt = now; _def.StateAtHurt = _lastState ?? "";
                        _def.OtherAttacker = _def.T.From != null && by != null && !ReferenceEquals(by, _def.T.From);
                        if (_def.Pressed) _def.Hurt = true;
                    }
                    break;
                }
                case 4:
                    Stat("block-ok");
                    if (_def != null) { _def.Blocked = true; if (_def.BlockTimerAtHit < 0f) _def.BlockTimerAtHit = GameRefl.BlockTimer(p); }
                    break;
                case 5: Stat("parry-ok"); _parriedAt = now; if (_def != null) { _def.Parried = true; _def.BlockTimerAtHit = GameRefl.BlockTimer(p); } break;
                case 9: Stat("dodge-iframe"); if (_def != null) _def.Dodged = true; break;
            }
        }

        /// <summary>
        /// time from pressing block until the block's ShieldBox (the parry/guard hurtbox) exists: the first frame of
        /// CharacterInfo.BlockStartAnim with a Shield vulnerability box, plus two frames (the press is read on the next game
        /// tick, then StateBlock starts). Cached per hero.
        /// </summary>
        private static float ShieldUpDelay(Player me, CharacterInfo ci)
        {
            if (ci == null) return 2f * FRAME;
            if (_shieldUp.TryGetValue(ci, out float v)) return v;
            float d = 0f; bool found = false;
            try
            {
                string id = ci.BlockStartAnim?.ID;
                Animator2dData data = me.Animator?.AnimationData;
                if (!string.IsNullOrEmpty(id) && data != null && data.AnimationIndexesByName.TryGetValue(id, out int idx) && idx >= 0 && idx < data.Animations.Count)
                {
                    var frames = data.Animations[idx].Frames; float t = 0f;
                    for (int i = 0; i < frames.Count && t < 0.5f; i++)
                    {
                        var fr = frames[i]; bool sh = false;
                        if (fr.VulnerabilityBoxes != null)
                            foreach (var hb in fr.VulnerabilityBoxes) if (hb != null && hb.BoxType == Animator2dData.HitBox.HitBoxType.Shield) { sh = true; break; }
                        if (sh) { d = t; found = true; break; }
                        t += fr.Duration;
                    }
                }
            }
            catch { }
            d = Math.Min(d, 0.2f);
            float res = d + 2f * FRAME;
            _shieldUp[ci] = res;
            Log.Verbose($"[defense] {Menu.Sanitize(ci.InternalName ?? "?")}: block shield box up {d * 1000f:0} ms into the block" + (found ? "" : " (none found in the start anim; assuming immediate)") + $", parry press lead >= {(res + 0.03f) * 1000f:0} ms");
            return res;
        }

        private float PickParryLead(float shieldUp, bool guess)
        {
            float win = ParryWindow();
            float lo = shieldUp + 0.03f, hi = Math.Max(lo, win - 0.06f);
            if (guess) return Skill == 0 ? MathHelper.Clamp(ParryLeadCentre + (R() * 2f - 1f) * ParryLeadSpread, 0f, win + 0.1f) : (lo + hi) * 0.5f;
            float lead = ParryLeadCentre + (R() * 2f - 1f) * ParryLeadSpread;
            return Skill == 0 ? MathHelper.Clamp(lead, 0f, win + 0.15f) : MathHelper.Clamp(lead, lo, hi);
        }

        // ---------------------------------------------------------------- threat detection
        private Threat FindThreat(Player me, List<Foe> foes, Scene2d scene, Vector2 pos, float now)
        {
            Threat best = null;
            float myFloor = 0f; try { myFloor = me.BodyComponent.FloorHeightIgnoringPits; } catch { }
            foreach (Foe f in foes)
            {
                Enemy e = f.E;
                MeleeAttackStats atk = null; AttackPhase ph = AttackPhase.None;
                try { atk = e.CurrentAttack; ph = e.CurrentAttackPhase; } catch { }
                if (f.Rush && _rushHandled) continue;   // the lane escape (BotRush.cs) owns Annihilus' Negative Rush
                if (f.Skin && !f.Boss)
                {
                    // damaging skin: contact range, any facing, no wind-up to wait for. Inside its reach = hit now.
                    try
                    {
                        float sdx = pos.X - f.BodyP.X, sdy = Math.Abs(pos.Y - f.BodyP.Y);
                        if (f.BodyH <= 90f && sdy <= f.SkinDepth + 8f && Math.Abs(sdx) <= f.SkinReach + HalfWidth(me) + 6f &&
                            !(_threatSeen.TryGetValue(e, out float sUntil) && now < sUntil))
                        {
                            var st = new Threat { Src = e, From = e, X = f.BodyP.X, H = f.BodyH, HitAt = now + 0.04f, Atk = atk, Skin = true, CanBlock = false, CanParry = false };
                            if (best == null || st.HitAt < best.HitAt) best = st;
                        }
                    }
                    catch { }
                    continue;
                }
                if (atk == null || (ph != AttackPhase.Antic && ph != AttackPhase.Delivery)) continue;
                Vector2 fp = f.BodyP;                                                     // the attacker's own position, not a boss part we aim at
                float dx = pos.X - fp.X, dy = Math.Abs(pos.Y - fp.Y);
                if ((dx > 0f) != f.FacingRight && Math.Abs(dx) > 10f) continue;           // not facing us
                bool timed = AttackTiming(e, atk, out float eta, out float boxReach, out bool grabBox, out int hits, out float gap, out float boxNear, out float boxFar);
                if (!timed) eta = ph == AttackPhase.Delivery ? 0.03f : -1f;
                if (eta < 0f || eta > THREAT_HORIZON) continue;
                bool guess = !timed || boxReach < 0f;
                if (guess && _guessCd.TryGetValue(e, out float gcd) && now < gcd) continue;  // a guessed guard just whiffed vs this enemy
                float depth = 22f; try { depth = Math.Max(16f, Math.Min(48f, atk.Depth.Max)) + 8f; } catch { }
                if (dy > depth) continue;
                Player aimed = null; try { aimed = e.Target; } catch { }
                bool atMe = ReferenceEquals(aimed, me);
                if (!guess && boxFar > boxNear)
                {
                    // will the swing's hit boxes (first hit window, enemy's facing) cover our body at impact?
                    float sign = f.FacingRight ? 1f : -1f, fwd = (pos.X - fp.X) * sign;
                    float adv = 0f; try { adv = MathHelper.Clamp(e.BodyComponent.Displacement.Speed.X * sign * eta, -40f, 90f); } catch { }
                    float hw = HalfWidth(me), slack = atMe ? 14f : 6f;
                    if (fwd < boxNear + adv - hw - slack || fwd > boxFar + adv + hw + slack) continue;
                }
                else
                {
                    float reach = Math.Min(f.Reach, 90f) + (atMe ? 15f : 6f);   // guessed timing: close range only
                    if (Math.Abs(dx) > reach) continue;
                    if (aimed != null && !atMe && Math.Abs(dx) > 40f) continue;  // winding up at somebody else
                }
                if (_threatSeen.TryGetValue(e, out float seenUntil) && now < seenUntil &&
                    _threatAttack.TryGetValue(e, out MeleeAttackStats seenAtk) && ReferenceEquals(seenAtk, atk)) continue;
                var t = new Threat { Src = e, From = e, X = fp.X, H = f.BodyH, HitAt = now + eta, Boss = f.Boss, Grab = grabBox, Hits = Math.Max(1, hits), HitGap = gap, Guess = guess, Atk = atk };
                try
                {
                    var dt = atk.DamageType?.GetDamageType();
                    if (dt != null) { t.CanBlock = dt.CanBeBlocked; t.CanParry = dt.CanBeParried; t.Breaker = dt.IsShieldBreaker; }
                    if (atk.IsUsedForGrabDetection || (atk is EnemyAttackStats eas && eas.CanInitiateGrab)) t.Grab = true;
                    if (atk.MultiHitCount > 1) { t.Hits = Math.Max(t.Hits, atk.MultiHitCount); t.HitGap = Math.Max(t.HitGap, atk.MultiHitDelay); }
                }
                catch { }
                if (f.State != null && f.State.IndexOf("Grab", StringComparison.Ordinal) >= 0) t.Grab = true;
                t.Multi = t.Hits > 1;
                if (best == null || t.HitAt < best.HitAt) best = t;
            }
            // hostile projectiles flying at us
            foreach (object o in scene.GetGameObjectsOfType<Projectile>())
            {
                if (!(o is Projectile pr) || !pr.Active || pr.IsDead || pr.Owner is Player) continue;
                if (_threatSeen.TryGetValue(pr, out float until) && now < until) continue;
                Vector2 pp = pr.Position2D;
                float dx = pos.X - pp.X, dy = Math.Abs(pos.Y - pp.Y);
                if (Math.Abs(dx) > 260f || dy > 24f) continue;
                float vx = 0f; try { vx = pr.BodyComponent.Displacement.Speed.X; } catch { }
                if (Math.Abs(vx) < 20f || Math.Sign(vx) != Math.Sign(dx)) continue;   // not coming at us
                float h = 0f; try { h = pr.Position.Z - pr.BodyComponent.FloorHeightIgnoringPits; } catch { }
                if (me.IsOnGround && h > 90f) continue;                               // sails overhead
                float eta = Math.Max(0f, Math.Abs(dx) - 14f) / Math.Abs(vx);
                if (eta > THREAT_HORIZON) continue;
                var t = new Threat { Src = pr, X = pp.X, H = h, HitAt = now + eta, Proj = true, Low = h < 24f };
                DamageInfoEx di = GameRefl.ProjectileDamage(pr);
                if (di != null) { t.CanBlock = di.CanBeBlocked; t.CanParry = di.CanBeParried; t.Breaker = di.IsShieldBreaker; }
                if (best == null || t.HitAt < best.HitAt) best = t;
            }
            return best;
        }

        /// <summary>
        /// Reads the attacker's current animation: time until the first frame that carries an attack box (0 if one is
        /// active now), how far the box reaches, whether it is a grab box and how many separate hit windows follow.
        /// </summary>
        private static bool AttackTiming(Enemy e, MeleeAttackStats atk, out float eta, out float reach, out bool grab, out int hits, out float gap)
            => AttackTiming(e, atk, out eta, out reach, out grab, out hits, out gap, out _, out _);

        /// <param name="near">closest forward edge (px, in the attacker's facing direction) of the first hit window's boxes</param>
        /// <param name="far">farthest forward edge of the first hit window's boxes</param>
        private static bool AttackTiming(Enemy e, MeleeAttackStats atk, out float eta, out float reach, out bool grab, out int hits, out float gap, out float near, out float far)
        {
            eta = -1f; reach = 0f; grab = false; hits = 0; gap = 0f; near = float.MaxValue; far = float.MinValue;
            try
            {
                Animator2d a = e.GetAttackAnimator(atk) ?? e.Animator;
                if (a == null || a.AnimationData == null) return false;
                int id = a.CurrentAnimationID;
                if (id < 0 || id >= a.AnimationData.Animations.Count) return false;
                var frames = a.AnimationData.Animations[id].Frames;
                int cur = a.CurrentFrameID;
                if (cur < 0 || cur >= frames.Count) return false;
                float speed = a.PlaySpeed > 0.05f ? a.PlaySpeed : 1f;
                float scale = a.Scale > 0.05f ? a.Scale : 1f;
                float t = 0f; bool inBox = false; float lastEnd = -1f;
                for (int i = cur; i < frames.Count; i++)
                {
                    var fr = frames[i];
                    float dur = i == cur ? Math.Max(0f, a.Delay) : fr.Duration;
                    bool any = false;
                    if (fr.AttackBoxes != null)
                        foreach (var hb in fr.AttackBoxes)
                        {
                            if (hb == null) continue;
                            if (hb.BoxType == Animator2dData.HitBox.HitBoxType.BodyAttack) continue;   // contact damage, not a swing
                            any = true;
                            if (hb.BoxType == Animator2dData.HitBox.HitBoxType.Grab) grab = true;
                            Rectangle r = hb.Box;
                            reach = Math.Max(reach, Math.Max(Math.Abs(r.Left), Math.Abs(r.Right)));
                            if (hits == 0 || (hits == 1 && inBox)) { near = Math.Min(near, r.Left * scale); far = Math.Max(far, r.Right * scale); }
                        }
                    if (any && !inBox)
                    {
                        hits++;
                        if (hits == 1) eta = (i == cur ? 0f : t) / speed;
                        else if (hits == 2) gap = (t - lastEnd) / speed;
                    }
                    if (!any && inBox) lastEnd = t;
                    inBox = any;
                    t += dur;
                    if (t / speed > 2.0f) break;
                }
                reach = Math.Min(reach, 220f);
                if (near > far) { near = 0f; far = 0f; }
                if (eta < 0f && e.CurrentAttackPhase == AttackPhase.Antic)
                {
                    // wind-up animation without hit boxes (the swing is a separate clip): assume it lands as it ends
                    eta = t / speed + 0.03f; reach = -1f; hits = 1; near = far = 0f;
                }
                return eta >= 0f;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- choosing a response
        private void DecideDefense(Player me, CharacterInfo ci, Threat t, Vector2 pos, bool onGround, float now)
        {
            _threatSeen[t.Src] = t.HitAt + 0.35f;
            if (t.From != null) _threatAttack[t.From] = t.Atk ?? t.From.CurrentAttack;
            if (_threatSeen.Count > 96) { _threatSeen.Clear(); _threatAttack.Clear(); }
            Stat("threats");
            float eta = t.HitAt - now;
            float react = t.Skin ? 0f : Jit(ReactionTime, 0.3f);        // skin contact: no reaction delay
            float p = DefendChance * (eta < react ? 0.5f : 1f) + (t.Boss ? 0.08f : 0f);
            if (!t.Skin && !Chance(Math.Min(0.97f, p))) { return; }                                      // didn't notice it (human-like)
            if (!onGround || me.IsFlying || me.IsInSpecialAttack || _specialHolding) { return; }
            float earliest = now + react;
            if (earliest > t.HitAt + 0.02f) { return; }                                         // far too late

            bool guardCapable = false; try { guardCapable = me.CanDodgeBlockParry(); } catch { }
            bool blockHero = ci.Defense == Player.DefenseType.BlockParry && guardCapable;
            bool dodgeHero = ci.Defense == Player.DefenseType.Dodge && guardCapable;
            bool shieldHero = GameRefl.HasHardShield(me);
            bool guardable = !t.Grab && !t.Breaker && (t.CanBlock || shieldHero);
            float away = Math.Sign(pos.X - t.X); if (away == 0f) away = me.HorizontalFlipped ? 1f : -1f;

            var d = new DefenseAction { T = t, Dir = away, DecidedAt = now };

            // the partner's "Cosmic Assist" defense for big / unblockable hits
            if (!t.Skin && (t.Boss || !guardable) && AssistAffordable(me, AssistKind.Defense, me.HPRatio < 0.35f) && Chance(TeamChance * 1.3f))
            { d.How = "assist"; d.PressAt = earliest; }
            else if (blockHero && guardable && !t.Skin && !(t.Proj && t.Low && Chance(0.25f)))
            {
                bool wantParry = t.CanParry && !t.Multi && !(Skill == 0 && Chance(0.35f));
                d.ShieldUp = ShieldUpDelay(me, ci);
                // the shield has to be up before impact; a press that cannot make it is not a parry attempt
                if (wantParry && Skill > 0 && t.HitAt - earliest < d.ShieldUp + 0.01f)
                {
                    if (t.HitAt - earliest < d.ShieldUp - 0.02f) { Stat("def-too-late"); return; }
                    wantParry = false;   // borderline: hold block, it may still guard
                }
                float lead = wantParry ? PickParryLead(d.ShieldUp, t.Guess) : 0.2f + R() * 0.3f;
                d.How = wantParry ? "parry" : "block";
                d.Lead = lead;
                d.PressAt = Math.Max(earliest, t.HitAt - lead);
                d.ReleaseAt = t.HitAt + HoldAfter(t);
                d.NeedFace = true;
            }
            else if (dodgeHero && !(t.Proj && t.Low && Chance(0.5f)))
            {
                d.How = "dodge";
                d.PressAt = Math.Max(earliest, t.HitAt - (DodgeLeadMin + R() * (DodgeLeadMax - DodgeLeadMin)));
                d.Dir = DodgeDirection(me, pos, away, t);
                if (d.Dir == 0f) { d.How = "jump"; d.Dir = 0f; d.PressAt = Math.Max(earliest, t.HitAt - 0.16f); }
            }
            else if (t.Grab || (t.Proj && t.Low) || Chance(t.Skin ? 0.3f : 0.6f))
            {
                d.How = "jump"; d.PressAt = Math.Max(earliest, t.HitAt - 0.16f);
                d.Dir = PathHasPit(me, pos, pos + new Vector2(away * 60f, 0f)) ? 0f : away;
            }
            else
            {
                d.How = "retreat"; d.PressAt = earliest; d.ReleaseAt = t.HitAt + 0.15f;
                if (PathHasPit(me, pos, pos + new Vector2(away * 50f, 0f))) { return; }
            }
            d.ResolveAt = Math.Max(d.PressAt, t.HitAt) + 0.3f + (t.Multi ? 0.4f : 0f) + (t.Guess ? 0.3f : 0f);
            CapHold(d);
            _def = d;
            if (t.Skin) Stat("skin-threat-" + d.How);
        }

        /// <summary>how long to keep guarding after the predicted impact (longer when the timing was guessed).</summary>
        private static float HoldAfter(Threat t)
        {
            float multi = t.Multi ? Math.Min(1.2f, (t.Hits - 1) * Math.Max(0.08f, t.HitGap) + 0.1f) : 0f;
            return (t.Guess ? 0.4f : 0.2f) + multi;
        }

        /// <summary>
        /// re-read the attacker's animation and move the predicted impact (and the planned press / release) when the
        /// real swing clip has started or the timing drifted. Flags AttackOver when the attack ended without hitting us.
        /// </summary>
        private void Retime(DefenseAction d, float now)
        {
            Enemy e = d.T.From;
            if (e == null || d.Parried || d.Blocked || d.Hurt) return;
            MeleeAttackStats atk = null; AttackPhase ph = AttackPhase.None;
            try { atk = e.CurrentAttack; ph = e.CurrentAttackPhase; } catch { }
            if (atk == null || !ReferenceEquals(atk, d.T.Atk) || (ph != AttackPhase.Antic && ph != AttackPhase.Delivery))
            {
                if (now > d.T.HitAt - 0.02f || atk == null || ph == AttackPhase.Recovery || ph == AttackPhase.None) d.AttackOver = true;
                return;
            }
            if (!AttackTiming(e, atk, out float eta, out float reach, out _, out _, out _)) return;
            bool guess = reach < 0f;
            if (guess && !d.T.Guess) return;                       // never replace a frame-read timing with a guess
            if (eta <= 0f && now >= d.T.HitAt - 0.03f) return;     // box live now: the hit is due, keep the plan
            float hit = now + eta;
            if (Math.Abs(hit - d.T.HitAt) < 0.02f && guess == d.T.Guess) return;
            // a wind-up that keeps getting longer (bosses chaining clips) must not keep the bot guarding forever
            if (hit > d.DecidedAt + DEF_MAX_WAIT) { if (!d.Pressed) { d.AttackOver = true; d.GaveUp = true; } return; }
            d.T.HitAt = hit; d.T.Guess = guess; d.Retimed++;
            if (!d.Pressed && d.How == "parry") d.PressAt = Math.Max(now, hit - d.Lead);
            else if (!d.Pressed) d.PressAt = Math.Min(d.PressAt, Math.Max(now, hit - 0.2f));
            d.ReleaseAt = Math.Max(d.ReleaseAt, hit + HoldAfter(d.T));
            d.ResolveAt = Math.Max(d.ResolveAt, hit + 0.3f + (d.T.Multi ? 0.4f : 0f));
            CapHold(d);
        }

        // hard limits on how long one planned guard can keep the bot from attacking
        private const float DEF_MAX_WAIT = 1.4f, DEF_MAX_TOTAL = 1.6f, DEF_MAX_HOLD = 0.9f;
        private readonly Dictionary<object, float> _guessCd = new Dictionary<object, float>();
        private static float MultiExtra(Threat t) => t.Multi ? Math.Min(0.8f, (t.Hits - 1) * Math.Max(0.08f, t.HitGap) + 0.1f) : 0f;
        private static void CapHold(DefenseAction d)
        {
            float extra = MultiExtra(d.T);
            if (d.Pressed) d.ReleaseAt = Math.Min(d.ReleaseAt, d.PressedAt + DEF_MAX_HOLD + extra);
            d.ResolveAt = Math.Min(d.ResolveAt, d.DecidedAt + DEF_MAX_TOTAL + extra);
        }

        /// <summary>log why a parry attempt did not become a parry (rate-limited) and count it by reason.</summary>
        private void ParryOutcome(DefenseAction d, float now)
        {
            if (d.How != "parry" || d.Parried) return;
            float win = ParryWindow();
            string key, why;
            float pressLead = d.PressedAt > 0f ? (d.T.HitAt - d.PressedAt) : float.NaN;
            if (d.Blocked)
            {
                key = "parry-fail-early";
                why = d.BlockTimerAtHit >= 0f ? $"too early: block was {d.BlockTimerAtHit * 1000f:0} ms old at impact (window {win * 1000f:0} ms)"
                                               : "too early: blocked instead of parried";
            }
            else if (d.HurtAt > 0f && d.OtherAttacker) { key = "parry-fail-other"; why = "hit by a different enemy during the guard"; }
            else if (d.HurtAt > 0f && !d.Pressed)
            {
                key = "parry-fail-late";
                why = $"too late: hit landed {(d.PressAt - d.HurtAt) * 1000f:0} ms before the planned press (predicted {Math.Max(0f, d.T.HitAt - d.HurtAt) * 1000f:0} ms later than it came)";
            }
            else if (d.HurtAt > 0f && d.StateAtHurt == "StateBlock") { key = "parry-fail-guard"; why = "hit through the guard (behind / not facing each other / unparryable box)"; }
            else if (d.HurtAt > 0f)
            {
                key = "parry-fail-late";
                why = $"too late: block not up yet ({(d.HurtAt - d.PressedAt) * 1000f:0} ms after the press, state {d.StateAtHurt}" + (d.OwnAttackAtPress ? ", pressed during own attack" : "") + ")";
            }
            else { key = "parry-fail-nohit"; why = d.AttackOver ? "no hit: the attack ended without touching us" : "no hit: nothing landed while guarding"; }
            Stat(key);
            if (_parryFailLogs++ < 80)
                Log.Verbose($"[parry P{Info.PlayerID + 1}] miss vs {(d.T.From != null ? d.T.From.GetType().Name : d.T.Kind)}: {why}; lead {(float.IsNaN(pressLead) ? "-" : (pressLead * 1000f).ToString("0"))} ms (aim {d.Lead * 1000f:0}, shield-up {d.ShieldUp * 1000f:0}), " +
                         $"{(d.T.Guess ? "guessed" : "frame-read")} timing, retimed {d.Retimed}x, re-armed {d.Rearms}x");
        }

        /// <summary>Dodge away from the attacker; if a hole is behind us, roll through the attacker instead; 0 = neither is safe.</summary>
        private float DodgeDirection(Player me, Vector2 pos, float away, Threat t)
        {
            float dist = 0f; try { dist = GameSettings.Singleton.DodgeSpeed * 0.35f; } catch { }
            dist = MathHelper.Clamp(dist, 50f, 140f);
            if (!PathHasPit(me, pos, pos + new Vector2(away * dist, 0f))) return away;
            if (!t.Proj && !PathHasPit(me, pos, pos + new Vector2(-away * dist, 0f))) { Stat("dodge-through"); return -away; }
            Stats.PitAvoid++;
            return 0f;
        }

        // ---------------------------------------------------------------- executing it
        /// <returns>true while the defense owns this frame's input.</returns>
        private bool RunDefense(Player me, CharacterInfo ci, Vector2 pos, bool onGround, bool hurt, float now)
        {
            // parry punish: chain the hero's parry attacks out of StateParry
            if (now - _parriedAt < 0.05f && !PlanBusy)
            {
                Release(A_DODGE);
                Plan("parry punish", null, false);
                At(0.02f, () => Tap(A_ATTACK)); At(0.16f, () => Tap(A_ATTACK)); At(0.3f, () => Tap(A_ATTACK)); At(0.44f, () => Tap(A_ATTACK));
                At(0.5f, () => { _comboCd = 0f; });
                Stat("parry-punish"); Intent = "parry punish"; Status = "fighting";
                _def = null; _parriedAt = -10f;
                return true;
            }
            if (_def == null) return false;
            DefenseAction d = _def;
            if ((d.How == "parry" || d.How == "block") && d.T.From != null && d.T.Atk != null && !d.T.Proj) Retime(d, now);
            bool done = now >= d.ResolveAt || now - d.DecidedAt > DEF_MAX_TOTAL + MultiExtra(d.T) || (d.T.From != null && (!d.T.From.Active || d.T.From.IsKO)) || (!d.Pressed && hurt)
                        || (d.AttackOver && !d.Pressed && (d.How == "parry" || d.How == "block"))
                        || (d.AttackOver && d.Pressed && now > d.PressedAt + 0.1f && (d.How == "parry" || d.How == "block") && now > d.T.HitAt + 0.1f);
            if (done)
            {
                if (d.How == "parry" && (d.Pressed || d.HurtAt > 0f)) ParryOutcome(d, now);
                if (d.Pressed && !d.Hurt && (d.How == "dodge" || d.How == "jump" || d.How == "retreat")) Stat("dodge-ok");
                if (d.Pressed && !d.Hurt && (d.Blocked || d.Dodged || d.How == "dodge")) _comboCd = 0f;   // punish the whiff / blocked swing
                if (d.How == "block" || d.How == "parry") Release(A_DODGE);
                // don't re-detect the same swing right away; a guessed guard that caught nothing cools down per enemy
                _threatSeen[d.T.Src] = Math.Max(_threatSeen.TryGetValue(d.T.Src, out float su) ? su : 0f, now + 0.35f);
                bool noHit = !d.Hurt && !d.Parried && !d.Blocked && !d.Dodged;
                if (d.T.Guess && noHit && d.T.From != null && (d.How == "parry" || d.How == "block")) { _guessCd[d.T.From] = now + 1.2f; if (_guessCd.Count > 64) _guessCd.Clear(); }
                if (d.GaveUp) Stat("def-giveup-long");
                _def = null;
                return false;
            }
            Status = "defending";
            if (!d.Pressed)
            {
                if (!onGround && d.How != "assist") return false;  // wait; can only guard on the ground
                bool ownAttack = GameRefl.StateName(me).IndexOf("Attack", StringComparison.Ordinal) >= 0;
                if (d.NeedFace)
                {
                    float toward = Math.Sign(d.T.X - pos.X);
                    bool facing = toward == 0f || (toward > 0f) == !me.HorizontalFlipped;
                    if (!facing && !ownAttack) { _move = new Vector2(toward, 0f); _urgentMove = true; Intent = "turn to guard"; _defWhy = "parry-wait"; return true; }
                }
                if (now < d.PressAt)
                {
                    // ready stance: stop walking into the swing. for a guard, also stop queueing attacks while our
                    // own swing plays out (it used to keep the combo going, so the block press got buffered behind it and came late)
                    // only in the last 0.3 s before the press (it used to cover the whole wait, so a boss that keeps
                    // winding up froze the bots); earlier than that keep fighting
                    if ((d.How == "parry" || d.How == "block") && d.PressAt - now < 0.3f) { AbortPlan(); Intent = "read " + d.T.Kind; _defWhy = "parry-wait"; return true; }
                    if (d.PressAt - now < 0.25f) { if (!ownAttack) AbortPlan(); Intent = "read " + d.T.Kind; _defWhy = "evade-wait"; return !ownAttack; }
                    return false;
                }
                AbortPlan();
                d.Pressed = true; d.PressedAt = now; d.OwnAttackAtPress = ownAttack;
                string kind = d.T.Kind;
                switch (d.How)
                {
                    case "parry":
                    case "block":
                    {
                        d.PressedAt = now; CapHold(d);
                        float hold = Math.Max(0.12f, d.ReleaseAt - now);
                        Release(A_DODGE);            // fresh press edge even if the button was still held
                        Hold(A_DODGE, hold);
                        if (d.How == "parry") { if (d.Rearms == 0) Stat("parry-try"); } else Stat("block");
                        if (ownAttack) Stat("guard-cancel");
                        Intent = (d.How == "parry" ? "parry " : "block ") + kind;
                        break;
                    }
                    case "dodge":
                        Tap(A_DODGE); _forcedMove = new Vector2(d.Dir, 0f); _forcedMoveT = 0.1f; _move = _forcedMove; _urgentMove = true;
                        Stat("dodge"); if (ownAttack) Stat("dodge-tech");
                        Intent = "dodge " + kind;
                        break;
                    case "jump":
                        JumpWhy("evade-jump"); Hold(A_JUMP, 0.18f); _forcedMove = new Vector2(d.Dir, 0f); _forcedMoveT = 0.3f; _move = _forcedMove; _urgentMove = true;
                        Stat("dodge"); Stat("evade-jump");
                        Intent = "hop over " + kind;
                        break;
                    case "assist":
                        Hold(A_SWAP, 0.1f); Tap(A_DODGE);
                        Stat("def-special"); Stat("assist"); _teamCd = Jit(6f);
                        Intent = "cosmic assist: defense vs " + kind;
                        break;
                    case "retreat":
                        _forcedMove = new Vector2(d.Dir, 0f); _forcedMoveT = Math.Max(0.1f, d.ReleaseAt - now); _move = _forcedMove; _urgentMove = true;
                        Stat("dodge"); Stat("evade-walk");
                        Intent = "back off from " + kind;
                        break;
                }
                if (d.T.Boss && Math.Abs(d.T.X - pos.X) < 90f) _bossBackoffT = Jit(0.6f);   // only near the body that swings
                _defWhy = d.How == "parry" || d.How == "block" ? "guarding" : "evading";
                return true;
            }
            // pressed: hold still while guarding (the game ignores walking in block anyway), keep evasive moves going
            if (d.How == "block" || d.How == "parry")
            {
                if (d.Parried || d.Hurt) { Release(A_DODGE); _def.ResolveAt = Math.Min(_def.ResolveAt, now + 0.05f); _defWhy = "guarding"; return d.Parried; }
                // re-timed later than planned: keep holding through the impact
                if (IsHolding(A_DODGE) && d.ReleaseAt - now > 0.02f) Hold(A_DODGE, d.ReleaseAt - now);
                // the block would be too old at impact for a parry: let go and press again once (if there is time)
                if (d.How == "parry" && !d.Blocked && d.Rearms < 1)
                {
                    float age = now - d.PressedAt, remaining = d.T.HitAt - now;
                    if (age + remaining > ParryWindow() - 0.03f && remaining > d.ShieldUp + FRAME + 0.05f)
                    {
                        Release(A_DODGE); d.Pressed = false; d.Rearms++;
                        d.PressAt = Math.Max(now + 1.5f * FRAME, d.T.HitAt - d.Lead);
                        Stat("parry-rearm"); Intent = "re-arm parry"; _defWhy = "parry-wait";
                        return true;
                    }
                }
                if (now > d.ReleaseAt) { Release(A_DODGE); _def.ResolveAt = Math.Min(_def.ResolveAt, now + 0.1f); return false; }
                _defWhy = "guarding";
                return true;
            }
            if (_forcedMoveT > 0f) { _move = _forcedMove; _urgentMove = true; _defWhy = "evading"; return true; }
            return d.How == "assist" && now - d.PressAt < 0.15f;
        }

        private static float ParryWindow()
        {
            try { return GameSettings.Singleton.BlockParryWindowDuration.Get(Paris.Game.GameInfo.Singleton.DifficultySetting); } catch { return 0.4f; }
        }
    }
}
