using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Paris.Engine;
using Paris.Engine.Combat;
using Paris.Engine.Scene;
using Paris.Game.Actor;
using Paris.Game.Camera;

namespace CosmicBots
{
    /// <summary>
    /// target hygiene.
    /// - Enemies of a LATER camera block wait in plain sight in Enemy.StateSpawnWaitingCamBlock (Enemy.InitState: a wave with
    ///   StartActive) - e.g. the yellow-armored agents standing behind the market counters. They are Active, IsActiveInWave and
    ///   not KO, but their body is disabled and they are not in the current wave, so hits do nothing until their camera block
    ///   triggers (they then run their spawn sequence, e.g. vault the counter). Same for StateSpawnWaitingHidden /
    ///   StateSpawnWaitingVisibility (CanBeHurt = false). They are not targets.
    /// - Safety net: 12 in-reach swings over >= 3 s without damage AND the game says it cannot be hit -> ignored for
    ///   5.5 s by all bots (never bosses / large targets, never something attacking or recently hurting us).
    /// - When the GO arrow is up (BeatEmUpCamera.CameraBlock == null, MainHUD.UpdateGoArrow) bots only fight threats and
    ///   otherwise follow the human.
    /// </summary>
    public partial class BotController
    {
        private static readonly Dictionary<Enemy, float> _ignoredUntil = new Dictionary<Enemy, float>();
        private static readonly List<Enemy> _ignoredPrune = new List<Enemy>();
        private static readonly HashSet<string> _skipLogged = new HashSet<string>();
        private bool _advance, _advanceWas;
        private Enemy _ndE; private int _ndHp, _ndSwings; private float _ndT;

        /// <summary>Real, hittable enemy right now? (spawn-waiting ones belong to a later camera block)</summary>
        internal static bool TargetableEnemy(Enemy e)
        {
            string st = GameRefl.StateName(e);
            bool skip = false; string why = null;
            if (st.StartsWith("StateSpawnWaiting", StringComparison.Ordinal)) { skip = true; why = st; }
            else
            {
                bool entered = true, hurtable = true;
                try { entered = e.HasEnteredCombat; hurtable = e.CanBeHurt; } catch { }
                if (st == "StateSpawning" && !entered && !hurtable) { skip = true; why = "StateSpawning (not hurtable yet)"; }
            }
            if (skip)
            {
                string key = e.GetType().Name + "/" + why;
                if (_skipLogged.Count < 64 && _skipLogged.Add(key)) Log.Verbose($"[targets] not a target: {e.GetType().Name} '{SafeName(e)}' in {why} (waiting for its camera block / spawn trigger; not in the current wave, cannot be damaged)");
                return false;
            }
            return true;
        }

        private static string SafeName(Enemy e) { try { return Menu.Sanitize(e.Name ?? ""); } catch { return ""; } }

        internal static bool IsIgnored(Enemy e) => e != null && _ignoredUntil.TryGetValue(e, out float t) && BotManager.Time < t;

        private static void PruneIgnored()
        {
            if (_ignoredUntil.Count == 0) return;
            _ignoredPrune.Clear();
            foreach (var kv in _ignoredUntil) if (BotManager.Time >= kv.Value || kv.Key == null || kv.Key.IsKO) _ignoredPrune.Add(kv.Key);
            foreach (Enemy e in _ignoredPrune) _ignoredUntil.Remove(e);
        }

        /// <summary>The game's GO arrow condition: no active camera block (area cleared, advance).</summary>
        internal static bool AdvanceShown(Scene2d scene)
        {
            try { return scene != null && scene.CurrentCamera is BeatEmUpCamera c && c.CameraBlock == null; } catch { return false; }
        }

        /// <summary>While GO is up: only enemies that are in combat and actually going for someone / close.</summary>
        private static bool IsThreat(Foe f, Vector2 pos, Player leader)
        {
            bool entered = true; Player tgt = null;
            try { entered = f.E.HasEnteredCombat; tgt = f.E.Target; } catch { }
            if (!entered) return false;
            if (tgt != null || f.Antic || f.Delivery) return true;
            if (Math.Abs(f.P.X - pos.X) < 70f && Math.Abs(f.P.Y - pos.Y) < 30f) return true;
            return leader != null && Vector2.Distance(f.P, leader.Position2D) < 110f;
        }

