using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace CosmicBots
{
    /// <summary>
    /// Kills every outbound crash-report / telemetry path found in Game.exe + ParisEngine.dll when running modded.
    /// Layered on purpose: if one layer misses, the next one still blocks the send.
    /// </summary>
    public static class Telemetry
    {
        public static readonly List<string> Disabled = new List<string>();
        private static int _mainNBugAddRemoved, _mainNBugHandlerReplaced;

        public static void Apply(Harmony h, Assembly game)
        {
            // Layer 1: rewrite Paris.Program.Main so NBug is never wired up.
            MethodInfo main = AccessTools.Method("Paris.Program:Main");
            if (main != null)
            {
                h.Patch(main, transpiler: new HarmonyMethod(typeof(Telemetry), nameof(MainTranspiler)));
                Note($"Program.Main transpiled: NBugDiscordProtocol registration removed x{_mainNBugAddRemoved}, NBug.Handler.UnhandledException replaced by CosmicBots logger x{_mainNBugHandlerReplaced}");
                if (_mainNBugAddRemoved == 0 || _mainNBugHandlerReplaced == 0)
                    Log.Warn("Program.Main IL did not match expectations; relying on the other NBug layers.");
            }
            else Log.Warn("Paris.Program.Main not found");

            // Layer 2: the game's own Discord sender chain.
            Skip(h, "Paris.NBugDiscordProtocol:Send", nameof(PrefixFalse), "NBugDiscordProtocol.Send (Discord crash webhook protocol) -> no-op");
            Skip(h, "Paris.DiscordWebHook:SendFile", nameof(PrefixEmptyString), "DiscordWebHook.SendFile (the game's Discord crash webhook) -> no-op");
            Skip(h, "Paris.FormUpload:MultipartFormDataPost", nameof(PrefixHttpResponse), "FormUpload.MultipartFormDataPost (HTTP multipart upload) -> no-op");
            Skip(h, "Paris.FormUpload:PostForm", nameof(PrefixHttpResponse), "FormUpload.PostForm (HTTP POST) -> no-op");

            // Layer 3: NBug itself (lives in NBug.dll in the game folder; patched by reflection).
            PatchNBug(h);

            // Layer 4: other HTTP senders in ParisEngine (Tribute web services + Steam Web API helpers).
            Type ws = AccessTools.TypeByName("Paris.Engine.WebServices");
            if (ws != null)
            {
                foreach (MethodInfo m in ws.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.IsGenericMethodDefinition) continue;
                    string n = m.Name;
                    if (!(n == "PostRequest" || n == "PostFileRequest" || n == "GetRequest" || n == "GetBinaryRequest" || n == "GetResponseForWebRequest")) continue;
                    string prefix = m.ReturnType == typeof(string) ? nameof(PrefixEmptyString)
                        : m.ReturnType == typeof(byte[]) ? nameof(PrefixBytes)
                        : m.ReturnType == typeof(Paris.Engine.WebServiceResponse) ? nameof(PrefixWsResponse) : null;
                    if (prefix == null) { Log.Warn("WebServices." + n + " not patched (unexpected return type " + m.ReturnType + ")"); continue; }
                    h.Patch(m, prefix: new HarmonyMethod(typeof(Telemetry), prefix));
                    Note("WebServices." + n + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ") (services.tributegames.com / api.steampowered.com) -> no-op");
                }
            }

            // Layer 5: GameAnalytics (already stubbed out by the developer in this build; patched anyway).
            Skip(h, "Paris.Engine.Stats.GameAnalytics:Init", nameof(PrefixSkip), "GameAnalytics.Init -> no-op (thread never started)");
            Skip(h, "Paris.Engine.Stats.GameAnalytics:PostUserInfo", nameof(PrefixSkip), "GameAnalytics.PostUserInfo -> no-op");
            Skip(h, "Paris.Engine.Stats.GameAnalytics:PostInfo", nameof(PrefixSkip), "GameAnalytics.PostInfo -> no-op");
            Skip(h, "Paris.Engine.Stats.GameAnalytics:PostToGA", nameof(PrefixTrue), "GameAnalytics.PostToGA -> no-op");

            // Layer 6: NLog — strip any network-type log target that NLog.Retail.config might define.
            MethodInfo nlogInit = AccessTools.Method("Paris.ParisXnaLogger:Init");
            if (nlogInit != null)
            {
                h.Patch(nlogInit, postfix: new HarmonyMethod(typeof(Telemetry), nameof(NLogPostfix)));
                Note("NLog: network/mail/webservice/database targets removed from NLog.Retail.config at load (local file logging kept)");
            }

            // Layer 7 (optional, default on): Epic Online Services.
            if (Config.BlockEpicOnlineServices)
            {
                Skip(h, "Paris.Engine.EpicHelper:Init", nameof(PrefixSkip), "EpicHelper.Init -> skipped (no EOS platform, no EOS login/stats ingest/achievement sync/player reports/online lobbies)");
                foreach (string n in new[] { "Login", "Reauth", "DoLinkAccount", "InternalLogin" })
                    Skip(h, "Paris.Engine.EpicHelper:" + n, nameof(PrefixSkip), null);
                // With EOS off there is no EOS platform object; the game's lobby lock/unlock (called when a Castle Doom run starts,
                // locks or ends) would dereference it. Offline there is no lobby either, so only the game's own lock flag is kept.
                MethodInfo lockM = AccessTools.Method("Paris.Engine.Networking.NetworkManager:ChangeLobbyLocked", new[] { typeof(bool) });
                if (lockM != null)
                {
                    h.Patch(lockM, prefix: new HarmonyMethod(typeof(Telemetry), nameof(LobbyLockPrefix)));
                    Note("NetworkManager.ChangeLobbyLocked -> lock flag only while EOS is off (no lobby offline)");
                }
            }
            else Note("Epic Online Services left ON (BlockEpicOnlineServices=false in CosmicBots.ini)");
        }

        private static readonly System.Reflection.PropertyInfo LobbyLockedProp =
            AccessTools.Property(typeof(Paris.Engine.Networking.NetworkManagerBase), "LobbyLocked");

        public static bool LobbyLockPrefix(object __instance, bool isLocked)
        {
            try
            {
                if (Paris.Engine.EpicHelper.Interface != null) return true;   // EOS running: game's own code
                LobbyLockedProp?.SetValue(__instance, isLocked, null);
                return false;
            }
            catch { return true; }
        }

        private static void PatchNBug(Harmony h)
        {
            Assembly nbug = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "NBug");
            if (nbug == null)
            {
                try { nbug = Assembly.Load("NBug"); } catch (Exception ex) { Log.Warn("NBug.dll could not be loaded for patching: " + ex.Message); }
            }
            if (nbug == null) return;
            Type[] types;
            try { types = nbug.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

            Type dispatcher = types.FirstOrDefault(t => t.FullName == "NBug.Core.Submission.Dispatcher");
            if (dispatcher != null)
            {
                foreach (ConstructorInfo c in dispatcher.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    h.Patch(c, prefix: new HarmonyMethod(typeof(Telemetry), nameof(PrefixSkip)));
                foreach (MethodInfo m in dispatcher.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == "Dispatch" && m.ReturnType == typeof(void)) h.Patch(m, prefix: new HarmonyMethod(typeof(Telemetry), nameof(PrefixSkip)));
                Note("NBug Dispatcher (queued-report sender, started by NBug.Handler's static constructor) -> constructors and Dispatch() no-op");
            }
            else Log.Warn("NBug Dispatcher type not found");

            Type iproto = types.FirstOrDefault(t => t.FullName == "NBug.Core.Submission.IProtocol");
            if (iproto != null)
            {
                var impls = new List<Type>(types.Where(t => t.IsClass && !t.IsAbstract && iproto.IsAssignableFrom(t)));
                Type discord = AccessTools.TypeByName("Paris.NBugDiscordProtocol");
                int n = 0;
                foreach (Type t in impls)
                {
                    if (t == discord) continue;
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.Name == "Send" && m.ReturnType == typeof(bool) && !m.IsAbstract)
                        {
                            try { h.Patch(m, prefix: new HarmonyMethod(typeof(Telemetry), nameof(PrefixFalse))); n++; } catch (Exception ex) { Log.Warn("NBug protocol patch failed " + t.FullName + ": " + ex.Message); }
                        }
                    }
                }
                Note("NBug built-in submit protocols (mail/HTTP/FTP/Redmine/etc.) Send() -> no-op: " + n + " methods");
            }

            Type settings = types.FirstOrDefault(t => t.FullName == "NBug.Settings");
            MethodInfo getDest = settings?.GetProperty("Destinations", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(true);
            if (getDest != null)
            {
                h.Patch(getDest, postfix: new HarmonyMethod(typeof(Telemetry), nameof(DestinationsPostfix)));
                Note("NBug.Settings.Destinations -> always returned empty (covers destinations configured in NBug.config/app.config)");
            }

            Type handler = types.FirstOrDefault(t => t.FullName == "NBug.Handler");
            if (handler != null)
            {
                foreach (PropertyInfo p in handler.GetProperties(BindingFlags.Static | BindingFlags.Public))
                {
                    MethodInfo g = p.GetGetMethod();
                    if (g != null && g.ReturnType == typeof(UnhandledExceptionEventHandler))
                    {
                        h.Patch(g, prefix: new HarmonyMethod(typeof(Telemetry), nameof(HandlerGetterPrefix)));
                        Note("NBug.Handler." + p.Name + " getter -> returns CosmicBots logger instead of NBug's reporter");
                    }
                }
            }
            Type exceptions = types.FirstOrDefault(t => t.FullName == "NBug.Exceptions");
            MethodInfo report = exceptions?.GetMethod("Report", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(Exception) }, null);
            if (report != null)
            {
                h.Patch(report, prefix: new HarmonyMethod(typeof(Telemetry), nameof(ManualReportPrefix)));
                Note("NBug.Exceptions.Report(Exception) (manual report API; not called by the game, patched anyway) -> logged to CosmicBots.log only");
            }
        }

        private static void Skip(Harmony h, string target, string prefix, string note)
        {
            MethodBase m = AccessTools.Method(target);
            if (m == null) { Log.Warn("Patch target not found: " + target); return; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(Telemetry), prefix));
            if (note != null) Note(note);
        }

        private static void Note(string s) { Disabled.Add(s); Log.Info("[telemetry] " + s); }

        // ---- Program.Main transpiler -------------------------------------------------------------
        public static IEnumerable<CodeInstruction> MainTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            MethodInfo safe = AccessTools.Method(typeof(Telemetry), nameof(GetSafeHandler));
            bool sawDiscordCtor = false;
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ci = list[i];
                if (ci.opcode == OpCodes.Newobj && ci.operand is ConstructorInfo ctor && ctor.DeclaringType?.FullName == "Paris.NBugDiscordProtocol")
                {
                    sawDiscordCtor = true;
                    continue;
                }
                if (sawDiscordCtor && (ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo add && add.Name == "Add")
                {
                    // stack: [collection, protocol] -> pop both, nothing registered
                    var pop1 = new CodeInstruction(OpCodes.Pop) { labels = ci.labels, blocks = ci.blocks };
                    list[i] = pop1;
                    list.Insert(i + 1, new CodeInstruction(OpCodes.Pop));
                    if (add.ReturnType != typeof(void)) list.Insert(i + 2, new CodeInstruction(OpCodes.Ldc_I4_0)); // keep stack shape if Add returned a value (then popped by original code)
                    sawDiscordCtor = false;
                    _mainNBugAddRemoved++;
                    continue;
                }
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo mi && mi.DeclaringType?.FullName == "NBug.Handler" && mi.ReturnType == typeof(UnhandledExceptionEventHandler))
                {
                    list[i] = new CodeInstruction(OpCodes.Call, safe) { labels = ci.labels, blocks = ci.blocks };
                    _mainNBugHandlerReplaced++;
                    continue;
                }
                if (ci.opcode == OpCodes.Ldsfld && ci.operand is FieldInfo fi && fi.DeclaringType?.FullName == "NBug.Handler" && fi.FieldType == typeof(UnhandledExceptionEventHandler))
                {
                    list[i] = new CodeInstruction(OpCodes.Call, safe) { labels = ci.labels, blocks = ci.blocks };
                    _mainNBugHandlerReplaced++;
                }
            }
            return list;
        }

        public static UnhandledExceptionEventHandler GetSafeHandler() => SafeUnhandled;

        private static void SafeUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            // CosmicBots' own handler (registered in Bootstrap) already wrote the exception to CosmicBots.log.
            Log.Info("NBug handler slot reached; crash report NOT created/sent (CosmicBots).");
        }

        // ---- generic prefixes ----------------------------------------------------------------------
        public static bool PrefixSkip() => false;
        public static bool PrefixFalse(ref bool __result) { __result = false; Log.Info("Blocked an outbound crash-report send."); return false; }
        public static bool PrefixTrue(ref bool __result) { __result = true; return false; }
        public static bool PrefixHttpResponse(ref System.Net.HttpWebResponse __result, MethodBase __originalMethod) { __result = null; Blocked(__originalMethod); return false; }
        public static bool PrefixBytes(ref byte[] __result, MethodBase __originalMethod) { __result = null; Blocked(__originalMethod); return false; }
        public static bool PrefixWsResponse(ref Paris.Engine.WebServiceResponse __result, MethodBase __originalMethod) { __result = default(Paris.Engine.WebServiceResponse); Blocked(__originalMethod); return false; }
        private static void Blocked(MethodBase m) => Log.Info("Blocked outbound HTTP: " + m?.DeclaringType?.Name + "." + m?.Name);
        public static bool PrefixEmptyString(ref string __result, MethodBase __originalMethod) { __result = ""; Blocked(__originalMethod); return false; }
        public static bool HandlerGetterPrefix(ref UnhandledExceptionEventHandler __result) { __result = SafeUnhandled; return false; }
        public static bool ManualReportPrefix(Exception exception) { Log.Exception("NBug manual report requested (not created, not sent)", exception); return false; }

        public static void DestinationsPostfix(object __result)
        {
            try
            {
                if (__result is System.Collections.IList l) { if (l.Count > 0) l.Clear(); return; }
                MethodInfo clear = __result?.GetType().GetMethod("Clear", Type.EmptyTypes);
                PropertyInfo count = __result?.GetType().GetProperty("Count");
                if (clear != null && count != null && (int)count.GetValue(__result, null) > 0) clear.Invoke(__result, null);
            }
            catch { }
        }

        public static void NLogPostfix()
        {
            try
            {
                var cfg = NLog.LogManager.Configuration;
                if (cfg == null) return;
                var remove = new List<string>();
                foreach (NLog.Targets.Target t in cfg.AllTargets)
                {
                    string n = t.GetType().Name;
                    if (n.IndexOf("Network", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("WebService", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Mail", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Database", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Chainsaw", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("NLogViewer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Syslog", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Http", StringComparison.OrdinalIgnoreCase) >= 0)
                        remove.Add(t.Name);
                }
                foreach (string name in remove) { cfg.RemoveTarget(name); Log.Info("[telemetry] removed NLog target " + name); }
                if (remove.Count > 0) NLog.LogManager.Configuration = cfg;
            }
            catch (Exception ex) { Log.Warn("NLog target scan failed: " + ex.Message); }
        }
    }
}
