using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Paris.Engine;
using Paris.Engine.Graphics.Animations;
using Paris.Engine.Physic;
using Paris.Game.Actor;
using Paris.Game.Data;

namespace CosmicBots
{
    /// <summary>One hittable part of a boss / big enemy (MasterMold: Head, HandLeft, HandRight).</summary>
    internal sealed class BossPart
    {
        public GameObject2d Owner;
        public string Name;
        public Rectangle Box;          // union of the part's vulnerability boxes (screen space: X, Y - Z)
        public float DMin, DMax;       // world depth (Y) range the game tests hits against
        public float HBot, HTop;       // box height range in the game's hit test (victim Position.Y - box edge)
        public bool Hurtable;
        public float CenterX => Box.X + Box.Width * 0.5f;
        public float DepthC => (DMin + DMax) * 0.5f;
    }

    internal sealed class PartSet
    {
        public float T = -1f;
        public readonly List<BossPart> Parts = new List<BossPart>(4);
        public bool Multi;             // parts owned by other objects / non-physical body / far from Position
        public string Sig = "", LoggedSig; public float LoggedT = -10f;
        public int Logs;
        public bool Announced;
    }

    /// <summary>
    /// v0.2.10: boss part targeting. Bosses like MasterMold are one Enemy whose Position is the (non-colliding) body in the
    /// background; what can be hit is the head and the two hands, exposed through the enemy's vulnerability boxes
    /// (GetVulnerabilityBoxCount / GetVulnerabilityBox / Get3DVulnerabilityBoxDepth / GetVulnerabilityBoxOwner). The game
    /// tests a hit with ParisMath.IntersectHitboxes: X overlap of the screen rects, overlap of the heights (Position.Y - box
    /// edge) and overlap of the depth ranges. The bot picks a part, stands on walkable ground inside the part's depth band
    /// (+/- the player's 8 px attack depth), within X reach of the box, and jumps if the part sits above its ground reach.
    /// </summary>
    public partial class BotController
    {
        private const float PLAYER_ATK_DEPTH = 8f, PART_EVAL = 0.25f;
        private static readonly Dictionary<Enemy, PartSet> _partSets = new Dictionary<Enemy, PartSet>();
        private static readonly List<Enemy> _partPrune = new List<Enemy>();

        // per-bot part choice
        private BossPart _partPick; private Enemy _partEnemy; private float _partEvalT;
        private float _partGroundY; private bool _partGroundOk; private float _partNeedRise;
        private readonly Dictionary<GameObject2d, float> _partBad = new Dictionary<GameObject2d, float>();
        private int _partLogs;
        // learned height range of this hero's grounded attack boxes (relative to its own Z)
        private float _atkLo = 6f, _atkHi = 48f; private bool _atkLearned; private CharacterInfoRef _atkHero;
        private sealed class CharacterInfoRef { public object Ci; }

        internal static PartSet GetParts(Enemy e)
        {
            if (e == null) return null;
            if (!_partSets.TryGetValue(e, out PartSet ps))
            {
                if (_partSets.Count > 32)
                {
                    _partPrune.Clear();
                    foreach (var kv in _partSets) if (kv.Key == null || !kv.Key.Active || kv.Key.IsKO) _partPrune.Add(kv.Key);
                    foreach (Enemy x in _partPrune) _partSets.Remove(x);
                }
                ps = new PartSet(); _partSets[e] = ps;
            }
            if (ps.T == BotManager.Time) return ps;
            ps.T = BotManager.Time;
            ps.Parts.Clear(); ps.Multi = false;
            try
            {
                int n = Math.Min(48, e.GetVulnerabilityBoxCount());
                float ey = e.Position.Y;
                for (int i = 0; i < n; i++)
                {
                    Rectangle r = e.GetVulnerabilityBox(i, true);
                    if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
                    if (e.GetVulnerabilityBoxType(i) == CollisionFlag.NONE) continue;
                    Animator2dData.HitBox info = null; try { info = e.GetVulnerabilityBoxInfo(i); } catch { }
                    if (info != null && (info.BoxType == Animator2dData.HitBox.HitBoxType.Shield || info.BoxType == Animator2dData.HitBox.HitBoxType.BodyShield ||
                                         info.BoxType == Animator2dData.HitBox.HitBoxType.Dodge)) continue;
                    GameObject2d owner = e; try { owner = e.GetVulnerabilityBoxOwner(i) ?? e; } catch { }
                    var depth = e.Get3DVulnerabilityBoxDepth(i);
                    float dmin = depth.Min, dmax = depth.Max;
                    if (dmax - dmin < 1f) { dmin = ey - 8f; dmax = ey + 8f; }   // no depth data: Character default-ish
                    BossPart p = null;
                    foreach (BossPart q in ps.Parts) if (ReferenceEquals(q.Owner, owner)) { p = q; break; }
                    if (p == null)
                    {
                        p = new BossPart { Owner = owner, Box = r, DMin = dmin, DMax = dmax };
                        ps.Parts.Add(p);
                    }
                    else
                    {
                        p.Box = Rectangle.Union(p.Box, r);
                        p.DMin = Math.Min(p.DMin, dmin); p.DMax = Math.Max(p.DMax, dmax);
                    }
                }
                foreach (BossPart p in ps.Parts)
                {
                    p.HBot = ey - p.Box.Bottom; p.HTop = ey - p.Box.Top;
                    p.Hurtable = PartHurtable(e, p.Owner);
                    if (!ReferenceEquals(p.Owner, e)) ps.Multi = true;
                    else if (Math.Abs(p.DepthC - e.Position.Y) > 14f || Math.Abs(p.CenterX - e.Position2D.X) > 40f + p.Box.Width * 0.5f) ps.Multi = true;
                }
                try { if (e.BodyComponent != null && e.BodyComponent.CollisionMask == CollisionFlag.NONE && ps.Parts.Count > 0) ps.Multi = true; } catch { }
                NameParts(e, ps);
            }
            catch (Exception ex) { Mod.LogOnce("parts", ex); ps.Parts.Clear(); ps.Multi = false; }
            return ps;
        }

