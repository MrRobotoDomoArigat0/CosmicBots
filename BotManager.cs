using System;
using System.Collections.Generic;
using HarmonyLib;
using Paris.Engine;
using Paris.Engine.Data;
using Paris.Engine.Context;
using Paris.Engine.Input;
using Paris.Engine.Networking;
using Paris.Engine.Player;
using Paris.Engine.Scene;
using Paris.Game;
using Paris.Game.Data;
using Paris.Game.HUD;
using Paris.Game.Menu;
using Paris.Game.Menu.Control;
using Paris.Game.System;

namespace CosmicBots
{
    public enum SlotState { Off, Bot, Human, Pending, Blocked }

    /// <summary>Adds/removes bot players for P2..P4 and decides when bots lock in heroes.</summary>
    public static class BotManager
    {
        public static float Time;
        private static float _charSelectSeen = -10f, _hudSeen = -10f, _reconcileTimer;
        public static CharacterSelect CharSelect;
        public static MainHUD Hud;
        public static int ForcedPlayerId = -1;
        public static readonly bool[] ForceLockIn = new bool[4];
        public static readonly bool[] RequeueOnSelect = new bool[4];
        private static readonly float[] RequeueAt = new float[4];
        /// <summary>Debounced re-pick: every hero change restarts a 1 s timer, so cycling through heroes causes one re-join, not one per step.</summary>
        public static void RequestRequeue(int slot) { RequeueOnSelect[slot] = true; RequeueAt[slot] = Time + 1.0f; }
        public static string LastAction = "";

        public static bool InCharSelect => CharSelect != null && Time - _charSelectSeen < 0.3f;
        public static bool InLevel => Hud != null && Time - _hudSeen < 0.3f && Scene2d.Active != null;

        public static void SeenCharSelect(CharacterSelect cs) { CharSelect = cs; _charSelectSeen = Time; }
        public static void SeenHud(MainHUD hud) { Hud = hud; _hudSeen = Time; }

        public static bool Online
        {
            get
            {
                try
                {
                    var nm = NetworkManagerBase.Singleton;
                    if (nm != null && nm.IsConnected) return true;
                    var pm = PlayerManager.Singleton;
                    return pm != null && pm.HasNetworkPlayers();
                }
                catch { return false; }
            }
        }

        public static CharacterSelectionContainer Container
        {
            get
            {
                if (InCharSelect) return CharSelectContainerRef(CharSelect);
                if (InLevel) return Hud.CharSelectionContainer;
                return null;
            }
        }

        private static readonly AccessTools.FieldRef<CharacterSelect, CharacterSelectionContainer> CharSelectContainerRef =
            AccessTools.FieldRefAccess<CharacterSelect, CharacterSelectionContainer>("_charContainer");

        public static bool ContextReady
        {
            get
            {
                var cm = ContextManager.Singleton;
                if (cm == null || cm.State != ContextManager.LoadingState.None) return false;
                return (InCharSelect || InLevel) && PlayerManager.Singleton?.LocalHostPlayer != null;
            }
        }

        public static PlayerInfo GetPlayer(int slot) => PlayerManager.Singleton?.GetPlayerByID(slot);

        public static bool CastleDoomMode => CastleDoom.Active;
        private static bool _loggedRunLocked;

        public static SlotState GetState(int slot)
        {
            PlayerInfo pi = GetPlayer(slot);
            if (pi != null) return pi.Controller is BotController ? SlotState.Bot : SlotState.Human;
            if (Online) return SlotState.Blocked;
            return Config.Slots[slot].Enabled ? SlotState.Pending : SlotState.Off;
        }

        public static void Tick(float dt)
        {
            Time += dt;
            _reconcileTimer -= dt;
            if (_reconcileTimer > 0f) return;
            _reconcileTimer = 0.25f;
            try { Reconcile(); } catch (Exception ex) { Mod.LogOnce("reconcile", ex); }
        }

        private static void Reconcile()
        {
            PlayerManager pm = PlayerManager.Singleton;
            if (pm == null) return;
            bool online = Online;
            // Castle Doom: once the run is locked the game removes anyone who joins mid-run, so bots only join before that
            // (character select, or a level before the lock). Bots already in the run stay.
            bool noJoin = InLevel && CastleDoom.RunLocked;
            if (noJoin && !_loggedRunLocked) { _loggedRunLocked = true; Log.Info("Castle Doom: the run is locked, so new bots can't join until the next run (bots already in the run stay)."); }
            if (!noJoin) _loggedRunLocked = false;
            for (int slot = 1; slot <= 3; slot++)
            {
                PlayerInfo pi = pm.GetPlayerByID(slot);
                bool isBot = pi?.Controller is BotController;
                bool want = Config.Slots[slot].Enabled && !online;
                if (isBot && (!want || RequeueNow(slot, pi)))
                {
                    if (ContextReady || online) Remove(slot, online ? "online session detected" : !want ? "disabled" : "re-picking hero");
                }
                else if (pi == null && want && !noJoin && ContextReady && CanAddMore())
                {
                    Add(slot);
                }
            }
        }

        // Hero changed in the menu after this bot already locked in on the select screen -> drop and re-add.
        private static bool RequeueNow(int slot, PlayerInfo pi)
        {
            if (!RequeueOnSelect[slot]) return false;
            if (!InCharSelect) { RequeueOnSelect[slot] = false; return false; }
            if (Time < RequeueAt[slot]) return false;   // still cycling heroes
            RequeueOnSelect[slot] = false;
            return true;
        }

