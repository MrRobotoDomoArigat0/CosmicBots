using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Paris.Engine;
using Paris.Engine.Input;
using Paris.Engine.Menu;

namespace CosmicBots
{
    /// <summary>
    /// makes the mod work when CosmicBots.exe is started directly (not through Steam's Play button).
    /// Root cause of the 13466 "NullReferenceException in MainMenu.Tick" crash: when Game.exe is not started by Steam it has no
    /// Steam App ID, so SteamAPI.Init() fails. InputManager (Steam Input) then never becomes Ready, Paris.InitControllers never
    /// adds the first local player, MenuContext.Controller stays null and MainMenu.Tick dereferences it on the first menu frame.
    /// Layers (all local, nothing is sent anywhere):
    ///  1. Before the game starts, declare the Steam App ID through the SteamAppId / SteamGameId environment variables
    ///     (the same thing steam_appid.txt does) unless Steam/the user already provided one. No game file is touched.
    ///  2. If Steam still cannot be initialised, report InputManager.Ready = true so the game falls back to its plain FNA
    ///     keyboard/gamepad path (every Steam Input call in InputManager is separately guarded by its _initialized flag).
    ///  3. MainMenu.Tick is skipped for any frame in which the menu has no controller yet (instead of throwing).
    ///  4. input fallback (ported from ShreddedBots): if Steam Input DID initialise but no first local player has been
    ///     added after Config.InputFallbackSeconds on the title screen (Steam Input never reports a controller or button press,
    ///     e.g. the controller isn't recognised or Steam Input is broken/disabled for this game), switch InputManager to the same
    ///     plain FNA keyboard/gamepad path as layer 2. Input only: SteamAPI, ownership, DLC, achievements etc. are not touched.
    /// </summary>
    public static class SteamFix
    {
        public const string AppId = "2753970"; // MARVEL Cosmic Invasion (same id SteamHelper.APP_ID uses)