        private static bool PartHurtable(Enemy e, GameObject2d owner)
        {
            try
            {
                if (e is MasterMold mm && owner is MasterMoldHead) return mm.IsHeadVulnerable && mm.CanBeHurt;
                if (owner is MasterMoldHand mh) return mh.CanBeHurt && !(mh.Owner != null && mh.Owner.IsInHandAttackState);
                if (owner is Character c) return c.CanBeHurt && !c.IsInvulnerable;
                return e.CanBeHurt && !e.IsInvulnerable;
            }
            catch { return true; }
        }

        private static void NameParts(Enemy e, PartSet ps)
        {
            var sb = new StringBuilder();
            foreach (BossPart p in ps.Parts)
            {
                string n = ReferenceEquals(p.Owner, e) ? "Body" : p.Owner is MasterMoldHead ? "Head" : p.Owner is MasterMoldHand ? "Hand" : p.Owner.GetType().Name;
                int same = 0; foreach (BossPart q in ps.Parts) if (q != p && q.Owner.GetType() == p.Owner.GetType()) same++;
                if (same > 0) n += p.CenterX < e.Position2D.X ? "(L)" : "(R)";
                p.Name = n;
                sb.Append(n).Append(p.Hurtable ? '+' : '-');
            }
            ps.Sig = sb.ToString();
        }

        /// <summary>Should this foe be fought through its parts instead of its Position? (bosses / big multi-part enemies)</summary>
        private static bool UsesParts(Foe f, out PartSet ps)
        {
            ps = null;
            if (f == null || !(f.Boss || IsLargeTarget(f.E))) return false;
            ps = GetParts(f.E);
            return ps != null && ps.Parts.Count > 0 && ps.Multi;
        }

        /// <summary>Log the boss's parts once when the fight starts and again when which parts can be hurt changes.</summary>
        private static void AnnounceParts(Enemy e, PartSet ps)
        {
            if (ps.Announced && ps.Sig == ps.LoggedSig) return;
            if (ps.Logs >= 40 || (ps.Announced && BotManager.Time - ps.LoggedT < 1.0f)) return;
            ps.Logs++; ps.LoggedSig = ps.Sig; ps.LoggedT = BotManager.Time;
            var sb = new StringBuilder();
            sb.Append(ps.Announced ? "[boss] " : "[boss] fight start: ").Append(e.GetType().Name).Append($" HP {SafeHp(e)} body at ({e.Position.X:0},{e.Position.Y:0},z {e.Position.Z:0}); parts:");
            foreach (BossPart p in ps.Parts)
                sb.Append($" {p.Name}[{(p.Hurtable ? "hittable" : "not hittable")} x {p.Box.Left}..{p.Box.Right} depth {p.DMin:0}..{p.DMax:0} height {p.HBot:0}..{p.HTop:0}]");
            try { if (e is MasterMold mm) sb.Append($" headVulnerable={mm.IsHeadVulnerable} handAttack={mm.IsInHandAttackState} state={GameRefl.StateName(e)}"); } catch { }
            ps.Announced = true;
            Log.Verbose(sb.ToString());
        }

