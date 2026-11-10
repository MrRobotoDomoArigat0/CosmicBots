using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Paris.Engine.Combat;
using Paris.Game.Actor;
using Paris.Game.Attacks;
using Paris.Game.Data;

namespace CosmicBots
{
    /// <summary>
    /// get out of Annihilus' Negative Rush lane.
    ///   Observed game behavior:  Annihilus.StateNegativeRush (attack "AnnihilusNegativeRush"): Antic (wind-up anim) -> Delivery. In Delivery he
    ///               picks a random point on the far edge of the arena (the edge on the side he faces, i.e. toward the arena centre),
    ///               waits NegativeRushChangeDirectionDelay (0.1 s), then dashes along that unit direction (private
    ///               _negativeRushDirection, X and depth Y) at attack.HorizontalDisplacement.Speed until he hits the playfield /
    ///               camera edge; then faces the centre again and repeats (NegativeRushDirectionCount = 5 legs); then Recovery.
    ///               His body is the attack box (hits seen about 90 px either side of him, +-20 in depth).
    ///   Lane:       a strip around his position along the dash direction: half-width = |dir.Y| * 88 + |dir.X| * (attack depth + 6)
    ///               (+ the bot's own half size); starts 88 px behind him and runs through the dash. Before the direction exists
    ///               (wind-up, between legs) the lane is the horizontal band in front of him (+12 px for the unknown angle).
    ///   Escape:     a bot inside the lane treats it as an instant threat (no reaction roll, no reaction delay), cancels its plan and
    ///               any dodge / guard it had started, and walks perpendicular to the lane toward the nearer clear side (camera
    ///               bounds, walkable ground, other bots' escape spots are scored). No dodging / rolling along the path. Dodge-type
    ///               heroes that cannot clear the lane before the charge arrives dodge through it as a fallback (i-frames).
    ///   Attack lock: the bot does not start attacks from the first escape until the rush ends + 0.3 s.
    /// All of it is wrapped in try/catch and falls back to the old behaviour.
    /// </summary>
    public partial class BotController
    {
        private const float RUSH_W = 88f;                 // half length of Annihilus' body attack box (px)
        private static FieldInfo _rushDirF, _rushChoseF;
        private static bool _rushRefl;

        // ---------------------------------------------------------------- per-bot state
        private bool _rushEsc, _rushHandled, _rushSticky, _rushLogged, _rushFallback;
        private float _rushLastActive = -100f, _rushEscUntil = -100f, _rushEpisodeEnd = -100f, _rushDodgeCd = -100f;
        private float _rushSide; private Vector2 _rushSideDir, _rushMove, _rushSpot;
        private static int _rushLines;