        private static bool CanAddMore()
        {
            int max = EngineSettings.Singleton.MaxPlayers;
            var c = Container;
            if (c != null) max = Math.Min(max, c.MaxPlayerAllowed);
            return PlayerManager.Singleton.GetPlayerCount() < max;
        }

        public static void Add(int slot)
        {
            PlayerManager pm = PlayerManager.Singleton;
            ulong handle = 0;
            while (handle < 16 && pm.ControllerAlreadyUsed(ParisInputType.Debug, handle)) handle++;
            Type prev = PlayerManager.DebugControllerType;
            PlayerManager.DebugControllerType = typeof(BotController);
            ForcedPlayerId = slot;
            try
            {
                PlayerInfo pi = pm.AddPlayer(ParisInputType.Debug, handle);
                LastAction = pi != null ? $"P{slot + 1} bot joined" : $"P{slot + 1} bot could not join";
                Log.Info(LastAction + (pi != null ? $" (PlayerID {pi.PlayerID}, handle {handle})" : ""));
                if (pi != null && pi.PlayerID != slot) Log.Warn($"Bot got PlayerID {pi.PlayerID} instead of {slot}");
            }
            finally
            {
                ForcedPlayerId = -1;
                PlayerManager.DebugControllerType = prev;
            }
            ForceLockIn[slot] = false;
        }

        public static void Remove(int slot, string why)
        {
            PlayerManager pm = PlayerManager.Singleton;
            PlayerInfo pi = pm.GetPlayerByID(slot);
            if (pi == null || !(pi.Controller is BotController bot)) return;
            try
            {
                if (Scene2d.Active != null && Scene2d.Active.Players != null)
                {
                    if (bot.Owner == null && bot.ActivePlayer != null) bot.Owner = bot.ActivePlayer;
                    bot.Deactivate(); // same sequence the game's own "remove debug player" cheat uses
                }
            }
            catch (Exception ex) { Log.Warn("Deactivate failed: " + ex.Message); }
            if (pi is GamePlayerInfo gpi) gpi.SelectedCharacter = byte.MaxValue;
            pm.RemovePlayer(slot);
            ForceLockIn[slot] = false;
            LastAction = $"P{slot + 1} bot left ({why})";
            Log.Info(LastAction);
        }

        // ------------------------------------------------------------ hero selection
        public static SaveGame HostSave => PlayerManager.Singleton?.LocalHostPlayer?.Save as SaveGame;

        public static bool IsHeroAvailable(int idx)
        {
            var list = CharacterManager.Singleton?.Characters;
            if (list == null || idx < 0 || idx >= list.Count) return false;
            CharacterInfo ci = list[idx];
            try { return ci != null && !ci.IsLocked(HostSave) && !ci.IsDlcLocked(); } catch { return false; }
        }

        public static List<int> AvailableHeroes()
        {
            var r = new List<int>();
            var list = CharacterManager.Singleton?.Characters;
            if (list == null) return r;
            for (int i = 0; i < list.Count; i++) if (IsHeroAvailable(i)) r.Add(i);
            return r;
        }

        public static int IndexOf(string internalName)
        {
            if (string.IsNullOrEmpty(internalName)) return -1;
            var list = CharacterManager.Singleton?.Characters;
            if (list == null) return -1;
            for (int i = 0; i < list.Count; i++) if (string.Equals(list[i]?.InternalName, internalName, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public static string HeroName(int idx)
        {
            var list = CharacterManager.Singleton?.Characters;
            if (list == null || idx < 0 || idx >= list.Count || list[idx] == null) return "?";
            string n = null;
            try { n = list[idx].ShortName?.GetString(); } catch { }
            if (string.IsNullOrEmpty(n) || n.StartsWith("#")) n = list[idx].InternalName;
            return Menu.Sanitize(n);
        }

        /// <summary>Hero index the bot in `slot` should confirm next (main or partner).</summary>
        public static int ResolveHero(int slot, bool partner, GamePlayerInfo pi)
        {
            var cfg = Config.Slots[slot];
            int main = pi.SelectedCharacter;
            int want = IndexOf(partner ? cfg.PartnerHero : cfg.MainHero);
            if (want >= 0 && IsHeroAvailable(want) && !(partner && want == main)) return want;
            // automatic: a stable pseudo-random available hero, avoiding our own main pick and other players' mains
            List<int> avail = AvailableHeroes();
            if (avail.Count == 0) return -1;
            var taken = new HashSet<int>();
            foreach (PlayerInfo other in PlayerManager.Singleton.Players)
                if (other is GamePlayerInfo g && g != pi && g.SelectedCharacter != byte.MaxValue) { taken.Add(g.SelectedCharacter); taken.Add(g.SelectedSecondaryCharacter); }
            int seed = slot * 7 + (partner ? 3 : 0);
            for (int k = 0; k < avail.Count; k++)
            {
                int c = avail[(seed + k * 5) % avail.Count];
                if (partner && c == main) continue;
                if (!taken.Contains(c)) return c;
            }
            foreach (int c in avail) if (!(partner && c == main)) return c;
            return -1;
        }

        public static bool ShouldLockIn(CharacterSelectionPanel panel, int slot, bool gameMode)
        {
            if (ForceLockIn[slot] || gameMode) return true;
            if (!Config.AutoLockInWhenHumansReady) return false;
            CharacterSelectionContainer c = Container;
            if (c?.Panels == null) return false;
            int humans = 0;
            foreach (CharacterSelectionPanel p in c.Panels)
            {
                if (p?.PlayerInfo == null || p.PlayerInfo.Controller is BotController) continue;
                humans++;
                if (!p.IsReady) return false;
            }
            return humans > 0;
        }
    }
}
