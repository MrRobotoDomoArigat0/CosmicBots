using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Paris.Engine.Graphics.Animations;
using Paris.Engine.Combat;
using Paris.Game.Actor;
using Paris.Game.Attacks;
using Paris.Game.Data;
using Paris.Game.System.Attacks;

namespace CosmicBots
{
    /// <summary>
    /// v0.2.12: spiky-skin awareness and the heavy-damage disengage.
    ///   Skin state:  an enemy "has its damaging skin up" while its CurrentAttack is a skin attack (attack name contains "Skin"/"Spike",
    ///                e.g. SwarmerSpikerDamagingSkinAttack) and the attack phase is Antic or Delivery (plus 0.35 s of grace).
    ///   Reach:       the widest attack box of the attack's current animation, raised to the farthest hit actually taken from that
    ///                attack (learned at runtime), clamped to 40..200.
    ///   Behaviour:   bots do not melee a skin-active enemy and keep their body outside its reach (forced step-out with hysteresis);
    ///                heroes with a projectile power attack and ammo shoot it from just outside the reach; defense treats a bot standing
    ///                inside the reach as an immediate threat (no reaction roll, no reaction delay).
    ///   Counter:     EnemyRusher.ApplyAttack absorbs every light hit from a player (no damage) and answers with its CounterAttack;
    ///                heavy swings, specials and projectiles bypass that. Heroes with a heavy swing open on such enemies with it.
    ///   Burst:       5 hits within 3 s -> about 1.8 s without attacking, evading / walking toward the human.
    /// All of it is wrapped in try/catch and falls back to "no skin / no escape".
    /// </summary>
    public partial class BotController
    {
        private sealed class SkinMemo { public float Until, Reach, Depth; }
        private static readonly Dictionary<Enemy, SkinMemo> _skinMemo = new Dictionary<Enemy, SkinMemo>();
        private static readonly Dictionary<string, float> _skinObs = new Dictionary<string, float>();
        private sealed class Flag { public bool V; public string Name; }
        private static readonly ConditionalWeakTable<MeleeAttackStats, Flag> _skinAtk = new ConditionalWeakTable<MeleeAttackStats, Flag>();
        private static readonly ConditionalWeakTable<Enemy, Flag> _counterMemo = new ConditionalWeakTable<Enemy, Flag>();
        private static readonly HashSet<string> _probed = new HashSet<string>();
        private static readonly ConditionalWeakTable<CharacterInfo, Flag> _rangedHero = new ConditionalWeakTable<CharacterInfo, Flag>();