        private void UpdateAdvance(Scene2d scene)
        {
            _advance = AdvanceShown(scene);
            if (_advance != _advanceWas) { _advanceWas = _advance; if (_advance) Stat("go-advance"); }
            PruneIgnored();
        }

        // ---------------------------------------------------------------- no-damage safety net
        private const int ND_SWINGS = 12;           // qualifying swings needed ...
        private const float ND_MIN_TIME = 3f;       // ... spread over at least this long
        private const float ND_IGNORE = 5.5f;       // then ignore it this long (was 20 s)
        private const float ND_HURT_MEMORY = 4f;    // never ignore something that hurt us this recently
        private static readonly Dictionary<Enemy, float> _lastContact = new Dictionary<Enemy, float>();
        private Enemy _lastHurtBy; // set in DefenseEvent (kind 0), from Character.LastAttacker (projectile -> owner)
        private float _ndFirstT = -1f; private int _ndSamples, _ndVulnSamples; private bool _ndWasDelivery;

        /// <summary>Bosses and big multi-part enemies: never "no damage" ignored, never "unreachable".</summary>
        internal static bool IsLargeTarget(Enemy e)
        {
            if (e == null) return false;
            if (e is Boss) return true;
            try { return e.BodyComponent != null && e.BodyComponent.CollisionRect.Width > 80f; } catch { return false; }
        }

        /// <summary>
        /// Harmony postfix on Character.ApplyAttack. A hit from any player (or a player's projectile) on an enemy that
        /// was not "Unaffected" (hurt, blocked, parried, dodged, armored...) proves the enemy is hittable.
        /// </summary>
        internal static void OnContact(Character victim, GameObject2d attacker, Paris.Engine.Damage.AttackResult res)
        {
            try
            {
                if (res == null || ReferenceEquals(res, Paris.Engine.Damage.AttackResult.Unaffected)) return;
                // Master Mold's hands are separate (non-Enemy) characters: credit the boss
                string part = null;
                if (victim is Paris.Game.Actor.MasterMoldHand mh) { victim = mh.Owner; part = "hand"; }
                if (!(victim is Enemy e)) return;
                GameObject2d a = attacker;
                if (e is EnemyRusher && res.Result == 3 && res.DamageDone == 0 && a is Player cp)   // a light hit absorbed by a counter rusher
                    try { if (cp.PlayerInfo?.Controller is BotController cb) cb.Stats.Add("counter-absorbed"); } catch { }
                if (a is Projectile pr) { try { a = pr.Owner; } catch { } }
                if (!(a is Player ap)) return;
                if (part != null || IsLargeTarget(e))
                {
                    if (part == null) part = e is Paris.Game.Actor.MasterMold ? "head" : "body";
                    try { if (ap.PlayerInfo?.Controller is BotController bc) bc.Stats.Add("boss-part-hit:" + part); } catch { }
                }
                _lastContact[e] = BotManager.Time;
                if (_lastContact.Count > 256)
                {
                    _ignoredPrune.Clear();
                    foreach (var kv in _lastContact) if (BotManager.Time - kv.Value > 10f || kv.Key == null || kv.Key.IsKO) _ignoredPrune.Add(kv.Key);
                    foreach (Enemy x in _ignoredPrune) _lastContact.Remove(x);
                }
            }
            catch { }
        }

        private void NdReset(int hp) { _ndHp = hp; _ndT = 0f; _ndSwings = 0; _ndFirstT = -1f; _ndSamples = 0; _ndVulnSamples = 0; }