        private static string SafeHp(Enemy e) { try { return e.HP + "/" + e.MaxHP; } catch { return "?"; } }

        /// <summary>Learn how high this hero's grounded attack boxes reach (relative to its own Z).</summary>
        private void LearnAttackHeights(Player me, object ci, bool onGround)
        {
            if (_atkHero == null || !ReferenceEquals(_atkHero.Ci, ci)) { _atkHero = new CharacterInfoRef { Ci = ci }; _atkLo = 6f; _atkHi = 48f; _atkLearned = false; }
            if (!onGround) return;
            try
            {
                int n = Math.Min(8, me.GetAttackBoxCount());
                if (n <= 0) return;
                float y = me.Position.Y, z = me.Position.Z;
                for (int j = 0; j < n; j++)
                {
                    Rectangle r = me.GetAttackBox(j, true);
                    if (r.IsEmpty) continue;
                    float hi = y - r.Top - z, lo = y - r.Bottom - z;
                    if (hi < 10f || hi > 160f) continue;
                    if (!_atkLearned) { _atkHi = hi; _atkLo = Math.Max(0f, lo); _atkLearned = true; }
                    else { _atkHi = Math.Max(_atkHi, Math.Min(hi, 120f)); _atkLo = Math.Min(_atkLo, Math.Max(0f, lo)); }
                }
            }
            catch { }
        }

        /// <summary>
        /// Per bot: choose a part of a multi-part boss and rewrite the Foe so the rest of the brain fights that part:
        /// P = a walkable ground spot in the part's depth band at the part's X, Air/H = needed jump, Invuln = part not hittable.
        /// </summary>
        private void ApplyParts(Player me, CharacterInfo ci, List<Foe> foes, Vector2 pos, bool flying)
        {
            float now = BotManager.Time;
            float myFloor = 0f; try { myFloor = me.BodyComponent != null ? me.BodyComponent.FloorHeightIgnoringPits : 0f; } catch { }
            bool canHigh = ci.Flying || ci.CanDoubleJump;
            float maxRise = flying ? 999f : ci.CanDoubleJump ? 110f : 60f;
            foreach (Foe f in foes)
            {
                if (!UsesParts(f, out PartSet ps)) continue;
                AnnounceParts(f.E, ps);

                // (re)pick
                bool curValid = _partEnemy == f.E && _partPick != null && ps.Parts.Contains(_partPick);
                if (!curValid)
                {
                    // part objects are rebuilt per frame: re-find by owner
                    if (_partEnemy == f.E && _partPick != null)
                        foreach (BossPart q in ps.Parts) if (ReferenceEquals(q.Owner, _partPick.Owner)) { _partPick = q; curValid = true; break; }
                }
                if (!curValid || now >= _partEvalT || (_partPick != null && !_partPick.Hurtable))
                {
                    _partEvalT = now + PART_EVAL;
                    BossPart best = null; float bestS = float.MaxValue, curS = float.MaxValue;
                    float bestY = 0f, bestRise = 0f; bool bestOk = false;
                    foreach (BossPart p in ps.Parts)
                    {
                        float gy = PartGroundY(me, p, pos, myFloor, out bool gok);
                        float rise = p.HBot - (myFloor + _atkHi) + 6f;
                        float s = Math.Abs(p.CenterX - pos.X) + Math.Abs(gy - pos.Y) * 1.5f;
                        if (!p.Hurtable) s += 600f;
                        if (!gok && !flying) s += 250f;
                        if (rise > maxRise) s += 300f; else if (rise > 0f) s += rise * (canHigh ? 0.5f : 1.0f);
                        if (_partBad.TryGetValue(p.Owner, out float badUntil) && now < badUntil) s += 400f;
                        int claimed = 0;
                        foreach (BotController b in Live) if (b != this && b._partEnemy == f.E && b._partPick != null && ReferenceEquals(b._partPick.Owner, p.Owner)) claimed++;
                        s += 70f * claimed;
                        bool isCur = curValid && ReferenceEquals(p.Owner, _partPick.Owner);
                        if (isCur) { s -= 35f; curS = s; }
                        if (s < bestS) { bestS = s; best = p; bestY = gy; bestOk = gok; bestRise = rise; }
                    }
                    if (best != null)
                    {
                        bool changed = !curValid || !ReferenceEquals(best.Owner, _partPick.Owner);
                        _partPick = best; _partEnemy = f.E; _partGroundY = bestY; _partGroundOk = bestOk; _partNeedRise = bestRise;
                        if (changed)
                        {
                            Stat("part-pick");
                            if (_partLogs++ < 60)
                                Log.Verbose($"[boss P{Info.PlayerID + 1}] {Stats.Hero} -> {f.E.GetType().Name} {best.Name} ({(best.Hurtable ? "hittable" : "not hittable")}): stand at ({best.CenterX:0},{bestY:0})" +
                                         $"{(bestOk ? "" : " [no walkable spot in its depth band]")}, part height {best.HBot:0}..{best.HTop:0}, my reach {myFloor + _atkLo:0}..{myFloor + _atkHi:0}" +
                                         $"{(bestRise > 0f ? $", needs a {bestRise:0} px jump" : ", ground attacks reach")}");
                        }
                    }
                }
                else
                {
                    // refresh geometry of the current pick every frame (hands move)
                    _partGroundY = PartGroundY(me, _partPick, pos, myFloor, out _partGroundOk);
                    _partNeedRise = _partPick.HBot - (myFloor + _atkHi) + 6f;
                }
                if (_partPick == null || _partEnemy != f.E) continue;

                BossPart pk = _partPick;
                f.Part = pk;
                f.P = new Vector2(pk.CenterX, _partGroundY);
                f.ExtraReach = Math.Min(60f, pk.Box.Width * 0.5f * 0.8f);
                f.LaneTol = MathHelper.Clamp((pk.DMax - pk.DMin) * 0.5f + PLAYER_ATK_DEPTH - 4f, LANE_TOLERANCE, 30f);
                f.Air = _partNeedRise > 0f && !flying;
                f.H = f.Air ? _partNeedRise + 10f : 0f;
                f.Flyer = false;
                f.Invuln = !pk.Hurtable;
                f.Label = f.E.GetType().Name + ":" + pk.Name;
            }
        }

