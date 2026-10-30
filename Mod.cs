using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace CosmicBots
{
    public static class Mod
    {
        private static readonly HashSet<string> _logged = new HashSet<string>();
        public static Assembly GameAssembly;

        public static void Apply(Assembly game, bool inProcess)
        {
            GameAssembly = game;
            Config.Load();
            var h = new Harmony("com.cosmicbots.mod");

            if (inProcess)
            {
                try
                {
                    h.Patch(AccessTools.Method(typeof(Assembly), nameof(Assembly.GetEntryAssembly)), prefix: new HarmonyMethod(typeof(Mod), nameof(EntryPrefix)));
                    Log.Info("Assembly.GetEntryAssembly patched to return Game.exe (in-process mode)");
                }
                catch (Exception ex) { Log.Exception("GetEntryAssembly patch failed (in-process mode)", ex); }
            }

            // Crash reporting / telemetry first. If this throws, we abort the launch rather than run modded with reporting active.
            Telemetry.Apply(h, game);
            Log.Info("Telemetry neutralized: " + Telemetry.Disabled.Count + " items");
            try { SteamFix.Apply(h); } catch (Exception ex) { Log.Exception("Steam startup fix failed", ex); }
            try { GameLog.Apply(h); } catch (Exception ex) { Log.Exception("Game log hook failed (game log unchanged)", ex); }

            try
            {
                Menu.InitKey();
                GamePatches.Apply(h);
                MenuFollow.Apply(h);
                Log.Info("Hooks installed: " + GamePatches.Installed + " bot/menu hooks ok, " + GamePatches.Failed + " failed");
            }
            catch (Exception ex) { Log.Exception("Bot/menu hooks failed (game will run without bots)", ex); }
        }

        public static bool EntryPrefix(ref Assembly __result)
        {
            if (GameAssembly == null) return true;
            __result = GameAssembly;
            return false;
        }

        public static void LogOnce(string key, Exception ex)
        {
            lock (_logged)
            {
                if (!_logged.Add(key + ":" + ex.GetType().Name + ":" + ex.Message)) return;
            }
            Log.Exception("Mod error (" + key + "), shown once", ex);
        }
    }
}
