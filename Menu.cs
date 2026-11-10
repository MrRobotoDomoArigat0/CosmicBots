using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Paris.Engine;
using Paris.Engine.Data;
using Paris.Engine.Graphics;
using Paris.Engine.Input;
using Paris.Engine.Player;
using Paris.Game;
using Paris.Game.Actor;

namespace CosmicBots
{
    /// <summary>F7 overlay menu (keyboard: arrows, Enter, Esc/F7).</summary>
    public static class Menu
    {
        public static bool Open;
        private static int _row;
        private static Keys _key = Keys.F7;
        private static string _toast = ""; private static float _toastTime;

        private enum RowKind { Toggle, Hero, Color, Partner, PartnerColor, LockIn, Aggression, Skill, Close }
        private struct Row { public RowKind Kind; public int Slot; }
        private static readonly List<Row> Rows = new List<Row>();

        static Menu()
        {
            for (int s = 1; s <= 3; s++)
            {
                Rows.Add(new Row { Kind = RowKind.Toggle, Slot = s });
                Rows.Add(new Row { Kind = RowKind.Hero, Slot = s });
                Rows.Add(new Row { Kind = RowKind.Color, Slot = s });
                Rows.Add(new Row { Kind = RowKind.Partner, Slot = s });
                Rows.Add(new Row { Kind = RowKind.PartnerColor, Slot = s });
            }
            Rows.Add(new Row { Kind = RowKind.LockIn });
            Rows.Add(new Row { Kind = RowKind.Aggression });
            Rows.Add(new Row { Kind = RowKind.Skill });
            Rows.Add(new Row { Kind = RowKind.Close });
        }

        public static void InitKey()
        {
            if (!Enum.TryParse(Config.MenuKey, true, out _key)) _key = Keys.F7;
        }

        private static bool Pressed(Keys k)
        {
            var im = InputManagerBase.Singleton;
            return im != null && im.IsKeyJustPressed(k);
        }

        public static void Toast(string s) { _toast = s; _toastTime = 3f; Log.Info("[menu] " + s); }

        public static void Tick(float dt)
        {
            if (_toastTime > 0f) _toastTime -= dt;
            if (Pressed(_key)) { Open = !Open; return; }
            if (!Open) return;
            if (Pressed(Keys.Escape) || Pressed(Keys.Back)) { Open = false; return; }
            if (Pressed(Keys.Up)) Move(-1);
            if (Pressed(Keys.Down)) Move(1);
            if (Pressed(Keys.Left)) Change(-1, false);
            if (Pressed(Keys.Right)) Change(1, false);
            if (Pressed(Keys.Enter) || Pressed(Keys.Space)) Change(1, true);
        }

        private static void Move(int d)
        {
            int n = Rows.Count;
            for (int i = 0; i < n; i++)
            {
                _row = (_row + d + n) % n;
                if (Selectable(Rows[_row])) return;
            }
        }

        private static bool Selectable(Row r)
        {
            if (r.Kind == RowKind.Hero || r.Kind == RowKind.Partner) return Config.Slots[r.Slot].Enabled;
            if (r.Kind == RowKind.Color) return Config.Slots[r.Slot].Enabled && !string.IsNullOrEmpty(Config.Slots[r.Slot].MainHero);
            if (r.Kind == RowKind.PartnerColor) return Config.Slots[r.Slot].Enabled && !string.IsNullOrEmpty(Config.Slots[r.Slot].PartnerHero);
            return true;
        }

