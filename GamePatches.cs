using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Paris.Engine;
using Paris.Engine.Achievements;
using Paris.Engine.Input;
using Paris.Engine.Player;
using Paris.Game;
using Paris.Game.Actor;
using Paris.Game.HUD;
using Paris.Game.Menu;

namespace CosmicBots
{
    /// <summary>Harmony hooks into the game for bots, menu and overlay.</summary>
    public static class GamePatches
    {
        public static void Apply(Harmony h)
        {
            P(h, AccessTools.Method(typeof(PlayerManager), "Tick", new[] { typeof(float) }), prefix: nameof(PlayerManagerTickPrefix));
            P(h, AccessTools.Method(typeof(PlayerManager), "FindAvailableID"), prefix: nameof(FindAvailableIDPrefix));
            P(h, AccessTools.Method(typeof(AchievementManagerBase), "Render", Type.EmptyTypes), postfix: nameof(RenderPostfix));
            P(h, AccessTools.Method(typeof(CharacterSelect), "Tick", new[] { typeof(float) }), postfix: nameof(CharSelectTickPostfix));
            P(h, AccessTools.Method(typeof(MainHUD), "Tick", new[] { typeof(float) }), postfix: nameof(HudTickPostfix));
            P(h, AccessTools.Method(typeof(CharacterSelectionPanel), "StateSelectCharacter", new[] { typeof(FSM.StateStep), typeof(float) }), prefix: nameof(StateSelectCharacterPrefix));

            // v0.2.5: defense outcome counters for bot players (block-ok / parry-ok / dodge-iframe / hits-taken)
            P(h, AccessTools.Method(typeof(Character), "BlockIncomingAttack"), postfix: nameof(BlockPostfix));
            P(h, AccessTools.Method(typeof(Character), "ParryIncomingAttack"), postfix: nameof(ParryPostfix));
            P(h, AccessTools.Method(typeof(Character), "DodgeIncomingAttack"), postfix: nameof(DodgePostfix));
            P(h, AccessTools.Method(typeof(Character), "InternalHurt"), postfix: nameof(HurtPostfix));
            // v0.2.11: separate postfix (logging only) so a failure here can never touch the hits-taken counter above
            try { P(h, AccessTools.Method(typeof(Character), "InternalHurt"), postfix: nameof(HurtDetailPostfix)); } catch (Exception ex) { Mod.LogOnce("patch-hurtdetail", ex); }
            // v0.2.9: "the enemy reacted to a player's hit" (anything but Unaffected) for the no-damage safety net
            P(h, AccessTools.Method(typeof(Character), "ApplyAttack", new[] { typeof(GameObject2d), typeof(int), typeof(Paris.Engine.Damage.DamageInfo), typeof(int) }), postfix: nameof(ApplyAttackPostfix));

            // Keyboard is captured by the mod menu while it is open.
            P(h, AccessTools.Method(typeof(InputActionID), "IsJustTriggered", new[] { typeof(ParisInputType) }), prefix: nameof(BlockBoolPrefix));
            P(h, AccessTools.Method(typeof(InputActionID), "IsTriggered", new[] { typeof(ParisInputType) }), prefix: nameof(BlockBoolPrefix));
            P(h, AccessTools.Method(typeof(InputDirectionActionID), "GetDirection", new[] { typeof(ParisInputType), typeof(bool), typeof(bool) }), prefix: nameof(BlockDirPrefix));
            P(h, AccessTools.Method(typeof(InputDirectionActionID), "GetDirection", new[] { typeof(ParisInputType), typeof(InputDirection), typeof(bool), typeof(bool) }), prefix: nameof(BlockDirFloatPrefix));
            P(h, AccessTools.Method(typeof(InputManagerBase), "SomeonePressedStart", new[] { typeof(bool), typeof(bool), typeof(ulong).MakeByRefType() }), prefix: nameof(BlockStartPrefix));
            P(h, AccessTools.Method(typeof(InputManager), "SomeonePressedStart", new[] { typeof(bool), typeof(bool), typeof(ulong).MakeByRefType() }), prefix: nameof(BlockStartPrefix));
        }

        public static int Installed, Failed;

        private static void P(Harmony h, System.Reflection.MethodBase m, string prefix = null, string postfix = null)
        {
            if (m == null) { Failed++; Log.Error("Hook target missing for " + (prefix ?? postfix)); return; }
            try
            {
                h.Patch(m, prefix: prefix == null ? null : new HarmonyMethod(typeof(GamePatches), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(GamePatches), postfix));
                Installed++;
                Log.Verbose("Hooked " + m.DeclaringType.FullName + "." + m.Name);
            }
            catch (Exception ex) { Failed++; Log.Exception("Hook failed: " + m.DeclaringType.FullName + "." + m.Name, ex); }
        }

