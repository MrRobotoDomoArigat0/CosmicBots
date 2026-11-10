using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Paris.Engine;
using Paris.Engine.GameObject;
using Paris.Engine.Player;
using Paris.Engine.Scene;
using Paris.Engine.Stages;
using Paris.Game;
using Paris.Game.Actor;
using Paris.Game.HUD;
using Paris.Game.Menu;

namespace CosmicBots
{
    /// <summary>
    /// bots in the "Siege of Castle Doom" (roguelite) mode. Everything here is a no-op outside that mode.
    /// Shared menus (Reed's Lab, results, failure screen, doors, equipment pickups) stay with the human players: bots never
    /// press the pickup/use button, and the game drives those screens from the host's controller. Only the per-player steps that
    /// would otherwise wait for every player are answered for the bots, using the game's own paths:
    ///  - Reed's Lab vote screen (FilterAgency): each bot presses "Ready" (abstains) on every page, so the humans' votes decide.
    ///  - Perk selection (PerkSelectPanel): each bot uses the game's own time-out auto-pick (random perk type, a perk not maxed yet).
    ///  - The elevator exit is not triggered by a bot while a human player is still standing (the human decides when to leave).
    /// In-run, the normal bot brain plays; BotBrain adds Castle Doom trap avoidance only in this mode.
    /// </summary>
    public static class CastleDoom
    {
        public static bool Active
        {
            get { try { return GameInfo.Singleton != null && GameInfo.Singleton.GameMode == GameMode.CastleDoom; } catch { return false; } }
        }

        /// <summary>True once the run is locked (enough Stabilizers): the game then removes anyone who tries to join mid-run.</summary>
        public static bool RunLocked
        {
            get { try { return Active && GameInfo.Singleton.CastleDoomInfo != null && GameInfo.Singleton.CastleDoomInfo.IsLocked; } catch { return false; } }
        }

        private static MethodInfo _readyAndReplicate;
        private static AccessTools.FieldRef<FilterAgency, Dictionary<PlayerInfo, bool>> _playersReady;
        private static AccessTools.FieldRef<PerkSelectPanel, FSM> _perkFsm;
        private static AccessTools.FieldRef<PerkSelectPanel, GamePlayerInfo> _perkPlayer;

        public static void Apply(Harmony h)
        {
            try
            {
                _readyAndReplicate = AccessTools.Method(typeof(FilterAgency), "ReadyAndReplicate", new[] { typeof(PlayerInfo), typeof(bool) });
                _playersReady = AccessTools.FieldRefAccess<FilterAgency, Dictionary<PlayerInfo, bool>>("_playersReady");
                Hook(h, AccessTools.Method(typeof(FilterAgency), "StateVote", new[] { typeof(FSM.StateStep), typeof(float) }), postfix: nameof(VotePostfix));
            }
            catch (Exception ex) { Log.Exception("Castle Doom: vote screen hook failed (bots will not ready up on Reed's Lab votes)", ex); }
            try
            {
                _perkFsm = AccessTools.FieldRefAccess<PerkSelectPanel, FSM>("_fsm");
                _perkPlayer = AccessTools.FieldRefAccess<PerkSelectPanel, GamePlayerInfo>("_playerInfo");
                Hook(h, AccessTools.Method(typeof(PerkSelectPanel), "Tick", new[] { typeof(float) }), postfix: nameof(PerkPanelPostfix));
            }
            catch (Exception ex) { Log.Exception("Castle Doom: perk hook failed (bot perks fall back to the game's own selection time-out)", ex); }
            try { Hook(h, AccessTools.Method(typeof(RoomExit), "DoTrigger", new[] { typeof(GameObject2d) }), prefix: nameof(RoomExitPrefix)); }
            catch (Exception ex) { Log.Exception("Castle Doom: elevator exit hook failed", ex); }
        }

        private static void Hook(Harmony h, MethodBase m, string prefix = null, string postfix = null)
        {
            if (m == null) { GamePatches.Failed++; Log.Error("Castle Doom hook target missing for " + (prefix ?? postfix)); return; }
            h.Patch(m, prefix: prefix == null ? null : new HarmonyMethod(typeof(CastleDoom), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(CastleDoom), postfix));
            GamePatches.Installed++;
            Log.Verbose("Hooked " + m.DeclaringType.FullName + "." + m.Name);
        }

        // ------------------------------------------------------------------ Reed's Lab vote screen
        private static float _voteSince = -1f;
        private const float VOTE_READY_DELAY = 0.75f;