        private static void Change(int d, bool enter)
        {
            Row r = Rows[_row];
            switch (r.Kind)
            {
                case RowKind.Toggle:
                {
                    if (BotManager.Online) { Toast("Bots are disabled in online sessions."); return; }
                    SlotState st = BotManager.GetState(r.Slot);
                    if (st == SlotState.Human) { Toast($"P{r.Slot + 1} is a human player."); return; }
                    Config.Slots[r.Slot].Enabled = !Config.Slots[r.Slot].Enabled;
                    Config.Save();
                    Toast($"P{r.Slot + 1} bot " + (Config.Slots[r.Slot].Enabled ? "ON" : "OFF") + (BotManager.ContextReady ? "" : " (joins at character select / in a level)"));
                    break;
                }
                case RowKind.Hero:
                case RowKind.Partner:
                {
                    var cfg = Config.Slots[r.Slot];
                    bool partner = r.Kind == RowKind.Partner;
                    string cur = partner ? cfg.PartnerHero : cfg.MainHero;
                    List<int> avail = BotManager.AvailableHeroes();
                    var opts = new List<string> { "" };
                    foreach (int i in avail) opts.Add(Paris.Game.System.CharacterManager.Singleton.Characters[i].InternalName);
                    int pos = Math.Max(0, opts.FindIndex(o => string.Equals(o, cur, StringComparison.OrdinalIgnoreCase)));
                    int other = BotManager.IndexOf(partner ? cfg.MainHero : cfg.PartnerHero);
                    for (int k = 0; k < opts.Count; k++)
                    {
                        pos = (pos + (d >= 0 ? 1 : -1) + opts.Count) % opts.Count;
                        if (pos == 0 || BotManager.IndexOf(opts[pos]) != other) break; // hero and partner must differ
                    }
                    if (partner) cfg.PartnerHero = opts[pos]; else cfg.MainHero = opts[pos];
                    Config.Save();
                    GamePatches.EffectiveColor(r.Slot, partner, out _); // clamp the color to the new hero's color count
                    ApplyHeroChange(r.Slot);
                    break;
                }
                case RowKind.Color:
                case RowKind.PartnerColor:
                {
                    // 0 = Auto (game default), 1..n = the n colors the game offers this bot for the selected hero (its LB/RB list).
                    var cfg = Config.Slots[r.Slot];
                    bool partner = r.Kind == RowKind.PartnerColor;
                    int cur = GamePatches.EffectiveColor(r.Slot, partner, out int n);
                    if (n <= 0) { Toast("Pick a hero first; colors follow the selected hero."); return; }
                    cur = (cur + (d >= 0 ? 1 : -1) + n + 1) % (n + 1);
                    if (partner) cfg.PartnerColor = cur; else cfg.Color = cur;
                    Config.Save();
                    ApplyHeroChange(r.Slot);
                    break;
                }
                case RowKind.LockIn:
                    if (!BotManager.InCharSelect) { Toast("Only on the character select screen."); return; }
                    for (int s = 1; s <= 3; s++) BotManager.ForceLockIn[s] = true;
                    Toast("Bots locking in.");
                    break;
                case RowKind.Aggression:
                    Config.Aggression = Math.Max(0.5f, Math.Min(2f, (float)Math.Round(Config.Aggression + (d >= 0 ? 0.25f : -0.25f), 2)));
                    if (enter && Config.Aggression >= 2f) Config.Aggression = 0.5f;
                    Config.Save();
                    break;
                case RowKind.Skill:
                    Config.BotSkill = (Config.BotSkill + (d >= 0 ? 1 : 2)) % 3; Config.Save();
                    Toast("Bot skill: " + Config.SkillName);
                    break;
                case RowKind.Close:
                    if (enter) Open = false;
                    break;
            }
        }

        private static void ApplyHeroChange(int slot)
        {
            PlayerInfo pi = BotManager.GetPlayer(slot);
            if (!(pi?.Controller is BotController)) return;
            if (BotManager.InCharSelect)
            {
                if (pi is GamePlayerInfo g && g.IsReadyForSpawning) { BotManager.RequestRequeue(slot); Toast($"P{slot + 1} bot will re-pick in 1s (keep cycling, it waits)."); }
                // still selecting: the select hook picks the new hero on its next tick
            }
            else Toast($"P{slot + 1}: applies at character select (or toggle the bot off/on to re-join now).");
        }

