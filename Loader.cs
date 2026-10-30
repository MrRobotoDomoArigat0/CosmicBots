using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace CosmicBots
{
    /// <summary>
    /// Entry point. Game files are never modified: we start Game.exe's own Main from a fresh AppDomain whose
    /// base directory is the game folder, after installing our Harmony patches inside that AppDomain.
    /// Because the game is started with AppDomain.ExecuteAssembly, Assembly.GetEntryAssembly() is Game.exe inside
    /// that domain, so the engine's reflection scan (ContextManager.GetAssemblyList) works unchanged.
    /// Fallback (-cbinproc): run Main in this domain and patch GetEntryAssembly instead.
    /// </summary>
    public static class Loader
    {
        public const string GameExe = "Game.exe";
        public static string VersionLabel
        {
            get
            {
                try
                {
                    var a = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(typeof(Loader).Assembly, typeof(AssemblyInformationalVersionAttribute));
                    return a != null ? a.InformationalVersion : typeof(Loader).Assembly.GetName().Version.ToString();
                }
                catch { return "?"; }
            }
        }

        public static int Main(string[] args)
        {
            Log.Init(truncate: true);
            Log.Info("CosmicBots " + VersionLabel + " (unofficial fan mod, offline use only) starting");
            string gameDir = AppDomain.CurrentDomain.BaseDirectory;
            string gamePath = Path.Combine(gameDir, GameExe);
            if (!File.Exists(gamePath))
            {
                Log.Error("Game.exe not found next to CosmicBots.exe (" + gameDir + "). Unzip CosmicBots into the game folder.");
                return 2;
            }
            try { Directory.SetCurrentDirectory(gameDir); } catch { }
            SteamFix.PrepareEnvironment(gameDir, args);

            bool inProc = Array.Exists(args, a => string.Equals(a, "-cbinproc", StringComparison.OrdinalIgnoreCase));
            string[] gameArgs = Array.FindAll(args, a => !string.Equals(a, "-cbinproc", StringComparison.OrdinalIgnoreCase) && !string.Equals(a, "-nosteamid", StringComparison.OrdinalIgnoreCase));
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Exception("Unhandled exception (loader domain), terminating=" + e.IsTerminating, e.ExceptionObject as Exception);
            try
            {
                if (inProc)
                {
                    Log.Info("Mode: in-process (-cbinproc)");
                    Bootstrap.Init(gameDir, inProcess: true);
                    try { return Bootstrap.RunInProcess(gamePath, gameArgs); }
                    finally { Log.FlushForExit(); }
                }

                var setup = new AppDomainSetup
                {
                    ApplicationBase = gameDir,
                    ApplicationName = "Game",
                };
                string gameConfig = gamePath + ".config";
                setup.ConfigurationFile = File.Exists(gameConfig) ? gameConfig : Path.Combine(gameDir, "CosmicBots.exe.config");
                Log.Info("Mode: AppDomain. Config file: " + setup.ConfigurationFile);
                AppDomain domain = AppDomain.CreateDomain("CosmicBots.Game", AppDomain.CurrentDomain.Evidence, setup);
                domain.SetData("CosmicBots.GameDir", gameDir);
                domain.DoCallBack(Bootstrap.DomainCallback);
                Log.Info("Patches installed; starting Game.exe Main");
                try { return domain.ExecuteAssembly(gamePath, gameArgs); }
                finally { try { domain.DoCallBack(Log.FlushForExit); } catch { } }
            }
            catch (Exception ex)
            {
                Log.Exception("Loader failed", ex);
                return 1;
            }
        }
    }

    public static class Bootstrap
    {
        private static string _gameDir;

        /// <summary>Runs inside the new AppDomain before Game.exe's Main.</summary>
        public static void DomainCallback()
        {
            string dir = (string)AppDomain.CurrentDomain.GetData("CosmicBots.GameDir") ?? AppDomain.CurrentDomain.BaseDirectory;
            Init(dir, inProcess: false);
        }

        public static void Init(string gameDir, bool inProcess)
        {
            _gameDir = gameDir;
            Log.StartAsync();   // from here on, log lines are queued and written by a background thread
            try { Directory.SetCurrentDirectory(gameDir); } catch { }
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            // Our own crash log. Registered before the game's handlers so it always runs first.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log.Exception("Unhandled exception in game, terminating=" + e.IsTerminating, e.ExceptionObject as Exception);
            Assembly game = Assembly.Load(AssemblyName.GetAssemblyName(Path.Combine(gameDir, Loader.GameExe)));
            Log.Info("Game: " + game.GetName().Name + " build " + game.GetName().Version);
            ApplyPatches(game, inProcess);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyPatches(Assembly game, bool inProcess)
        {
            Mod.Apply(game, inProcess);
        }

        public static int RunInProcess(string gamePath, string[] args)
        {
            Assembly game = Assembly.Load(AssemblyName.GetAssemblyName(gamePath));
            MethodInfo main = game.EntryPoint;
            object r = main.Invoke(null, main.GetParameters().Length == 0 ? null : new object[] { args });
            return r is int i ? i : 0;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            try
            {
                string name = new AssemblyName(e.Name).Name;
                foreach (string ext in new[] { ".dll", ".exe" })
                {
                    string p = Path.Combine(_gameDir ?? AppDomain.CurrentDomain.BaseDirectory, name + ext);
                    if (File.Exists(p)) return Assembly.LoadFrom(p);
                }
            }
            catch { }
            return null;
        }
    }
}
