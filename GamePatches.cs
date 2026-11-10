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

            // defense outcome counters for bot players (block-ok / parry-ok / dodge-iframe / hits-taken)
            P(h, AccessTools.Method(typeof(Character), "BlockIncomingAttack"), postfix: nameof(BlockPostfix));
            P(h, AccessTools.Method(typeof(Character), "ParryIncomingAttack"), postfix: nameof(ParryPostfix));
            P(h, AccessTools.Method(typeof(Character), "DodgeIncomingAttack"), postfix: nameof(DodgePostfix));
            P(h, AccessTools.Method(typeof(Character), "InternalHurt"), postfix: nameof(HurtPostfix));
            // separate postfix (logging only) so a failure here can never touch the hits-taken counter above
            try { P(h, AccessTools.Method(typeof(Character), "InternalHurt"), postfix: nameof(HurtDetailPostfix)); } catch (Exception ex) { Mod.LogOnce("patch-hurtdetail", ex); }
            // "the enemy reacted to a player's hit" (anything but Unaffected) for the no-damage safety net
            P(h, AccessTools.Method(typeof(Character), "ApplyAttack", new[] { typeof(GameObject2d), typeof(int), typeof(Paris.Engine.Damage.DamageInfo), typeof(int) }), postfix: nameof(ApplyAttackPostfix));

            // Keyboard is captured by the mod menu while it is open.
            P(h, AccessTools.Method(typeof(InputActionID), "IsJustTriggered", new[] { typeof(ParisInputType) }), prefix: nameof(BlockBoolPrefix));
            P(h, AccessTools.Method(typeof(InputActionID), "IsTriggered", new[] { typeof(ParisInputType) }), prefix: nameof(BlockBoolPrefix));
            P(h, AccessTools.Method(typeof(InputDirectionActionID), "GetDirection", new[] { typeof(ParisInputType), typeof(bool), typeof(bool) }), prefix: nameof(BlockDirPrefix));
            P(h, AccessTools.Method(typeof(InputDirectionActionID), "GetDirection", new[] { typeof(ParisInputType), typeof(InputDirection), typeof(bool), typeof(bool) }), prefix: nameof(BlockDirFloatPrefix));
            P(h, AccessTools.Method(typeof(InputManagerBase), "SomeonePressedStart", new[] { typeof(bool), typeof(bool), typeof(ulong).MakeByRefType() }), prefix: nameof(BlockStartPrefix));
            P(h, AccessTools.Method(typeof(InputManager), "SomeonePressedStart", new[] { typeof(bool), typeof(bool), typeof(ulong).MakeByRefType() }), prefix: nameof(BlockStartPrefix));

            // Siege of Castle Doom mode. Every hook below returns straight away outside that mode.
            CastleDoom.Apply(h);
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
                    __instance.CurrentSelection = target; // bot "hovers" its configured hero (the game resets the hovered color to its default here)
                    if (gameMode) __instance.UpdateCharacterSelection();
                }
                // bot color. Same call the game makes for LB/RB (ChangePalette -> SetPaletteIndex), on the list of colors
                // the game itself offers this player for the hovered hero, so locked colors are never used.
                if (target >= 0 && target == __instance.CurrentSelection) ApplyColor(__instance, slot, partner, pi);
                if (!BotManager.ShouldLockIn(__instance, slot, gameMode)) return false; // wait: skip vanilla (which would auto-confirm)
                return target >= 0; // vanilla Debug path confirms CurrentSelection
            }
            catch (Exception ex) { Mod.LogOnce("charselect", ex); return true; }
        }

        private static readonly AccessTools.FieldRef<CharacterSelectionPanel, System.Collections.Generic.List<int>> PanelColors =
            AccessTools.FieldRefAccess<CharacterSelectionPanel, System.Collections.Generic.List<int>>("_availablePalettes");

        /// <summary>Per slot / hero (0 main, 1 partner): how many colors the game offers, which one (1-based) is shown, and for which hero. 0 / -1 = unknown.</summary>
        public static readonly int[,] ColorCount = new int[4, 2], ColorShown = new int[4, 2];
        public static readonly int[,] ColorHero = { { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 } };
        private static readonly int[,] _cntHero = { { -1, -1 }, { -1, -1 }, { -1, -1 }, { -1, -1 } }, _cntN = new int[4, 2], _cntTick = new int[4, 2];

        /// <summary>Real number of colors the game offers this bot for its configured hero (0 = no hero set / unknown).
        /// Uses the select screen's own list when the bot is on that hero, otherwise the same save data the select screen reads.</summary>
        public static int HeroColorCount(int slot, bool partner)
        {
            int h = partner ? 1 : 0;
            var cfg = Config.Slots[slot];
            string name = partner ? cfg.PartnerHero : cfg.MainHero;
            if (string.IsNullOrEmpty(name)) return 0;
            int hero = BotManager.IndexOf(name);
            if (hero < 0) return 0;
            if (ColorHero[slot, h] == hero && ColorCount[slot, h] > 0) return ColorCount[slot, h];
            int now = Environment.TickCount;
            if (_cntHero[slot, h] == hero && unchecked(now - _cntTick[slot, h]) < 500) return _cntN[slot, h];
            int n = 0;
            try
            {
                var ci = Paris.Game.System.CharacterManager.Singleton.Characters[hero];
                SaveGame sv = BotManager.GetPlayer(slot)?.Save as SaveGame ?? BotManager.HostSave;
                if (sv != null)
                    foreach (string id in sv.GetUnlockedPalettes(hero)) if (ci.Palettes.IndexOf(id) >= 0) n++;
            }
            catch { n = 0; }
            _cntHero[slot, h] = hero; _cntN[slot, h] = n; _cntTick[slot, h] = now;
            return n;
        }

        /// <summary>Configured color clamped to the hero's real color count (0 = Auto). Out-of-range values are clamped and saved.</summary>
        public static int EffectiveColor(int slot, bool partner, out int count)
        {
            var cfg = Config.Slots[slot];
            string name = partner ? cfg.PartnerHero : cfg.MainHero;
            int c = partner ? cfg.PartnerColor : cfg.Color;
            count = HeroColorCount(slot, partner);
            int fixedC = c;
            if (string.IsNullOrEmpty(name)) fixedC = 0;                 // Auto hero: color is Auto too
            else if (count > 0) fixedC = Math.Max(0, Math.Min(count, c));
            if (fixedC != c)
            {
                if (partner) cfg.PartnerColor = fixedC; else cfg.Color = fixedC;
                Config.Save();
            }
            return fixedC;
        }

        private static void ApplyColor(CharacterSelectionPanel panel, int slot, bool partner, GamePlayerInfo pi)
        {
            int h = partner ? 1 : 0;
            var list = PanelColors(panel);
            if (list == null || list.Count == 0) { ColorCount[slot, h] = 0; return; }
            ColorHero[slot, h] = panel.CurrentSelection;
            ColorCount[slot, h] = list.Count;
            int want = EffectiveColor(slot, partner, out _);
            // Auto = the game's default color for a freshly hovered hero (palette 1), i.e. exactly what bots did before 0.5.0.
            int pos = want <= 0 ? panel.GetPaletteIndex(1) : Math.Min(want, list.Count) - 1;
            if (pos < 0) pos = 0;
            int cur = panel.GetPaletteIndex(pi.Heroes[h].PaletteIndex);
            if (cur != pos) panel.SetPaletteIndex(pos);
            ColorShown[slot, h] = panel.GetPaletteIndex(pi.Heroes[h].PaletteIndex) + 1;
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
