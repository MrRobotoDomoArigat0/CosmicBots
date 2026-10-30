using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Paris.Engine;
using Paris.Engine.Combat;
using Paris.Engine.GameObject;
using Paris.Engine.Input;
using Paris.Engine.Player;
using Paris.Engine.Scene;
using Paris.Game;
using Paris.Game.Actor;
using Paris.Game.Controller;
using Paris.Game.Data;

namespace CosmicBots
{
    /// <summary>
    /// Bot "controller". Extends the game's DebugPlayerController so every engine path that special-cases debug
    /// bots keeps working. The game reads player input only through the virtual IsTriggered/IsJustTriggered/
    /// IsReleased/IsJustReleased/GetDirection methods overridden here; the brain (BotBrain.cs) decides what to press.
    /// </summary>
    public partial class BotController : DebugPlayerController
    {
        // Gameplay action ids (InputActionID.ID strings used by GamePlayerController).
        internal const string A_ATTACK = "PlayerAttack", A_POWER = "PlayerPowerAttack", A_SPECIAL = "PlayerSpecialAttack",
            A_JUMP = "PlayerJump", A_DODGE = "PlayerDodgeBlockParry", A_SWAP = "PlayerTeamSwap";

        private readonly HashSet<string> _down = new HashSet<string>();
        private readonly HashSet<string> _prev = new HashSet<string>();
        private readonly HashSet<string> _deferredTaps = new HashSet<string>();
        private readonly Dictionary<string, float> _holds = new Dictionary<string, float>();
        private Vector2 _move;                    // world-space direction (X right, Y = depth lane)

        public GamePlayerInfo Info { get; private set; }
        public Player ActivePlayer { get; private set; }
        public string Status { get; private set; } = "waiting";
        /// <summary>Short description of what the bot is doing right now (debug overlay / status HUD).</summary>
        public string Intent { get; private set; } = "";

        public override void Tick(float deltaTime)
        {
            base.Tick(deltaTime);
            _prev.Clear();
            foreach (string s in _down) _prev.Add(s);
            _down.Clear();
            // taps that had to wait one frame because the button was still down
            foreach (string s in _deferredTaps) _down.Add(s);
            _deferredTaps.Clear();
            // held buttons
            if (_holds.Count > 0)
            {
                _holdKeys.Clear(); _holdKeys.AddRange(_holds.Keys); var keys = _holdKeys;
                foreach (string k in keys)
                {
                    float t = _holds[k];
                    if (t > 0f) { _down.Add(k); _holds[k] = t - deltaTime; }
                    else _holds.Remove(k);
                }
            }
            _move = Vector2.Zero; _urgentMove = false;
            try { Think(deltaTime); FilterMove(); SprintOutput(); PitGuard(); _lastOut = _move; _lastOutSteer = _moveFromSteer && !_urgentMove; _lastOutEngaging = _steerEngaging; }
            catch (Exception ex)
            {
                _down.Clear(); _holds.Clear(); _move = Vector2.Zero; _plan.Clear();
                Status = "error";
                Mod.LogOnce("bot-think", ex);
            }
        }

        // ---------------------------------------------------------------- input primitives used by the brain
        /// <summary>Press for one frame (a new "just pressed" edge). If the button is still down from last frame, the press happens next frame.</summary>
        private void Tap(string id)
        {
            if (_prev.Contains(id) && !_down.Contains(id)) _deferredTaps.Add(id);
            else _down.Add(id);
        }

        /// <summary>Press now and keep it down for `seconds`.</summary>
        private void Hold(string id, float seconds)
        {
            Tap(id);
            _holds[id] = Math.Max(seconds, _holds.TryGetValue(id, out float cur) ? cur : 0f);
        }

        // ---------------------------------------------------------------- final movement filter (anti-twitch)
        private bool _urgentMove;                 // dodges, evasive jumps, hazards, combo chase: bypass the reversal cooldown
        private int _outSX, _outSY;               // last non-zero output sign per axis
        private float _outTX = -10f, _outTY = -10f;
        private const float REVERSE_COOLDOWN = 0.13f, MIN_COMPONENT = 0.2f;