        // ------------------------------------------------------------------ drawing
        public static string Sanitize(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace('[', '(').Replace(']', ')');

        // Layout (game coordinates = EngineSettings.GameResolution, e.g. 480x270):
        //   margin M = 8*scale above and below the box, padding P = 4*scale inside it.
        //   Height in lines: 2 title lines (3 with the Castle Doom note) + 0.3 gap + 19 rows + 3 x 0.3 bot-block gaps + 0.3 gap + 1 status line = 23.5 lines.
        //   Line height = 12*scale if that fits, else shrunk to fit (never below the font's own height + 1px).
        //   If it still does not fit, the rows scroll so the selected row is always visible ("^" / "v").
        private const float Gap = 0.3f, StatusLines = 1f;
        private const string CastleDoomNote = "Castle Doom bots: experimental";
        private static int _first;

        public static void Render()
        {
            Renderer r = Renderer.Singleton;
            if (r == null || EngineSettings.Singleton == null) return;
            Vector2 res = EngineSettings.Singleton.GameResolution;
            float scale = Math.Max(1f, (float)Math.Floor(res.Y / 360f));
            int font = r.DebugFontID;
            float glyph = 8f;
            try { glyph = Math.Max(1f, r.MeasureString(font, "Ag").Y); } catch { }
            float minLh = Math.Min(12f * scale, (float)Math.Ceiling(glyph * scale) + scale);

            if (!Open)
            {
                float y = res.Y - 12f * scale * 4.5f;
                if (_toastTime > 0f) Text(r, font, new Vector2(8 * scale, y - 12f * scale), _toast, Color.Yellow, scale);
                return;
            }

            bool castleDoom = BotManager.CastleDoomMode;
            float TitleLines = castleDoom ? 3f : 2f;   // title, credit (+ Castle Doom note)
            float M = 8f * scale, P = 4f * scale;
            float avail = res.Y - 2f * M - 2f * P;
            int n = Rows.Count;
            float lines = TitleLines + Gap + n + 3 * Gap + Gap + StatusLines;
            float lh = Math.Max(minLh, Math.Min(12f * scale, (float)Math.Floor(avail / lines)));
            bool scroll = lines * lh > avail;
            int visible = n;
            float rowGap = Gap;
            if (scroll)
            {
                rowGap = 0f;   // drop the block gaps first, then scroll
                visible = Math.Max(3, (int)Math.Floor((avail - (TitleLines + Gap + Gap + StatusLines) * lh) / lh));
                if (visible >= n) { visible = n; scroll = false; }
            }
            if (!scroll) _first = 0;
            else
            {
                if (_row < _first) _first = _row;
                if (_row >= _first + visible) _first = _row - visible + 1;
                _first = Math.Max(0, Math.Min(_first, n - visible));
            }
            int gapsShown = 0;
            for (int i = _first; i < _first + visible - 1; i++) if (Rows[i].Kind == RowKind.PartnerColor) gapsShown++;
            float h = (TitleLines + Gap + visible + gapsShown * rowGap + Gap + StatusLines) * lh + 2f * P;
            float w = Math.Min(330f * scale, res.X - 2f * M);
            Vector2 o = new Vector2(M, M);
            r.DrawBox(new Rectangle((int)o.X, (int)o.Y, (int)w, (int)h), new Color(0, 0, 0, 200));
            r.DrawRectangle(new Rectangle((int)o.X, (int)o.Y, (int)w, (int)h), new Color(255, 200, 60));
            Vector2 p = o + new Vector2(P + 2f * scale, P);
            Text(r, font, p, "COSMIC BOTS  (" + _key + " close, arrows, Enter)", new Color(255, 200, 60), scale); p.Y += lh;
            Text(r, font, p, "By MrRobotoDomoArigato", new Color(255, 200, 60), scale); p.Y += lh;
            if (castleDoom) { Text(r, font, p, CastleDoomNote, new Color(255, 140, 60), scale); p.Y += lh; }
            p.Y += lh * Gap;

            bool online = BotManager.Online;
            float rightX = o.X + w - P - 6f * scale;
            for (int i = _first; i < _first + visible; i++)
            {
                Row row = Rows[i];
                bool sel = i == _row;
                bool dim = online || !Selectable(row);
                Color c = dim ? Color.DimGray : sel ? Color.Yellow : Color.White;
                Text(r, font, p, (sel ? "> " : "  ") + RowText(row), c, scale);
                if (scroll && i == _first && _first > 0) Text(r, font, new Vector2(rightX, p.Y), "^", Color.Yellow, scale);
                if (scroll && i == _first + visible - 1 && _first + visible < n) Text(r, font, new Vector2(rightX, p.Y), "v", Color.Yellow, scale);
                p.Y += lh;
                if (row.Kind == RowKind.PartnerColor && i < _first + visible - 1) p.Y += lh * rowGap;
            }
            p.Y += lh * Gap;
            if (_toastTime > 0f) Text(r, font, p, _toast, Color.Yellow, scale);
            else if (!string.IsNullOrEmpty(BotManager.LastAction)) Text(r, font, p, BotManager.LastAction, new Color(255, 230, 150), scale);
        }

        private static string RowText(Row row)
        {
            switch (row.Kind)
            {
                case RowKind.Toggle:
                {
                    SlotState st = BotManager.GetState(row.Slot);
                    string s = st == SlotState.Human ? "Human" : st == SlotState.Blocked ? "disabled (online)" :
                               st == SlotState.Bot ? "ON  - " + BotStatus(row.Slot) : st == SlotState.Pending ? "ON  (waiting to join)" : "OFF";
                    if (st == SlotState.Human && Config.Slots[row.Slot].Enabled) s += " (bot ON, waits for free slot)";
                    return $"P{row.Slot + 1} bot: {s}";
                }
                case RowKind.Hero:
                    return "     Hero:    " + HeroLabel(Config.Slots[row.Slot].MainHero);
                case RowKind.Color:
                    return "     Color:   " + ColorLabel(row.Slot, false);
                case RowKind.PartnerColor:
                    return "     Partner color: " + ColorLabel(row.Slot, true);
                case RowKind.Partner:
                    return "     Partner: " + HeroLabel(Config.Slots[row.Slot].PartnerHero);
                case RowKind.LockIn:
                    return "Lock in bots now" + (BotManager.InCharSelect ? "" : "  (character select only)") + (Config.AutoLockInWhenHumansReady ? "  - auto when humans ready" : "");
                case RowKind.Aggression:
                    return "Aggression: " + Config.Aggression.ToString("0.00");
                case RowKind.Skill:
                    return "Bot skill: " + Config.SkillName;
                default:
                    return "Close";
            }
        }

        private static string ColorLabel(int slot, bool partner)
        {
            int c = GamePatches.EffectiveColor(slot, partner, out int n);
            if (c <= 0 || n <= 0) return "Auto";
            return c + " of " + n;
        }

        private static string HeroLabel(string internalName)
        {
            if (string.IsNullOrEmpty(internalName)) return "Auto";
            int idx = BotManager.IndexOf(internalName);
            if (idx < 0) return Sanitize(internalName) + " (not found)";
            return BotManager.HeroName(idx) + (BotManager.IsHeroAvailable(idx) ? "" : " (locked)");
        }

        private static string BotStatus(int slot)
        {
            PlayerInfo pi = BotManager.GetPlayer(slot);
            if (!(pi?.Controller is BotController bot)) return "";
            var g = pi as GamePlayerInfo;
            Player p = bot.ActivePlayer;
            if (p != null)
            {
                int idx = Paris.Game.System.CharacterManager.Singleton.Characters.IndexOf(p.CurrentCharacterInfo);
                return $"{BotManager.HeroName(idx)} {(int)Math.Round(p.HPRatio * 100f)}% HP, {bot.Status}";
            }
            if (g != null && g.SelectedCharacter != byte.MaxValue)
                return BotManager.HeroName(g.SelectedCharacter) + (g.SelectedSecondaryCharacter != byte.MaxValue ? " + " + BotManager.HeroName(g.SelectedSecondaryCharacter) : "") + ", " + bot.Status;
            return "picking hero";
        }

        private static void Text(Renderer r, int font, Vector2 pos, string s, Color c, float scale)
        {
            r.DrawText(font, pos, s, c, Color.Black, Color.Transparent, scale, 0f);
        }
    }
}
