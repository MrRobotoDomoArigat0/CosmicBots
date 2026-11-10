using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CosmicBots
{
    public class BotSlotConfig
    {
        public bool Enabled;
        public string MainHero = "";     // CharacterInfo.InternalName, "" = automatic
        public string PartnerHero = "";  // CharacterInfo.InternalName, "" = automatic
        /// <summary>color variant for the main hero. 0 = Auto (the game's default color), N = the Nth color the game offers for that hero (wraps).</summary>
        public int Color;
        /// <summary>same for the partner hero.</summary>
        public int PartnerColor;
    }

    /// <summary>Settings persisted to CosmicBots.ini next to the loader.</summary>
    public static class Config
    {
        public static readonly BotSlotConfig[] Slots = { null, new BotSlotConfig(), new BotSlotConfig(), new BotSlotConfig() }; // index = PlayerID (1..3 => P2..P4)
        /// <summary>Always on: the mod never starts Epic Online Services (offline only; no online play, login or sync).</summary>
        public const bool BlockEpicOnlineServices = true;
        public static bool AutoLockInWhenHumansReady = true;
        public static float Aggression = 1f;      // 0.5 .. 2
        public static int BotSkill = 2;           // 0 = Easy, 1 = Normal, 2 = Hard
        public static string SkillName => BotSkill <= 0 ? "Easy" : BotSkill >= 2 ? "Hard" : "Normal";
        public static string MenuKey = "F7";
        /// <summary>Seconds the title screen waits for a player to join through Steam Input before switching to plain keyboard/gamepad input. 0 = never.</summary>
        public static float InputFallbackSeconds = 5f;

        public static string PathIni => System.IO.Path.Combine(Log.GameDir, "CosmicBots.ini");

        public static void Load()
        {
            try
            {
                if (!File.Exists(PathIni)) { Save(); return; }
                string section = "";
                bool seenSkill = false, seenAggr = false, seenFallback = false, seenColor = false;
                foreach (string raw in File.ReadAllLines(PathIni))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2).Trim().ToUpperInvariant(); continue; }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    if (section == "GENERAL")
                    {
                        switch (k)
                        {
                            case "autolockinwhenhumansready": AutoLockInWhenHumansReady = ParseBool(v, true); break;
                            case "aggression": if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float a)) Aggression = Math.Max(0.25f, Math.Min(3f, a)); seenAggr = true; break;
                            case "botskill": BotSkill = ParseSkill(v); seenSkill = true; break;
                            case "menukey": MenuKey = v; break;
                            case "inputfallbackseconds": InputFallbackSeconds = ParseF(v, 5f, 0f, 60f); seenFallback = true; break;
                        }
                    }
                    else if (section.Length == 2 && section[0] == 'P' && section[1] >= '2' && section[1] <= '4')
                    {
                        var s = Slots[section[1] - '1'];
                        switch (k)
                        {
                            case "enabled": s.Enabled = ParseBool(v, false); break;
                            case "mainhero": s.MainHero = v; break;
                            case "partnerhero": s.PartnerHero = v; break;
                            case "color": s.Color = ParseColor(v); break;
                            case "partnercolor": s.PartnerColor = ParseColor(v); seenColor = true; break;
                        }
                    }
                }
                Log.Info("Config loaded from " + PathIni + $" (skill {SkillName}, aggression {Aggression.ToString("0.##", CultureInfo.InvariantCulture)}, bots enabled: P2 {Slots[1].Enabled}, P3 {Slots[2].Enabled}, P4 {Slots[3].Enabled})");
                if (!seenSkill || !seenAggr || !seenFallback || !seenColor) Save();   // also rewrites an old INI without the removed settings once a key is missing
            }
            catch (Exception ex) { Log.Exception("Config load failed (using defaults)", ex); }
        }

        private static int _saveSeq; private static readonly object _saveLock = new object();
        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("; CosmicBots settings. Edited automatically by the F7 menu; safe to edit by hand while the game is closed. Delete this file to reset.");
                sb.AppendLine("[General]");
                sb.AppendLine("; true = bots lock in their heroes automatically once every human on the select screen is ready.");
                sb.AppendLine("AutoLockInWhenHumansReady=" + (AutoLockInWhenHumansReady ? "true" : "false"));
                sb.AppendLine("; Bot aggression: 0.5 (cautious) .. 2 (reckless). Default 1.");
                sb.AppendLine("Aggression=" + Aggression.ToString("0.##", CultureInfo.InvariantCulture));
                sb.AppendLine("; Easy / Normal / Hard: bot reaction time and decision quality. Default Hard.");
                sb.AppendLine("BotSkill=" + SkillName);
                sb.AppendLine("; Key that opens the mod menu (XNA Keys name, e.g. F7, F8, Insert).");
                sb.AppendLine("MenuKey=" + MenuKey);
                sb.AppendLine("; Seconds the title screen waits for a player to join through Steam Input (\"Press any button\") before switching to plain keyboard/gamepad input. Helps when Steam Input never sees your controller. 0 = off.");
                sb.AppendLine("InputFallbackSeconds=" + InputFallbackSeconds.ToString("0.##", CultureInfo.InvariantCulture));
                for (int i = 1; i <= 3; i++)
                {
                    sb.AppendLine();
                    sb.AppendLine("[P" + (i + 1) + "]");
                    sb.AppendLine("Enabled=" + (Slots[i].Enabled ? "true" : "false"));
                    sb.AppendLine("; hero internal name (e.g. Spiderman, Wolverine, DoctorDoom, InvisibleWoman), empty = automatic. DLC heroes are used only if you own the DLC.");
                    sb.AppendLine("MainHero=" + Slots[i].MainHero);
                    sb.AppendLine("PartnerHero=" + Slots[i].PartnerHero);
                    sb.AppendLine("; hero color: Auto (the game's default color) or 1, 2, 3 ... = the 1st, 2nd, 3rd color the game offers for that hero (same list as LB/RB on the select screen; locked colors are not in it).");
                    sb.AppendLine("Color=" + ColorName(Slots[i].Color));
                    sb.AppendLine("PartnerColor=" + ColorName(Slots[i].PartnerColor));
                }
                // write off the game thread (the game folder may be on a slow drive); only the newest pending save is written
                string text = sb.ToString(); int seq = System.Threading.Interlocked.Increment(ref _saveSeq);
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    lock (_saveLock)
                    {
                        if (seq != System.Threading.Volatile.Read(ref _saveSeq)) return;
                        try { File.WriteAllText(PathIni, text); } catch (Exception ex) { Log.Exception("Config save failed", ex); }
                    }
                });
            }
            catch (Exception ex) { Log.Exception("Config save failed", ex); }
        }

        private static int ParseSkill(string v)
        {
            v = v.Trim().ToLowerInvariant();
            if (v == "easy" || v == "0") return 0;
            if (v == "hard" || v == "2") return 2;
            return 1;
        }

        public const int MaxColor = 16;
        public static string ColorName(int c) => c <= 0 ? "Auto" : c.ToString(CultureInfo.InvariantCulture);
        private static int ParseColor(string v)
        {
            if (int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c)) return Math.Max(0, Math.Min(MaxColor, c));
            return 0; // "Auto" or anything else
        }

        private static float ParseF(string v, float def, float min, float max)
        {
            if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return def;
            return Math.Max(min, Math.Min(max, f));
        }

        private static bool ParseBool(string v, bool def)
        {
            v = v.ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            return def;
        }
    }
}