        /// <summary>Nearest walkable ground Y to the part's depth centre where our attack depth still overlaps the part.</summary>
        private static float PartGroundY(Player me, BossPart p, Vector2 pos, float myFloor, out bool ok)
        {
            float c = p.DepthC;
            float lo = p.DMin - PLAYER_ATK_DEPTH + 2f, hi = p.DMax + PLAYER_ATK_DEPTH - 2f;
            if (hi < lo) { lo = hi = c; }
            float x = p.CenterX;
            // prefer the depth closest to where we are (inside the band), then the band centre outward
            float start = MathHelper.Clamp(pos.Y, lo, hi);
            for (int k = 0; k <= 20; k++)
            {
                float off = (k + 1) / 2 * 4f * (k % 2 == 0 ? 1f : -1f);
                float y = start + off;
                if (y < lo - 0.01f || y > hi + 0.01f) continue;
                if (SpotWalkable(me, new Vector2(x, y), myFloor)) { ok = true; return y; }
            }
            // none: the nearest walkable depth outside the band (approach as close as the arena allows)
            ok = false;
            for (int k = 1; k <= 20; k++)
            {
                float d = k * 4f;
                if (SpotWalkable(me, new Vector2(x, hi + d), myFloor)) return hi + d;
                if (SpotWalkable(me, new Vector2(x, lo - d), myFloor)) return lo - d;
            }
            return start;
        }

        /// <summary>Our current part target was walled off: try another part for 2 s.</summary>
        private void MarkPartBad(float secs)
        {
            if (_partPick == null) return;
            if (_partBad.TryGetValue(_partPick.Owner, out float until) && BotManager.Time < until) return;
            _partBad[_partPick.Owner] = BotManager.Time + secs;
            if (_partBad.Count > 16) _partBad.Clear();
            _partEvalT = 0f;
            Stat("part-unreachable");
        }

        // ---------------------------------------------------------------- v0.2.10: attack-suppressed bookkeeping
        private string _suppWhy; private bool _suppMark;
        /// <summary>Something kept the bot from attacking this frame: count the episode and its time by reason.</summary>
        private void Suppress(string why)
        {
            if (why != _suppWhy) { Stats.Add(BotStats.SuppPrefix + why); _suppWhy = why; }
            Stats.AddTime(BotStats.SuppPrefix + why, _lastDt);
            _suppMark = true;
        }
        private void SuppressFrameStart() { if (!_suppMark) _suppWhy = null; _suppMark = false; }
    }
}