        // ---------------------------------------------------------------- perception (called from See, every frame per enemy)
        private static void RushRead(Foe f)
        {
            f.Rush = false; f.RushKnown = false; f.RushDir = default(Vector2); f.RushSpeed = f.RushDepth = 0f;
            try
            {
                Enemy e = f.E;
                MeleeAttackStats atk = e.CurrentAttack;
                string an = null; try { an = atk != null ? atk.Name : null; } catch { }
                bool named = (f.State != null && f.State.IndexOf("NegativeRush", StringComparison.Ordinal) >= 0) ||
                             (an != null && an.IndexOf("NegativeRush", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!named) return;
                AttackPhase ph = e.CurrentAttackPhase;
                if (ph != AttackPhase.Antic && ph != AttackPhase.Delivery) return;
                f.Rush = true;
                float depth = 24f; try { depth = Math.Max(16f, Math.Min(48f, atk.Depth.Max)); } catch { }
                f.RushDepth = depth;
                float speed = 0f; try { speed = atk.HorizontalDisplacement.Speed; } catch { }
                // dash direction: the private field once he has chosen it for this leg, else his actual velocity
                if (ph == AttackPhase.Delivery)
                {
                    try
                    {
                        if (!_rushRefl)
                        {
                            _rushRefl = true;
                            _rushDirF = AccessTools.Field(typeof(Annihilus), "_negativeRushDirection");
                            _rushChoseF = AccessTools.Field(typeof(Annihilus), "_negativeRushDidChoseDirection");
                        }
                        if (e is Annihilus && _rushDirF != null && _rushChoseF != null && (bool)_rushChoseF.GetValue(e))
                        {
                            Vector2 d = (Vector2)_rushDirF.GetValue(e);
                            if (d.LengthSquared() > 0.25f) { d.Normalize(); f.RushDir = d; f.RushKnown = true; }
                        }
                    }
                    catch { }
                    try
                    {
                        Vector3 v = e.BodyComponent.Displacement.Speed;
                        Vector2 v2 = new Vector2(v.X, v.Y);
                        float l = v2.Length();
                        if (l > 40f) { speed = Math.Max(speed, l); if (!f.RushKnown) { v2 /= l; f.RushDir = v2; f.RushKnown = true; } }
                    }
                    catch { }
                }
                f.RushSpeed = speed > 60f ? speed : 420f;
            }
            catch { f.Rush = false; }
        }

        // ---------------------------------------------------------------- per frame
        /// <summary>Returns true while this bot must not start attacks (escaping / until the rush ends + 0.3 s).</summary>
        private bool RushUpdate(Player me, CharacterInfo ci, List<Foe> foes, Vector2 pos, bool onGround, bool hurt, float now, float dt)
        {
            bool wasEsc = _rushEsc;
            _rushEsc = false; _rushHandled = false; _rushFallback = false;
            try
            {
                bool any = false;
                foreach (Foe f in foes) if (f.Rush) { any = true; break; }
                if (!any)
                {
                    if (_rushSticky && now > _rushLastActive + 0.3f) { _rushSticky = false; _rushSide = 0f; }
                    return _rushSticky;
                }
                if (now - _rushLastActive > 0.8f) { _rushLogged = false; _rushSticky = false; _rushSide = 0f; }   // a new rush
                _rushLastActive = now;
                if (hurt || me.IsInSpecialAttack || _specialHolding) return _rushSticky;
                _rushHandled = true;

                float hw = HalfWidth(me);
                Foe pick = null; float pickEta = float.MaxValue, pickP = 0f, pickHw = 0f; Vector2 pickD = default(Vector2); bool pickKnown = false;
                foreach (Foe f in foes)
                {
                    if (!f.Rush) continue;
                    bool known = f.RushKnown;
                    Vector2 d = known ? f.RushDir : new Vector2(f.FacingRight ? 1f : -1f, 0f);
                    float D = Math.Max(26f, f.RushDepth + 6f);
                    float bodyW = hw * Math.Abs(d.Y) + 4f * Math.Abs(d.X);
                    float Hw = Math.Abs(d.Y) * RUSH_W + Math.Abs(d.X) * D + bodyW + (known ? 0f : 12f);
                    Vector2 v = pos - f.BodyP;
                    float t = v.X * d.X + v.Y * d.Y;                   // along the dash
                    float p = -v.X * d.Y + v.Y * d.X;                  // across it
                    if (t < -(RUSH_W + 4f) || (!known && t > 320f)) continue;
                    if (Math.Abs(p) >= Hw + (wasEsc ? 8f : 0f)) continue;
                    float eta = known ? Math.Max(0f, t - RUSH_W - hw) / Math.Max(60f, f.RushSpeed) : 0.6f;
                    if (eta < pickEta) { pickEta = eta; pick = f; pickP = p; pickHw = Hw; pickD = d; pickKnown = known; }
                }
                if (pick == null) return _rushSticky;

                // ---- inside the lane: choose the side
                Vector2 n = new Vector2(-pickD.Y, pickD.X);
                float floor = 0f; try { floor = me.BodyComponent.FloorHeightIgnoringPits; } catch { }
                if (_rushSide != 0f && Vector2.Dot(_rushSideDir, pickD) < 0.97f) _rushSide = 0f;   // dash direction changed: choose again
                float near = pickP >= 0f ? 1f : -1f;
                float bestCost = float.MaxValue, bestSide = near; Vector2 bestSpot = pos; bool bestOk = false;
                for (int k = 0; k < 2; k++)
                {
                    float side = k == 0 ? near : -near;
                    float need = Math.Max(4f, pickHw + 10f - side * pickP);
                    Vector2 spot = pos + n * side * need;
                    Vector2 sc = ClampToCamera(me, spot, FloorAt(me, spot, floor));
                    float clear = side * (((sc - pick.BodyP).X * n.X) + ((sc - pick.BodyP).Y * n.Y));
                    bool ok = clear >= pickHw + 3f;
                    float cost = need + (ok ? 0f : 1000f);
                    try { if (!SpotWalkable(me, sc, floor)) cost += 400f; } catch { }
                    try { if (PathHasPit(me, pos, sc)) cost += 300f; } catch { }
                    foreach (BotController b in Live) if (b != this && b._rushEsc && Vector2.DistanceSquared(b._rushSpot, sc) < 26f * 26f) cost += 40f;
                    if (_rushSide == side) cost -= 60f;
                    if (cost < bestCost) { bestCost = cost; bestSide = side; bestSpot = sc; bestOk = ok; }
                }
                _rushSide = bestSide; _rushSideDir = pickD; _rushSpot = bestSpot;
                Vector2 u = bestSpot - pos;
                if (u.LengthSquared() < 1f) u = n * bestSide; else u.Normalize();
                _rushMove = u;
                _rushEsc = true; _rushSticky = true; _rushEscUntil = now + 0.3f;
                Suppress("rush-escape");
                if (PlanBusy) AbortPlan();
                // a dodge / jump already in motion runs out; a guard that has not resolved is dropped (it would stand in the lane)
                if (_def != null && (!_def.Pressed || _def.How == "block" || _def.How == "parry")) { if (_def.How == "block" || _def.How == "parry") Release(A_DODGE); _def = null; }

                // ---- fallback: cannot clear the lane before the charge arrives -> dodge through it (i-frames)
                float walk = 90f; try { walk = Math.Max(40f, me.CurrentWalkSpeed); } catch { }
                float clearT = Math.Max(0f, pickHw + 6f - Math.Abs(pickP)) / walk;
                if (pickKnown && pickEta <= 0.12f && clearT > pickEta && now >= _rushDodgeCd && onGround && !me.IsFlying && _def == null && ci.Defense == Player.DefenseType.Dodge)
                {
                    bool cap = false; try { cap = me.CanDodgeBlockParry(); } catch { }
                    if (cap)
                    {
                        float dir = pickD.X >= 0f ? -1f : 1f;                 // through him, not along his path
                        if (PathHasPit(me, pos, pos + new Vector2(dir * 70f, 0f))) dir = -dir;
                        if (!PathHasPit(me, pos, pos + new Vector2(dir * 70f, 0f)))
                        {
                            Tap(A_DODGE); _forcedMove = new Vector2(dir, 0f); _forcedMoveT = 0.1f; _rushMove = _forcedMove; _rushDodgeCd = now + 0.7f;
                            _rushFallback = true; Stat("rush-dodge");
                        }
                    }
                }

                // ---- bookkeeping: one episode = one continuous escape
                if (!wasEsc && now > _rushEpisodeEnd + 0.25f)
                {
                    Stat("rush-escape");
                    if (!_rushLogged && _rushLines++ < 150)
                    {
                        _rushLogged = true;
                        string way = Math.Abs(u.Y) >= Math.Abs(u.X) ? (u.Y < 0f ? "up" : "down") : (u.X < 0f ? "left" : "right");
                        Log.Verbose($"[rush P{PTag}] {Stats.Hero} Negative Rush: stepping {way} out of the lane ({(pickKnown ? "dash dir " + pickD.X.ToString("0.00") + "," + pickD.Y.ToString("0.00") : "predicted horizontal lane, wind-up")}, " +
                                 $"lane half-width {pickHw:0}, {Math.Abs(pickP):0} px off the centre line, charge eta {(pickKnown ? pickEta.ToString("0.00") + " s" : "?")}, needs {Math.Max(0f, pickHw + 6f - Math.Abs(pickP)):0} px, room {(bestOk ? "ok" : "tight")}{(_rushFallback ? ", fallback dodge" : "")})");
                    }
                }
                else if (_rushFallback && _rushLines++ < 150)
                    Log.Verbose($"[rush P{PTag}] {Stats.Hero} could not clear the lane in time (eta {pickEta:0.00} s, needs {clearT:0.00} s): fallback dodge through it");
                _rushEpisodeEnd = now;
                return true;
            }
            catch (Exception ex) { _rushEsc = false; _rushHandled = false; Mod.LogOnce("rushupdate", ex); }
            return _rushSticky;
        }

        /// <summary>Output of the escape this frame (called from Think instead of the normal movement / fighting).</summary>
        private void RushApply()
        {
            _move = _rushMove; _urgentMove = true;
            Status = "defending"; Intent = "rush: out of Annihilus' lane";
            Release(A_ATTACK);
        }

        /// <summary>Hit logger hook: counts Negative Rush hits (and those taken while escaping).</summary>
        private void RushHitNote(string atkName, float now)
        {
            if (atkName == null || atkName.IndexOf("NegativeRush", StringComparison.OrdinalIgnoreCase) < 0) return;
            Stat("rush-hit");
            if (_rushEsc || now < _rushEscUntil) Stat("rush-hit-escaping");
        }
    }
}
