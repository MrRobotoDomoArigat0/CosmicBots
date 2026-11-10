using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Paris.Engine.Player;
using Paris.Game;
using Paris.Game.Data;
using Paris.Game.Menu;

namespace CosmicBots
{
    /// <summary>
    /// Makes bots take part in screens where every player must vote/confirm, by feeding them menu input
    /// through BotController (so the game's own vote logic, sounds and network messages run unchanged).
    /// Audit of Game.exe: the only screen that waits on input from EVERY player is the arcade
    /// PATH SELECT vote (MissionSelect.UpdateArcadeModeSelection). Character select is handled by the
    /// select hook; the arcade continue prompt auto-accepts Debug-type controllers in vanilla code;
    /// pause, results, Cosmic Vault, credits, Corps Files and story mission select read only the host's controller.
    /// </summary>
    public static class MenuFollow
    {
        private const float STEP_DELAY = 0.15f;     // seconds between bot cursor actions (one action per step)
        private const float CONFIRM_DELAY = 0.25f;  // pause after the leader confirms before a bot confirms
        private static readonly float[] _nextStep = new float[4];
        private static readonly byte[] _lastLeaderState = new byte[4];
        private static float _leaderConfirmedAt = -1f;
        private static byte _leaderPrev = byte.MaxValue;

        private static readonly AccessTools.FieldRef<MissionSelect, byte[]> VoteState =
            AccessTools.FieldRefAccess<MissionSelect, byte[]>("_playerSelectionState");

        public static void Apply(Harmony h)
        {
            var m = AccessTools.Method(typeof(MissionSelect), "UpdateArcadeModeSelection", Type.EmptyTypes);
            if (m == null) { Log.Error("MissionSelect.UpdateArcadeModeSelection not found; bots will not vote on PATH SELECT"); return; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(MenuFollow), nameof(VotePrefix)), postfix: new HarmonyMethod(typeof(MenuFollow), nameof(VotePostfix)));
            Log.Info("Hooked Paris.Game.Menu.MissionSelect.UpdateArcadeModeSelection (path select vote follow)");
        }

        /// <summary>Lowest-slot human player (P1 first). Returns -1 if none.</summary>
        private static int Leader(byte[] state)
        {
            for (int i = 0; i < state.Length; i++)
            {
                PlayerInfo pi = PlayerManager.Singleton.GetPlayerByID(i);
                if (pi != null && !(pi.Controller is BotController)) return i;
            }
            return -1;
        }

        public static void VotePrefix(MissionSelect __instance)
        {
            try
            {
                if (GameInfo.Singleton.GameMode != GameMode.Arcade || __instance.DisableInput) return;
                byte[] state = VoteState(__instance);
                if (state == null) return;
                int leader = Leader(state);
                if (leader < 0) return;
                byte L = state[leader];
                if (L == byte.MaxValue) return; // leader's cursor not placed yet (first frame)
                bool leaderConfirmed = L >= 2;
                int leaderPanel = L % 2;       // 0 = top path, 1 = bottom path; 2/3 = confirmed on 0/1
                if (L != _leaderPrev)
                {
                    if (leaderConfirmed) _leaderConfirmedAt = BotManager.Time;
                    _leaderPrev = L;
                }

                for (int i = 0; i < state.Length && i < 4; i++)
                {
                    PlayerInfo pi = PlayerManager.Singleton.GetPlayerByID(i);
                    if (!(pi?.Controller is BotController bot)) continue;
                    bot.ClearMenuIntent();
                    byte s = state[i];
                    if (s == byte.MaxValue || BotManager.Time < _nextStep[i]) continue;
                    bool confirmed = s >= 2;
                    int panel = s % 2;

                    if (confirmed && (!leaderConfirmed || panel != leaderPanel))
                        bot.MenuPress = "MenuCancel";                       // human changed/unconfirmed -> follow
                    else if (!confirmed && panel != leaderPanel)
                        bot.MenuDir = leaderPanel == 0 ? Vector2.UnitY : -Vector2.UnitY; // move cursor to the human's path
                    else if (!confirmed && leaderConfirmed && BotManager.Time - _leaderConfirmedAt >= CONFIRM_DELAY)
                        bot.MenuPress = "MenuAccept";                       // same path as the human -> confirm
                    else continue;
                    _nextStep[i] = BotManager.Time + STEP_DELAY;
                }
            }
            catch (Exception ex) { Mod.LogOnce("path-vote", ex); }
        }

        public static void VotePostfix()
        {
            try
            {
                foreach (PlayerInfo pi in PlayerManager.Singleton.Players)
                    if (pi?.Controller is BotController bot) bot.ClearMenuIntent();
            }
            catch { }
        }
    }
}
