using System;
using HarmonyLib;
using Paris.Engine;
using Paris.Game.Actor;
using Paris.Game.Attacks;
using Paris.Game.Damage;

namespace CosmicBots
{
    /// <summary>Reflection accessors for non-public game members the bot brain reads (all read-only).</summary>
    public static class GameRefl
    {
        private static AccessTools.FieldRef<Actor2d, FSM> _fsm;
        private static AccessTools.FieldRef<Player, bool> _teamReady;
        private static AccessTools.FieldRef<Player, PlayerAttackStats> _special;
        private static Func<Player, int> _stdCount;
        private static AccessTools.FieldRef<Projectile, DamageInfoEx> _projDmg;
        private static System.Reflection.MethodInfo _shieldType;
        private static AccessTools.FieldRef<Player, float> _blockTimer;
        private static bool _init, _ok;

        public static bool Ok { get { Init(); return _ok; } }

        public static void Init()
        {
            if (_init) return;
            _init = true;
            try
            {
                _fsm = AccessTools.FieldRefAccess<Actor2d, FSM>("_fsm");
                _teamReady = AccessTools.FieldRefAccess<Player, bool>("_isSpecialReadyForTeamUp");
                _special = AccessTools.FieldRefAccess<Player, PlayerAttackStats>("_specialAttack");
                var m = AccessTools.Method(typeof(Player), "GetStandardAttackCount");
                if (m != null) _stdCount = AccessTools.MethodDelegate<Func<Player, int>>(m, null, true);
                try { _projDmg = AccessTools.FieldRefAccess<Projectile, DamageInfoEx>("_currentDamageInfo"); } catch (Exception ex) { Mod.LogOnce("refl-proj", ex); }
                try { _blockTimer = AccessTools.FieldRefAccess<Player, float>("_blockStartTimer"); } catch (Exception ex) { Mod.LogOnce("refl-blocktimer", ex); }
                try { _shieldType = AccessTools.PropertyGetter(typeof(Character), "ShieldBlockType"); } catch (Exception ex) { Mod.LogOnce("refl-shield", ex); }
                _ok = true;
            }
            catch (Exception ex) { Mod.LogOnce("refl-init", ex); }
        }

        /// <summary>v0.2.9: Player._blockStartTimer (seconds since the current block started; parry if <= window), or -1.</summary>
        public static float BlockTimer(Player p)
        {
            if (p == null || !Ok || _blockTimer == null) return -1f;
            try { return _blockTimer(p); } catch { return -1f; }
        }

        /// <summary>Name of the actor's current FSM state method (e.g. "StateAttackMelee"), or "" if unknown.</summary>
        public static string StateName(Actor2d a)
        {
            if (a == null || !Ok) return "";
            try { FSM f = _fsm(a); return f != null && f.HasState ? f.CurrentStateName ?? "" : ""; } catch { return ""; }
        }

        /// <summary>v0.2.7: FSM.ChangeState by state method name (e.g. "StateFall"); false if the state does not exist.</summary>
        public static bool ChangeState(Actor2d a, string stateName)
        {
            if (a == null || !Ok) return false;
            try
            {
                FSM f = _fsm(a); if (f == null) return false;
                for (int i = 0; i < 255; i++)
                {
                    string n = f.GetStateName((byte)i);
                    if (n == "Invalid") break;
                    if (n == stateName) { f.ChangeState((byte)i); return true; }
                }
            }
            catch (Exception ex) { Mod.LogOnce("refl-chstate", ex); }
            return false;
        }

        public static bool TeamUpReady(Player p)
        {
            if (p == null || !Ok) return false;
            try { return _teamReady(p); } catch { return false; }
        }

        public static int SpecialCost(Player p)
        {
            if (p == null || !Ok) return -1;
            try { PlayerAttackStats s = _special(p); return s != null && s.EnergyCost < 100000 ? s.EnergyCost : -1; /* >=100000 = unusable placeholder special */ } catch { return -1; }
        }

        public static int StandardAttackCount(Player p)
        {
            try { if (_stdCount != null) return _stdCount(p); } catch { }
            try { return p.CurrentCharacterInfo?.Attack?.Count ?? 0; } catch { return 0; }
        }

        /// <summary>Damage info a hostile projectile will apply (CanBeBlocked / CanBeParried / IsShieldBreaker), or null.</summary>
        public static DamageInfoEx ProjectileDamage(Projectile p)
        {
            if (p == null || !Ok || _projDmg == null) return null;
            try { return _projDmg(p); } catch { return null; }
        }

        /// <summary>True for heroes whose block is a real shield (ShieldBlockType.Shield: Captain America, Silver Surfer): they block even "unblockable" hits unless shield-breaking.</summary>
        public static bool HasHardShield(Character c)
        {
            if (c == null || !Ok || _shieldType == null) return false;
            try { object v = _shieldType.Invoke(c, null); return v != null && v.ToString() == "Shield"; } catch { return false; }
        }
    }
}