        // ------------------------------------------------------------------ tick / render
        public static void PlayerManagerTickPrefix(float deltaTime)
        {
            try { Menu.Tick(deltaTime); } catch (Exception ex) { Mod.LogOnce("menu-tick", ex); }
            BotManager.Tick(deltaTime);
        }

        public static void RenderPostfix()
        {
            try { Menu.Render(); } catch (Exception ex) { Mod.LogOnce("menu-render", ex); }
        }

        public static void BlockPostfix(Character __instance) => BotController.OnDefenseEvent(__instance, 4);
        public static void ParryPostfix(Character __instance) => BotController.OnDefenseEvent(__instance, 5);
        public static void DodgePostfix(Character __instance) => BotController.OnDefenseEvent(__instance, 9);
        public static void HurtPostfix(Character __instance) => BotController.OnDefenseEvent(__instance, 0);
        public static void HurtDetailPostfix(Character __instance, Paris.Game.Damage.DamageInfoEx dmg, Paris.Game.Combat.AttackResultEx attackResult) => BotController.OnHitDetail(__instance, dmg, attackResult);
        public static void ApplyAttackPostfix(Character __instance, GameObject2d attacker, Paris.Engine.Damage.AttackResult __result)
        {
            BotController.OnContact(__instance, attacker, __result);
        }

        public static void CharSelectTickPostfix(CharacterSelect __instance) => BotManager.SeenCharSelect(__instance);
        public static void HudTickPostfix(MainHUD __instance) => BotManager.SeenHud(__instance);

        // Put a bot into the exact P-slot the user enabled (vanilla assigns the lowest free id).
        public static bool FindAvailableIDPrefix(ref byte __result)
        {
            int id = BotManager.ForcedPlayerId;
            if (id < 0) return true;
            if (PlayerManager.Singleton.GetPlayerByID(id) != null) return true;
            __result = (byte)id;
            return false;
        }

        // ------------------------------------------------------------------ character select
        private static readonly AccessTools.FieldRef<CharacterSelectionPanel, CharacterSelectMode> PanelMode =
            AccessTools.FieldRefAccess<CharacterSelectionPanel, CharacterSelectMode>("_mode");

        public static bool StateSelectCharacterPrefix(CharacterSelectionPanel __instance, FSM.StateStep stateStep)
        {
            if (stateStep != FSM.StateStep.Tick) return true;
            try
            {
                GamePlayerInfo pi = __instance.PlayerInfo;
                if (pi == null || !(pi.Controller is BotController)) return true;
                int slot = pi.PlayerID;
                if (slot < 1 || slot > 3) return true;
                if (PlayerManager.Singleton.GetPlayerByID(slot) == null || pi.IsReadyForSpawning) return true; // vanilla handles leave/ready
                bool gameMode = PanelMode(__instance) == CharacterSelectMode.Game;
                bool partner = !__instance.SelectingMainHero;
                int target = BotManager.ResolveHero(slot, partner, pi);
                if (target >= 0 && __instance.CurrentSelection != target)
                {
                    __instance.CurrentSelection = target; // bot "hovers" its configured hero
                    if (gameMode) __instance.UpdateCharacterSelection();
                }
                if (!BotManager.ShouldLockIn(__instance, slot, gameMode)) return false; // wait: skip vanilla (which would auto-confirm)
                return target >= 0; // vanilla Debug path confirms CurrentSelection
            }
            catch (Exception ex) { Mod.LogOnce("charselect", ex); return true; }
        }

        // ------------------------------------------------------------------ input capture while menu open
        private static bool Capturing(ParisInputType t) => Menu.Open && t == ParisInputType.Keyboard;

        public static bool BlockBoolPrefix(ParisInputType controllerType, ref bool __result)
        {
            if (!Capturing(controllerType)) return true;
            __result = false; return false;
        }

        public static bool BlockDirPrefix(ParisInputType controllerType, ref Vector2 __result)
        {
            if (!Capturing(controllerType)) return true;
            __result = Vector2.Zero; return false;
        }

        public static bool BlockDirFloatPrefix(ParisInputType controllerType, ref float __result)
        {
            if (!Capturing(controllerType)) return true;
            __result = 0f; return false;
        }

        public static bool BlockStartPrefix(ref ParisInputType __result, ref ulong handle)
        {
            if (!Menu.Open) return true;
            handle = 0; __result = ParisInputType.None; return false;
        }
    }
}