        private static bool SkinName(string n)
            => !string.IsNullOrEmpty(n) && (n.IndexOf("DamagingSkin", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("BodyCounter", StringComparison.OrdinalIgnoreCase) >= 0);   // v0.2.14: + red queen QueenExterminaMeleeBodyCounterAttack (spike counter when hurt)
            //   // v0.2.13: was any "Skin"/"Spike" (flagged BugTrooperSpikedShield swings)

        private static int _classLines;
        private static void LogClass(string n, bool skin)
        {
            try
            {
                if (_classLines >= 40 || string.IsNullOrEmpty(n)) return;
                if (!skin && n.IndexOf("Skin", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("Spike", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("Counter", StringComparison.OrdinalIgnoreCase) < 0) return;
                _classLines++;
                Log.Verbose("[skin] attack " + Menu.Sanitize(n) + " classified as " + (skin ? "SKIN (damaging skin: avoid / no melee)" : "normal melee (regular defense + targeting)"));
            }
            catch { }
        }

        private static bool IsSkinAttack(MeleeAttackStats atk)
        {
            if (atk == null) return false;
            if (!_skinAtk.TryGetValue(atk, out Flag f)) { string n = null; try { n = atk.Name; } catch { } f = new Flag { V = SkinName(n), Name = n }; try { _skinAtk.Add(atk, f); } catch { } LogClass(n, f.V); }
            return f.V;
        }

        /// <summary>Fills Foe.Skin / SkinReach / SkinDepth / Counter (called from See, every frame per enemy).</summary>
        private static void SkinRead(Foe f, float now)
        {
            f.Skin = false; f.Counter = false; f.SkinReach = f.SkinDepth = 0f;
            try
            {
                Enemy e = f.E;
                MeleeAttackStats atk = e.CurrentAttack;
                if (e is EnemyRusher) f.Counter = CounterOf((EnemyRusher)e, out _);
                bool skinAtk = atk != null && (IsSkinAttack(atk) || (f.Counter && SameName(atk, e)));
                if (skinAtk)
                {
                    AttackPhase ph = e.CurrentAttackPhase;
                    if (!_skinMemo.TryGetValue(e, out SkinMemo m)) { m = new SkinMemo(); if (_skinMemo.Count > 64) PruneSkin(now); _skinMemo[e] = m; }
                    if (ph == AttackPhase.Antic || ph == AttackPhase.Delivery)
                    {
                        m.Until = now + 0.35f;
                        m.Reach = SkinReachOf(e, atk, f.Reach);
                        float depth = 24f; try { depth = Math.Max(16f, Math.Min(56f, atk.Depth.Max)); } catch { }
                        m.Depth = depth;
                    }
                    if (now < m.Until && m.Reach > 0f) { f.Skin = true; f.SkinReach = m.Reach; f.SkinDepth = m.Depth; }
                }
                else if (_skinMemo.Count > 0 && _skinMemo.TryGetValue(e, out SkinMemo m2) && now < m2.Until && m2.Reach > 0f)
                { f.Skin = true; f.SkinReach = m2.Reach; f.SkinDepth = m2.Depth; }   // gap between two skin attacks
            }
            catch { }
        }

        /// <summary>The rusher's configured counter attack (EnemyRusher.CounterAttack) when it is a damaging-skin counter (cached per enemy).</summary>
        private static bool CounterOf(EnemyRusher er, out string name)
        {
            if (!_counterMemo.TryGetValue(er, out Flag c))
            {
                string cn = null; bool has = false;
                try { var ca = er.CounterAttack; if (ca != null && !ca.IsEmpty) { var a = ca.GetAttack(); cn = a?.Name; has = a != null; } } catch { }
                string lab = LabelOf(er);
                c = new Flag { V = has && (SkinName(cn) || lab.IndexOf("Spiker", StringComparison.OrdinalIgnoreCase) >= 0), Name = cn };
                try { _counterMemo.Add(er, c); } catch { }
                ProbeRusher(er, lab, cn);
                if (c.V) LogClass(cn, true);
            }
            name = c.Name; return c.V;
        }

        private static bool SameName(MeleeAttackStats atk, Enemy e)
        {
            try { return e is EnemyRusher er && CounterOf(er, out string cn) && cn != null && cn == atk.Name; } catch { return false; }
        }

        private static void PruneSkin(float now)
        {
            var dead = new List<Enemy>();
            foreach (var kv in _skinMemo) if (kv.Key == null || !kv.Key.Active || kv.Key.IsKO || now > kv.Value.Until + 5f) dead.Add(kv.Key);
            foreach (Enemy e in dead) _skinMemo.Remove(e);
            if (_skinMemo.Count > 64) _skinMemo.Clear();
        }

        private static float SkinReachOf(Enemy e, MeleeAttackStats atk, float fallback)
        {
            float r = 0f;
            try
            {
                Animator2d a = e.GetAttackAnimator(atk) ?? e.Animator;
                if (a != null && a.AnimationData != null)
                {
                    int id = a.CurrentAnimationID;
                    if (id >= 0 && id < a.AnimationData.Animations.Count)
                    {
                        float scale = a.Scale > 0.05f ? a.Scale : 1f;
                        foreach (var fr in a.AnimationData.Animations[id].Frames)
                        {
                            if (fr.AttackBoxes == null) continue;
                            foreach (var hb in fr.AttackBoxes)
                            {
                                if (hb == null) continue;
                                Rectangle bx = hb.Box;
                                r = Math.Max(r, Math.Max(Math.Abs(bx.Left), Math.Abs(bx.Right)) * scale);
                            }
                        }
                    }
                }
            }
            catch { }
            try { string n = atk.Name; if (n != null && _skinObs.TryGetValue(n, out float o)) r = Math.Max(r, o); } catch { }
            r = Math.Max(r, Math.Min(120f, fallback * 0.8f));
            return MathHelper.Clamp(r, 40f, 200f);
        }

        /// <summary>A hit taken from a skin attack at this horizontal distance: the skin reaches at least that far.</summary>
        private static void SkinObserve(string attackName, float dx)
        {
            try
            {
                if (!SkinName(attackName)) return;
                float v = MathHelper.Clamp(Math.Abs(dx) - 4f, 0f, 200f);
                _skinObs.TryGetValue(attackName, out float old);
                if (v > old) _skinObs[attackName] = v;
            }
            catch { }
        }

        /// <summary>Once per rusher type: which attack is the counter / close-range / rush / explode attack (the data decides what a skin enemy does).</summary>
        private static void ProbeRusher(EnemyRusher er, string label, string counterName)
        {
            try
            {
                if (_probed.Count > 40 || !_probed.Add(label)) return;
                string N(AttackID id) { try { if (id == null || id.IsEmpty) return "-"; return id.GetAttack()?.Name ?? "?"; } catch { return "?"; } }
                bool imm = false; try { imm = er.ImmuneToHitReaction; } catch { }
                Log.Verbose($"[probe] rusher {label}: counter={counterName ?? "-"} close={N(er.CloseRangeAttack)} rush={N(er.RushAttack)} explode={N(er.ExplodeAttack)} immune-to-hit-reaction={imm}");
            }
            catch { }
        }

        /// <summary>True when the hero's power attack fires a projectile (Iron Man, Storm, Rocket, ...).</summary>
        private static bool HeroRanged(CharacterInfo ci)
        {
            if (ci == null) return false;
            if (_rangedHero.TryGetValue(ci, out Flag f)) return f.V;
            bool v = false;
            try
            {
                foreach (CharacterAttackInfo ai in ci.PowerAttack)
                {
                    var a = ai?.AttackID?.GetAttack() as MeleeAttackStats;
                    if (a != null && a.ProjectileAttackSteps != null && a.ProjectileAttackSteps.Count > 0) { v = true; break; }
                }
            }
            catch { }
            try { _rangedHero.Add(ci, new Flag { V = v }); } catch { }
            return v;
        }

        // ---------------------------------------------------------------- per-bot state
        private bool _skinEsc, _canShootSkin;
        private float _skinEscT, _skinEscCool = -100f, _escapeUntil = -100f, _escapeStart = -100f;
        private bool _escapeNeedDodge;
        private int _escapeLines;
        private static int _skinLines;

        /// <summary>
        /// Called once per frame after the enemies are read. Returns true while the bot must not start attacks (stepping out of a
        /// skin enemy's reach or in a burst escape). Aborts a running combo plan in that case.
        /// </summary>
        private bool SkinUpdate(Player me, CharacterInfo ci, List<Foe> foes, Vector2 pos, Player leader, Vector2 leaderGround, bool onGround, bool hurt, float now, float dt)
        {
            bool block = false;
            try
            {
                _canShootSkin = false;
                try { _canShootSkin = HeroRanged(ci) && me.Ammo > 0; } catch { }

                // ---- stepping out of a skin enemy's reach
                float hw = HalfWidth(me);
                bool inside = false; float push = 0f; Foe nearest = null; float nd = float.MaxValue;
                foreach (Foe f in foes)
                {
                    if (!f.Skin || f.Boss) continue;
                    if (f.BodyH > 90f) continue;
                    float dx = pos.X - f.BodyP.X, ady = Math.Abs(pos.Y - f.BodyP.Y);
                    if (ady > f.SkinDepth + 8f) continue;
                    float enter = f.SkinReach + hw + 4f, lim = _skinEsc ? enter + 20f : enter;
                    float ax = Math.Abs(dx);
                    if (ax < lim) { inside = true; push += (dx >= 0f ? 1f : -1f) * (lim - ax + 1f); if (ax < nd) { nd = ax; nearest = f; } }
                }
                if (inside)
                {
                    if (!_skinEsc) { if (now < _skinEscCool) inside = false; else { _skinEsc = true; _skinEscT = 0f; Stat("skin-avoid"); if (_skinLines++ < 120) Log.Verbose($"[skin P{PTag}] {Stats.Hero} stepping out of {(nearest != null ? nearest.Label : "?")} skin reach {(nearest != null ? nearest.SkinReach : 0f):0} (dx {nd:0})"); } }
                    else { _skinEscT += dt; if (_skinEscT > 2.5f) { _skinEsc = false; _skinEscCool = now + 1.5f; inside = false; Stat("skin-avoid-giveup"); } }
                }
                if (!inside) { _skinEsc = false; _skinEscT = 0f; }
                if (_skinEsc)
                {
                    block = true;
                    if (PlanBusy) AbortPlan();
                    Suppress("skin-avoid");
                    if (_def == null && !hurt)
                    {
                        float dir = push >= 0f ? 1f : -1f;
                        if (Math.Abs(push) < 0.5f) dir = leader != null && leaderGround.X != pos.X ? Math.Sign(leaderGround.X - pos.X) : (me.HorizontalFlipped ? 1f : -1f);
                        _forcedMove = new Vector2(dir, 0f); _forcedMoveT = Math.Max(_forcedMoveT, 0.12f);
                    }
                }

                // ---- burst escape
                if (now < _escapeUntil)
                {
                    block = true;
                    if (PlanBusy) AbortPlan();
                    Suppress("burst-escape");
                    float dir = 0f;
                    if (leader != null && Math.Abs(leaderGround.X - pos.X) > 28f) dir = Math.Sign(leaderGround.X - pos.X);
                    else { Foe nf = null; float bd = 160f; foreach (Foe f in foes) { float d = Math.Abs(f.BodyP.X - pos.X); if (d < bd) { bd = d; nf = f; } } if (nf != null) dir = pos.X >= nf.BodyP.X ? 1f : -1f; }
                    // never run through a skin enemy to get to the human
                    if (dir != 0f)
                        foreach (Foe f in foes)
                            if (f.Skin && Math.Abs(pos.Y - f.BodyP.Y) < f.SkinDepth + 8f && (f.BodyP.X - pos.X) * dir > 0f && Math.Abs(f.BodyP.X - pos.X) < 200f) { dir = -dir; break; }
                    if (!hurt && _def == null)
                    {
                        bool dodged = false;
                        if (_escapeNeedDodge && onGround && !me.IsFlying && dir != 0f && ci.Defense == Paris.Game.Actor.Player.DefenseType.Dodge)
                        {
                            bool cap = false; try { cap = me.CanDodgeBlockParry(); } catch { }
                            if (cap && !PathHasPit(me, pos, pos + new Vector2(dir * 70f, 0f))) { Tap(A_DODGE); _forcedMove = new Vector2(dir, 0f); _forcedMoveT = 0.1f; _escapeNeedDodge = false; dodged = true; }
                        }
                        if (!dodged && dir != 0f) { _forcedMove = new Vector2(dir, leader != null && Math.Abs(leaderGround.Y - pos.Y) > 10f ? Math.Sign(leaderGround.Y - pos.Y) * 0.4f : 0f); _forcedMoveT = Math.Max(_forcedMoveT, 0.12f); }
                    }
                }
            }
            catch (Exception ex) { Mod.LogOnce("skinupdate", ex); }
            return block;
        }

        /// <summary>Called from the hit logger for every hit this bot takes: 5 hits within 3 s start a short disengage.</summary>
        private void TryBurstEscape(Player p, float now)
        {
            try
            {
                if (now < _escapeUntil + 0.6f) return;
                int n = 0; int count = Math.Min(_hitN, 16);
                for (int i = 0; i < count; i++) if (now - _hitT[i] <= BURST_WINDOW && _hitT[i] > _escapeUntil) n++;
                if (n < BURST_HITS) return;
                _escapeStart = now; _escapeUntil = now + 1.8f; _escapeNeedDodge = true;
                Stat("burst-escape");
                if (_escapeLines++ < 120)
                    Log.Verbose($"[burst P{PTag}] escape: {Stats.Hero} took {n} hits in {BURST_WINDOW:0} s -> attack-suppressed: burst-escape for 1.8 s, evading toward the human (hp {p.HPRatio:0.00}, state {_lastState})");
            }
            catch { }
        }

        // ---------------------------------------------------------------- engage helpers
        /// <summary>Skin-active target: shoot it from just outside its reach (heroes with a projectile power attack), never melee it.</summary>
        private Vector2 EngageSkinRanged(Player me, CharacterInfo ci, Foe t, Vector2 pos, bool onGround, bool flying)
        {
            Status = "engaging";
            float hw = HalfWidth(me);
            float stand = t.SkinReach + hw + 34f;
            float side = pos.X >= t.BodyP.X ? 1f : -1f;
            Vector2 goal = new Vector2(t.BodyP.X + side * stand, t.BodyP.Y);
            float dx = t.BodyP.X - pos.X, dy = t.BodyP.Y - pos.Y;
            bool aligned = Math.Abs(dy) <= LANE_TOLERANCE + 2f;
            bool facing = (dx > 0f) == !me.HorizontalFlipped || Math.Abs(dx) <= 6f;
            bool band = Math.Abs(dx) >= t.SkinReach + hw + 10f && Math.Abs(dx) <= stand + 70f;
            if (aligned && band)
            {
                if (!facing) { _move = new Vector2(Math.Sign(dx), 0f); _urgentMove = true; Intent = "turn to spiky"; return pos; }
                if (_powerCd <= 0f && me.Ammo > 0 && !PlanBusy)
                {
                    Tap(A_POWER); _powerCd = Jit(1.3f); Stat("ranged-on-spiky"); Intent = "ranged on " + t.Label;
                    return pos;
                }
                Intent = "hold range (spiky skin)";
                return pos;
            }
            Intent = "spacing vs spiky skin";
            return goal;
        }

        /// <summary>Heavy swings, specials and projectiles are not absorbed by a counter rusher: open with the heavy swing.</summary>
        private bool TryCounterHeavy(CharacterInfo ci, Foe t)
        {
            try
            {
                if (!t.Counter || t.Skin || PlanBusy) return false;
                if (ci.HeavySwingAttack == null || ci.HeavySwingAttack.GetAttack() == null) return false;
                float hold = ci.HeavyAttackStartChargingHoldDuration + 0.15f;
                Plan("heavy opener", t.E, false);
                At(0f, () => { Hold(A_ATTACK, hold); Intent = "heavy opener (counter enemy)"; });
                At(hold + 0.6f, () => { _comboCd = Jit(0.25f / Aggr, 0.5f); });
                Stat("counter-heavy-open");
                return true;
            }
            catch { return false; }
        }
    }
}