        /// <summary>
        /// Player.GetDirection normalizes any non-zero input (no analog walk speed, no deadzone) and HandleMovement flips
        /// facing on any non-zero X. So drop tiny components and never reverse an axis within REVERSE_COOLDOWN of moving
        /// the other way (stop for a moment instead). Counts rapid reversals for the [stats] line.
        /// </summary>
        private void FilterMove()
        {
            float now = BotManager.Time;
            Vector2 m = _move;
            if (m != Vector2.Zero)
            {
                if (m.LengthSquared() > 1f) m.Normalize();
                if (Math.Abs(m.X) < MIN_COMPONENT && Math.Abs(m.Y) >= MIN_COMPONENT) m.X = 0f;
                if (Math.Abs(m.Y) < MIN_COMPONENT && Math.Abs(m.X) >= MIN_COMPONENT) m.Y = 0f;
                if (!_urgentMove)
                {
                    if (m.X != 0f && _outSX != 0 && Math.Sign(m.X) != _outSX && now - _outTX < REVERSE_COOLDOWN) m.X = 0f;
                    if (m.Y != 0f && _outSY != 0 && Math.Sign(m.Y) != _outSY && now - _outTY < REVERSE_COOLDOWN) m.Y = 0f;
                }
            }
            if (m.X != 0f)
            {
                int sx = Math.Sign(m.X);
                if (_outSX != 0 && sx != _outSX && now - _outTX < 0.6f && !_urgentMove) Stats.Reversals++;
                _outSX = sx; _outTX = now;
            }
            if (m.Y != 0f)
            {
                int sy = Math.Sign(m.Y);
                if (_outSY != 0 && sy != _outSY && now - _outTY < 0.6f && !_urgentMove) Stats.Reversals++;
                _outSY = sy; _outTY = now;
            }
            _move = m;
        }

        private readonly List<string> _holdKeys = new List<string>(8);

        private void Release(string id) { _holds.Remove(id); _down.Remove(id); _deferredTaps.Remove(id); }
        private bool IsHolding(string id) => _holds.ContainsKey(id);

        // ---------------------------------------------------------------- input surface read by the game
        // ---------------------------------------------------------------- menu intents (set by MenuFollow for one menu update)
        /// <summary>Menu action id ("MenuAccept"/"MenuCancel") the bot "presses" during the current menu update, or null.</summary>
        public string MenuPress;
        /// <summary>MenuMove direction (menu convention: +Y = up) the bot "pushes" during the current menu update.</summary>
        public Vector2 MenuDir;
        public void ClearMenuIntent() { MenuPress = null; MenuDir = Vector2.Zero; }

        public override bool IsTriggered(InputActionID action) => action != null && (_down.Contains(action.ID) || (MenuPress != null && action.ID == MenuPress));

        public override bool IsJustTriggered(InputActionID action)
        {
            if (action == null) return false;
            if (MenuPress != null && action.ID == MenuPress) return true;
            if (action.ID == "MenuAccept") return base.IsJustTriggered(action); // keep the engine's one-shot auto-confirm
            return _down.Contains(action.ID) && !_prev.Contains(action.ID);
        }

        public override bool IsJustReleased(InputActionID action) => action != null && !_down.Contains(action.ID) && _prev.Contains(action.ID);

        public override bool IsReleased(InputActionID action) => action == null || !_down.Contains(action.ID);

        public override Vector2 GetDirection(bool justPressed)
        {
            if (justPressed || _move == Vector2.Zero) return Vector2.Zero;
            Vector2 d = _move;
            if (d.LengthSquared() > 1f) d.Normalize();
            d.Y = -d.Y; // Player.GetDirection flips Y back (same convention as DebugPlayerController)
            return d;
        }

        // Menu reads get MenuDir; the digital stick (double-tap sprint detector) only reports while the bot sprints on purpose.
        public override Vector2 GetDirection(InputDirectionActionID inputId, bool justPressed, bool shouldGetMouseDelta = false)
        {
            if (inputId == null) return Vector2.Zero;
            if (inputId.ID == "MenuMove") return MenuDir;
            if (inputId.ID == "PlayerMoveDigital") return SprintDigital(justPressed);   // v0.2.5: deliberate double-tap sprint only
            return Vector2.Zero;
        }

        private GamePlayerInfo FindMyInfo()
        {
            if (Info != null && Info.Controller == this) return Info;
            PlayerManager pm = PlayerManager.Singleton;
            if (pm == null) return null;
            foreach (PlayerInfo pi in pm.Players)
                if (pi != null && pi.Controller == this) return pi as GamePlayerInfo;
            return null;
        }
    }
}