        // ---- layer 1: host process, before the game domain exists ----------------------------------------
        public static void PrepareEnvironment(string gameDir, string[] args)
        {
            try
            {
                if (Array.Exists(args, a => string.Equals(a, "-nosteamid", StringComparison.OrdinalIgnoreCase)))
                { Log.Info("Steam: App ID env override disabled by -nosteamid"); return; }
                string existing = Environment.GetEnvironmentVariable("SteamAppId");
                if (!string.IsNullOrEmpty(existing)) { Log.Info("Steam: SteamAppId already set by the launcher (" + existing + "); leaving as is"); return; }
                if (File.Exists(Path.Combine(gameDir, "steam_appid.txt"))) { Log.Info("Steam: steam_appid.txt present; leaving App ID to Steam"); return; }
                Environment.SetEnvironmentVariable("SteamAppId", AppId);
                Environment.SetEnvironmentVariable("SteamGameId", AppId);
                // Native steam_api may read the C runtime's copy of the environment instead of the Win32 one.
                foreach (string crt in new[] { "ucrtbase.dll", "msvcrt.dll" })
                {
                    try { _putenv_s_u(crt, "SteamAppId", AppId); _putenv_s_u(crt, "SteamGameId", AppId); } catch { }
                }
                Log.Info("Steam: no App ID found (not started by Steam and no steam_appid.txt); declared SteamAppId=" + AppId + " for this process only");
            }
            catch (Exception ex) { Log.Warn("Steam env setup failed: " + ex.Message); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern IntPtr LoadLibraryA(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr h, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int PutEnvS(string name, string value);
        private static void _putenv_s_u(string dll, string name, string value)
        {
            IntPtr h = LoadLibraryA(dll); if (h == IntPtr.Zero) return;
            IntPtr p = GetProcAddress(h, "_putenv_s"); if (p == IntPtr.Zero) return;
            ((PutEnvS)Marshal.GetDelegateForFunctionPointer(p, typeof(PutEnvS)))(name, value);
        }

        // ---- layers 2 + 3 + diagnostics: Harmony, inside the game domain ---------------------------------
        private static AccessTools.FieldRef<InputManager, bool> _inputInitialized;
        private static bool _loggedSteam, _lastSteam, _loggedReady, _loggedSkip;
        // layer 4 (input fallback, ported from ShreddedBots)
        private static bool _forcePlainInput, _loggedForce;
        private static System.Diagnostics.Stopwatch _noControllerSince;
        private static InputManager _lastInput;

        public static void Apply(Harmony h)
        {
            try { _inputInitialized = AccessTools.FieldRefAccess<InputManager, bool>("_initialized"); }
            catch (Exception ex) { Log.Warn("SteamFix: InputManager._initialized not found (" + ex.Message + "); Ready fallback disabled"); }

            MethodInfo ready = AccessTools.PropertyGetter(typeof(InputManager), "Ready");
            if (ready != null && _inputInitialized != null)
            {
                h.Patch(ready, postfix: new HarmonyMethod(typeof(SteamFix), nameof(ReadyPostfix)));
                Log.Info("SteamFix: InputManager.Ready fallback installed (plain keyboard/gamepad input if Steam Input cannot start)");
            }
            else Log.Warn("SteamFix: InputManager.Ready getter not found");

            MethodInfo menuTick = AccessTools.Method("Paris.Game.Menu.MainMenu:Tick", new[] { typeof(float) });
            if (menuTick != null)
            {
                h.Patch(menuTick, prefix: new HarmonyMethod(typeof(SteamFix), nameof(MainMenuTickPrefix)));
                Log.Info("SteamFix: MainMenu.Tick null-controller guard installed");
            }
            else Log.Warn("SteamFix: MainMenu.Tick not found");

            MethodInfo steamInit = AccessTools.Method(typeof(SteamHelper), "Init", new[] { typeof(float) });
            if (steamInit != null) h.Patch(steamInit, postfix: new HarmonyMethod(typeof(SteamFix), nameof(SteamInitPostfix)));
        }

        public static void SteamInitPostfix()
        {
            try
            {
                bool ok = SteamHelper.Initialized;
                if (_loggedSteam && ok == _lastSteam) return;
                _loggedSteam = true; _lastSteam = ok;
                if (ok) Log.Info("Steam: SteamAPI initialised (Steam Input and Steam features available)");
                else Log.Warn("Steam: SteamAPI.Init failed (is the Steam client running and is this account the owner of the game?). Using plain keyboard/gamepad input; the game retries Steam every 15 s.");
            }
            catch { }
        }

        // InputManager.Ready is false forever if Steam Input never initialised; every Steam call in InputManager is guarded by _initialized.
        public static void ReadyPostfix(InputManager __instance, ref bool __result)
        {
            try
            {
                _lastInput = __instance;
                if (_forcePlainInput)
                {
                    // Layer 4: keep Steam Input switched off for InputManager (the game may re-initialise it on its periodic Steam retry).
                    if (_inputInitialized(__instance)) _inputInitialized(__instance) = false;
                    __result = true;
                    return;
                }
                if (__result) return;
                if (_inputInitialized(__instance)) return;      // Steam Input is up but simply not ready yet: keep the game's own answer
                __result = true;
                if (!_loggedReady) { _loggedReady = true; Log.Warn("Steam: Steam Input not available; reporting input as ready so the game uses plain keyboard/gamepad input."); }
            }
            catch { }
        }

        public static bool MainMenuTickPrefix(MenuContext __instance)
        {
            try
            {
                if (__instance.Controller != null) { _noControllerSince = null; return true; }
                __instance.ResetController();                    // picks Players[0] if one exists by now
                if (__instance.Controller != null) { _noControllerSince = null; return true; }
                MaybeForcePlainInput();
                if (!_loggedSkip) { _loggedSkip = true; Log.Warn("MainMenu.Tick skipped: no controller yet (waiting for the first local player to be added)."); }
                return false;
            }
            catch { return true; }
        }

        /// <summary>Layer 4: called on every title-screen frame that still has no controller.</summary>
        private static void MaybeForcePlainInput()
        {
            if (_forcePlainInput || _inputInitialized == null) return;
            float limit = Config.InputFallbackSeconds;
            if (limit <= 0f) return;                                     // disabled in the ini
            if (_noControllerSince == null) { _noControllerSince = System.Diagnostics.Stopwatch.StartNew(); return; }
            if (_noControllerSince.Elapsed.TotalSeconds < limit) return;
            _forcePlainInput = true;
            try { if (_lastInput != null) _inputInitialized(_lastInput) = false; } catch { }
            if (!_loggedForce)
            {
                _loggedForce = true;
                Log.Warn("Input fallback: no player joined through Steam Input after " + limit.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                    + " s; switching to plain keyboard/gamepad input. Press a button to join. (Set InputFallbackSeconds=0 in CosmicBots.ini to turn this off.)");
            }
        }
    }
}
