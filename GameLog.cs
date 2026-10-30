using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace CosmicBots
{
    /// <summary>
    /// Performance helper:
    ///  wraps the game's own plain NLog file targets (its local log.txt) in NLog's AsyncTargetWrapper so the game thread only enqueues (no game files are modified; done in memory after
    ///    ParisXnaLogger.Init, after the telemetry target scan).
    /// </summary>
    public static class GameLog
    {
        public static void Apply(Harmony h)
        {
            MethodInfo init = AccessTools.Method("Paris.ParisXnaLogger:Init");
            if (init == null) { Log.Warn("[gamelog] ParisXnaLogger.Init not found; game log left as is"); return; }
            h.Patch(init, postfix: new HarmonyMethod(typeof(GameLog), nameof(NLogInitPostfix)) { priority = Priority.Last });
        }

        public static void NLogInitPostfix()
        {
            try
            {
                var cfg = LogManager.Configuration;
                if (cfg == null) { Log.Info("[gamelog] game NLog has no configuration"); return; }
                foreach (Target t in cfg.AllTargets)
                {
                    string info = t.GetType().Name + " '" + t.Name + "'";
                    if (t is FileTarget ft)
                    {
                        string file = "?";
                        try { file = ft.FileName?.Render(LogEventInfo.CreateNullEvent()); } catch { }
                        info += $": file={file} keepFileOpen={ft.KeepFileOpen} autoFlush={ft.AutoFlush} concurrentWrites={ft.ConcurrentWrites} " +
                                $"bufferSize={ft.BufferSize} openFileFlushTimeout={ft.OpenFileFlushTimeout} archiveAboveSize={ft.ArchiveAboveSize}";
                    }
                    else if (t is WrapperTargetBase w) info += " wrapping " + (w.WrappedTarget?.GetType().Name ?? "?") + " '" + w.WrappedTarget?.Name + "'";
                    Log.Verbose("[gamelog] NLog target " + info);
                }
                foreach (LoggingRule r in cfg.LoggingRules)
                    Log.Verbose($"[gamelog] NLog rule '{r.LoggerNamePattern}' levels [{string.Join(",", r.Levels)}] -> {string.Join(",", new List<Target>(r.Targets).ConvertAll(x => x.Name))}");

                var wrapped = new Dictionary<Target, AsyncTargetWrapper>();
                foreach (LoggingRule r in cfg.LoggingRules)
                    for (int i = 0; i < r.Targets.Count; i++)
                    {
                        if (!(r.Targets[i] is FileTarget ft)) continue;   // only plain (synchronous) file targets
                        if (!wrapped.TryGetValue(ft, out AsyncTargetWrapper a))
                        {
                            a = new AsyncTargetWrapper(ft.Name + "_cbasync", ft)
                            {
                                QueueLimit = 10000,
                                OverflowAction = AsyncTargetWrapperOverflowAction.Discard,
                                TimeToSleepBetweenBatches = 200,
                                BatchSize = 500,
                            };
                            wrapped[ft] = a;
                            cfg.AddTarget(a);
                        }
                        r.Targets[i] = a;
                    }
                if (wrapped.Count == 0) { Log.Info("[gamelog] no plain file targets to wrap (already async or none)"); return; }
                LogManager.Configuration = cfg;   // re-initialize with the wrappers
                Log.Info("[gamelog] game NLog file target(s) now written asynchronously: " + string.Join(", ", new List<Target>(wrapped.Keys).ConvertAll(x => x.Name)));
            }
            catch (Exception ex) { Log.Warn("[gamelog] NLog inspection/wrap failed (game log unchanged): " + ex.Message); }
        }
    }
}