        /// <summary>
        /// Safety net rewrite): only swings that should connect count (our attack's Delivery starts while the target is
        /// in reach, in our lane, in front of us, grounded and not blocking / armored / hurt / dodging). After 12 such swings over
        /// at least 3 s without its HP dropping, and only if the game itself says it cannot be hit right now (CanBeHurt false,
        /// invulnerable, no hurt boxes, or not a valid target), it is ignored for 5.5 s by all bots. Never for bosses / big
        /// targets, never while it is attacking us or shortly after it hurt us. If the game says it IS hittable, we just
        /// change sides instead (our swings are whiffing).
        /// </summary>
        private void NoDamageWatch(Player me, Foe t, Vector2 pos, float dt)
        {
            if (t == null) { _ndE = null; return; }
            int hp = 0; try { hp = t.E.HP; } catch { }
            float now = BotManager.Time;
            if (t.E != _ndE) { _ndE = t.E; NdReset(hp); _ndWasDelivery = false; return; }
            if (t.Boss || IsLargeTarget(t.E)) { NdReset(hp); return; }
            Player aimed = null; try { aimed = t.E.Target; } catch { }
            bool attackingMe = ReferenceEquals(aimed, me) && (t.Antic || t.Delivery || (t.State ?? "").IndexOf("Attack", StringComparison.Ordinal) >= 0);
            bool hurtMe = ReferenceEquals(_lastHurtBy, t.E) && now - _lastHurtAt < ND_HURT_MEMORY;
            bool contact = _lastContact.TryGetValue(t.E, out float ct) && now - ct < 1.0f;
            if (hp < _ndHp || attackingMe || hurtMe || contact) { NdReset(Math.Min(_ndHp, hp)); return; }

            AttackPhase ph = AttackPhase.None; try { ph = me.CurrentAttackPhase; } catch { }
            bool delivery = ph == AttackPhase.Delivery;
            bool rising = delivery && !_ndWasDelivery;
            _ndWasDelivery = delivery;

            string st = t.State ?? "";
            bool busy = t.Blocking || t.Armored || t.Air ||
                        st.IndexOf("Hurt", StringComparison.Ordinal) >= 0 || st.IndexOf("Dodge", StringComparison.Ordinal) >= 0 ||
                        st.IndexOf("Knock", StringComparison.Ordinal) >= 0 || st.IndexOf("Block", StringComparison.Ordinal) >= 0;
            float dx = t.P.X - pos.X;
            bool facing = (dx >= 0f) == !me.HorizontalFlipped || Math.Abs(dx) < 6f;
            bool inReach = Math.Abs(dx) <= Math.Max(ATTACK_RANGE_X, t.Reach) + 10f && Math.Abs(t.P.Y - pos.Y) <= 14f;
            if (!(inReach && facing && !busy)) return;    // not a swing that should connect: neither counts nor resets

            // sample the game's own "can it be hit" state while we are lined up
            _ndSamples++;
            try { if (t.E.GetVulnerabilityBoxCount() > 0) _ndVulnSamples++; } catch { _ndVulnSamples++; }
            if (!rising) return;
            _ndSwings++;
            if (_ndFirstT < 0f) _ndFirstT = now;
            _ndT = now - _ndFirstT;
            if (_ndSwings < ND_SWINGS || _ndT < ND_MIN_TIME) return;

            bool canBeHurt = true, invuln = false;
            try { canBeHurt = t.E.CanBeHurt; invuln = t.E.IsInvulnerable; } catch { }
            float vulnFrac = _ndSamples > 0 ? (float)_ndVulnSamples / _ndSamples : 1f;
            bool gameSaysNo = !canBeHurt || invuln || vulnFrac < 0.2f || !TargetableEnemy(t.E);
            if (gameSaysNo)
            {
                _ignoredUntil[t.E] = now + ND_IGNORE;
                Stat("ignored-no-damage");
                Log.Verbose($"[bot P{Info.PlayerID + 1}] ignoring {t.Label} '{SafeName(t.E)}' for {ND_IGNORE:0.0} s: {_ndSwings} in-reach swings / {_ndT:0.0} s without damage (HP {hp}, state {t.State}; " +
                         $"CanBeHurt={canBeHurt} Invulnerable={invuln} hurtbox-frames={vulnFrac * 100f:0}%)");
                _ndE = null;
                AbortPlan();
            }
            else
            {
                Stat("whiff-reposition");
                Log.Verbose($"[bot P{Info.PlayerID + 1}] {_ndSwings} swings at {t.Label} '{SafeName(t.E)}' without damage but the game says it is hittable (hurtbox-frames {vulnFrac * 100f:0}%, state {t.State}); switching sides, not ignoring");
                _side = -_side;
                NdReset(hp);
                AbortPlan();
            }
        }
    }
}
