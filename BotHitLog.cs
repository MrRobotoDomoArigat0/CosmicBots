using System;
using System.Runtime.CompilerServices;
using Paris.Engine;
using Paris.Engine.Combat;
using Paris.Game.Actor;
using Paris.Game.Combat;
using Paris.Game.Damage;

namespace CosmicBots
{
    /// <summary>Hit bookkeeping for the bots: feeds the "took a burst of hits" escape and the skin/rush learning. Writes no logs.</summary>
    public partial class BotController
    {
        private int PTag { get { try { return Info != null ? Info.PlayerID + 1 : 0; } catch { return 0; } } }

        // ---------------------------------------------------------------- labels (cached per game object)
        private static readonly ConditionalWeakTable<object, string> _labelCache = new ConditionalWeakTable<object, string>();

        /// <summary>"Enemy[SwarmerSpiker]" (class + sprite-set leaf: the game's enemies are data driven, most share the class Enemy).</summary>
        internal static string LabelOf(object o)
        {
            if (o == null) return "?";
            try
            {
                if (_labelCache.TryGetValue(o, out string s)) return s;
                s = BuildLabel(o);
                try { _labelCache.Add(o, s); } catch { }
                return s;
            }
            catch { return o.GetType().Name; }
        }

        private static string BuildLabel(object o)
        {
            string type = o.GetType().Name;
            string set = null;
            try
            {
                if (o is Paris.Engine.GameObject.AnimatedGameObject ago) set = ago.AnimationSet;
            }
            catch { }
            if (!string.IsNullOrEmpty(set))
            {
                int i = Math.Max(set.LastIndexOf('\\'), set.LastIndexOf('/'));
                if (i >= 0 && i < set.Length - 1) set = set.Substring(i + 1);
                type += "[" + Menu.Sanitize(set) + "]";
            }
            if (o is Enemy e)
            {
                try { if (e.IsFlyingType) type += "(flyer)"; } catch { }
            }
            else if (o is Projectile pr)
            {
                try { if (pr.Owner != null) type += "<-" + LabelOf(pr.Owner); } catch { }
            }
            return type;
        }

        private static string AttackName(DamageInfoEx dmg)
        {
            try { string n = dmg?.Attack?.Name; return string.IsNullOrEmpty(n) ? null : Menu.Sanitize(n); } catch { return null; }
        }

        // ---------------------------------------------------------------- per-hit bookkeeping
        private const int BURST_HITS = 5; private const float BURST_WINDOW = 3f;
        private readonly float[] _hitT = new float[16]; private int _hitN;

        /// <summary>Harmony postfix on Character.InternalHurt (victim = any character; only bot players are handled).</summary>
        internal static void OnHitDetail(Character victim, DamageInfoEx dmg, AttackResultEx res)
        {
            try
            {
                if (!(victim is Player p) || !(p.PlayerInfo?.Controller is BotController b)) return;
                b.HitDetail(p, dmg, res);
            }
            catch (Exception ex) { Mod.LogOnce("hitdetail", ex); }
        }

        private void HitDetail(Player p, DamageInfoEx dmg, AttackResultEx res)
        {
            float now = BotManager.Time;
            GameObject2d owner = null;
            try { owner = dmg?.Owner; } catch { }
            if (owner == null) { try { owner = p.LastAttacker; } catch { } }
            Enemy en = owner as Enemy;

            string atkName = AttackName(dmg);
            try { RushHitNote(atkName, now); } catch { }

            // ring buffer of the last 16 hit times (burst detector)
            int slot = _hitN & 15; _hitT[slot] = now; _hitN++;
            try { if (en != null) SkinObserve(dmg?.Attack?.Name, p.Position2D.X - en.Position2D.X); } catch { }   // learn how far a skin reaches
            TryBurstEscape(p, now);
        }
    }
}