        public static void VotePostfix(FilterAgency __instance, FSM.StateStep stateStep)
        {
            try
            {
                if (stateStep == FSM.StateStep.Constructor) { _voteSince = BotManager.Time; return; }
                if (stateStep != FSM.StateStep.Tick || _readyAndReplicate == null || _playersReady == null) return;
                if (_voteSince < 0f) _voteSince = BotManager.Time;
                if (BotManager.Time - _voteSince < VOTE_READY_DELAY) return;
                var ready = _playersReady(__instance);
                if (ready == null) return;
                foreach (PlayerInfo p in PlayerManager.Singleton.Players)
                {
                    if (!(p?.Controller is BotController) || !ready.ContainsKey(p) || ready[p]) continue;
                    _readyAndReplicate.Invoke(__instance, new object[] { p, false });
                    Log.Info($"Castle Doom: P{p.PlayerID + 1} bot ready on the Reed's Lab vote (no vote; the humans decide)");
                }
            }
            catch (Exception ex) { Mod.LogOnce("cd-vote", ex); }
        }

        // ------------------------------------------------------------------ perk selection
        private static readonly Dictionary<PerkSelectPanel, float> _perkWait = new Dictionary<PerkSelectPanel, float>();
        private const float PERK_PICK_DELAY = 1.2f;   // short "thinking" pause so the human sees the bot's panel

        public static void PerkPanelPostfix(PerkSelectPanel __instance)
        {
            try
            {
                if (_perkFsm == null || _perkPlayer == null) return;
                GamePlayerInfo pi = _perkPlayer(__instance);
                if (!(pi?.Controller is BotController)) { _perkWait.Remove(__instance); return; }
                FSM f = _perkFsm(__instance);
                byte st = f.CurrentState;
                if (st != 4 && st != 7) { _perkWait.Remove(__instance); return; }     // 4 = PerkTypeSelection, 7 = PerkIdSelection
                if (!_perkWait.TryGetValue(__instance, out float since)) { _perkWait[__instance] = BotManager.Time; return; }
                if (BotManager.Time - since < PERK_PICK_DELAY) return;
                _perkWait.Remove(__instance);
                __instance.StartAutoSelection();   // the game's own time-out pick for this one panel
                Log.Info($"Castle Doom: P{pi.PlayerID + 1} bot picks a perk (game auto-pick)");
            }
            catch (Exception ex) { Mod.LogOnce("cd-perk", ex); }
        }

        // ------------------------------------------------------------------ elevator exit
        public static bool RoomExitPrefix(RoomExit __instance, GameObject2d obj)
        {
            try
            {
                if (!(__instance is CastleDoomElevatorExit)) return true;
                if (!(obj is Player p) || !(p.PlayerInfo?.Controller is BotController)) return true;
                return !AnyHumanStanding();   // let a bot take the exit only if no human can
            }
            catch { return true; }
        }

        public static bool AnyHumanStanding()
        {
            Scene2d scene = Scene2d.Active;
            if (scene?.Players == null) return false;
            foreach (var kv in scene.Players)
            {
                if (kv.Key == null || kv.Key.Controller is BotController || kv.Value == null) continue;
                foreach (IPlayer ip in kv.Value)
                    if (ip is Player h && h.IsActivePlayerObject && !h.IsKO) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ trap avoidance (used by BotBrain in this mode only)
        public static Vector2 TrapAvoid(Scene2d scene, Vector2 pos)
        {
            Vector2 push = Vector2.Zero;
            void Away(Vector2 hp, float r)
            {
                float d = Vector2.Distance(hp, pos);
                if (d < r && d > 0.01f) push += (pos - hp) / d * (r - d) / r * 1.5f;
            }
            try
            {
                foreach (object o in scene.GetGameObjectsOfType<Hazard>())
                {
                    if (!(o is Hazard hz) || !hz.Active) continue;
                    string st = GameRefl.StateName(hz);
                    if (o is TeslaCoil && st == "StatePreAttack") Away(hz.Position2D, (hz.AttackRadius > 0f ? hz.AttackRadius : 48f) + 18f);
                    else if (o is PendulumBlade && st != "StateRetracting" && st != "StateDead" && st != "StateOff" && st != "StateHidden") Away(hz.Position2D, 56f);
                }
                foreach (object o in scene.GetGameObjectsOfType<Paris.Game.Actor.Trap.SentryLaser>())
                {
                    if (!(o is Paris.Game.Actor.Trap.SentryLaser sl) || !sl.Active) continue;
                    string st = sl._fsm?.CurrentStateName ?? "";
                    if (st == "StateFollowTarget" || st == "StateTrigger") Away(sl.Position2D, 44f);
                }
            }
            catch (Exception ex) { Mod.LogOnce("cd-traps", ex); }
            return push;
        }
    }
}
