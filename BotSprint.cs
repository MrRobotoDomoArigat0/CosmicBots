using System;
using Microsoft.Xna.Framework;
using Paris.Game.Actor;

namespace CosmicBots
{
    /// <summary>
    /// sprinting. The game starts a sprint on a digital double-tap (PlayerSprint): entering StateWalk records the
    /// just-pressed direction and opens a SprintDigitalDoubleTapTime (0.25 s) window, releasing the stick for a frame arms
    /// it, and pushing the same X direction again calls DoSprintCheck -> StateSprint (CharacterInfo.RunSpeed, 180 px/s
    /// by default, ~2x walk). The bot reproduces exactly that input (idle frame, tap, release frame, press) only when it
    /// decided to sprint; otherwise its digital stick reads zero so normal steering can never trigger a sprint by accident.
    /// Sprint ends with a neutral or depth-only input before arrival, so the walk hysteresis settles the last
    /// few dozen px; holes on the sprint path cancel it (the game does not prevent pit fall-off while sprinting).
    /// </summary>
    public partial class BotController
    {
        private const float SPRINT_MIN_DIST = 200f;
        private bool _sprintWant, _inSprint, _sprintOwned;
        private int _sprintPhase, _sprintDir, _sprintFrames;
        private float _sprintGoalX, _sprintGoalY, _sprintCd, _sprintPhaseT, _sprintStartT;
        private string _sprintReason = "";

        /// <summary>Called by Think with the steering goal; decides whether a sprint is wanted this frame.</summary>
        private void SprintIntent(Player me, Vector2 pos, Vector2 goal, bool engaging, bool following, float leaderDist, bool onGround, bool flying, bool hurt, bool hazard, float dt)
        {
            Tick(ref _sprintCd, dt);
            float gdx = goal.X - pos.X, gdy = goal.Y - pos.Y;
            bool ok = onGround && !flying && !hurt && !hazard && !PlanBusy && _def == null && !_specialHolding && _forcedMoveT <= 0f
                      && (engaging || following) && Math.Abs(gdx) > SPRINT_MIN_DIST && Math.Abs(gdy) < Math.Abs(gdx) * 0.5f;
            // keep sprinting while it still pays off (arrival handled in SprintOutput)
            if (_inSprint) ok = onGround && !hurt && !PlanBusy && _def == null && !_specialHolding && Math.Sign(gdx) == _sprintDir;
            if (ok && !_inSprint && _sprintPhase == 0)
            {
                // the whole run must be on solid ground
                float rs = RunSpeed(me);
                Vector2 end = new Vector2(pos.X + Math.Sign(gdx) * Math.Min(Math.Abs(gdx), rs * 1.5f + 60f), pos.Y);
                if (PathHasPit(me, pos, end) || PathHasPit(me, pos, new Vector2(goal.X, pos.Y))) ok = false;
            }
            _sprintWant = ok;
            if (ok) { _sprintGoalX = goal.X; _sprintGoalY = goal.Y; _sprintReason = following ? (leaderDist > 260f ? "catch up" : "regroup") : "close in"; }
        }

        private static float RunSpeed(Player me) { try { return Math.Max(60f, me.CurrentRunSpeed); } catch { return 180f; } }

        /// <summary>Runs after FilterMove (before PitGuard): drives the double-tap and the sprint itself.</summary>
        private void SprintOutput()
        {
            Player me = ActivePlayer;
            if (me == null) { _sprintPhase = 0; _inSprint = false; return; }
            float now = BotManager.Time;
            string st = GameRefl.StateName(me);
            bool sprinting = st == "StateSprint";
            if (sprinting && !_inSprint) { Stat("sprints"); _sprintStartT = now; if (_sprintOwned) Stat("sprint-" + _sprintReason.Replace(' ', '-')); }
            if (!sprinting && _inSprint) _sprintOwned = false;
            _inSprint = sprinting;
            bool roomExit = false; try { roomExit = me.IsDoingRoomExit; } catch { }
            if (sprinting && (!_sprintOwned || roomExit)) return;   // the game's own runs (room exits): hands off
            Vector2 pos = me.Position2D;
            if (sprinting)
            {
                _sprintPhase = 0;
                float rs = RunSpeed(me);
                float age = now - _sprintStartT;
                float rem = (_sprintGoalX - pos.X) * _sprintDir;
                // distance still covered after letting go: rest of the minimum sprint time + deceleration + a margin
                float stopDist = rs * Math.Max(0f, 0.25f - age) + rs * rs / (2f * 1200f) + 40f;
                if (!_sprintWant || rem < stopDist)
                {
                    // leave the sprint: depth-only input switches straight to walk (works with either sprint setting)
                    float sy = Math.Sign(_sprintGoalY - pos.Y); if (sy == 0f) sy = 1f;
                    _move = new Vector2(0f, sy * 0.5f); _urgentMove = true;
                    _sprintCd = 0.6f;
                }
                else
                {
                    float y = MathHelper.Clamp(_move.Y, -0.6f, 0.6f);
                    if (Math.Abs(_sprintGoalY - pos.Y) < 4f) y = 0f;
                    _move = new Vector2(_sprintDir, y); _urgentMove = true;
                    Intent = "sprint (" + _sprintReason + ")";
                }
                return;
            }
            bool walkOrIdle = st == "StateWalk" || st == "StateIdle";
            if (_sprintPhase == 0)
            {
                if (!_sprintWant || _sprintCd > 0f || !walkOrIdle || _move.X == 0f || Math.Sign(_move.X) != Math.Sign(_sprintGoalX - pos.X)) return;
                _sprintDir = Math.Sign(_sprintGoalX - pos.X);
                _sprintPhase = st == "StateIdle" ? 2 : 1; _sprintPhaseT = now; _sprintFrames = 0;
                Stat("sprint-try");
            }
            if (!_sprintWant || (!walkOrIdle && _sprintPhase < 5) || now - _sprintPhaseT > 0.6f || roomExit)
            {
                if (_sprintPhase >= 4) Stat("sprint-fail");
                _sprintPhase = 0; _sprintCd = 1.5f; return;
            }
            _sprintFrames++;
            switch (_sprintPhase)
            {
                case 1: // let go until the walk state drops to idle (the old double-tap window has long expired)
                    _move = Vector2.Zero;
                    if (st == "StateIdle") { _sprintPhase = 2; _sprintFrames = 0; }
                    break;
                case 2: // first tap: idle -> walk records the just-pressed direction
                    _move = new Vector2(_sprintDir, 0f); _urgentMove = true;
                    if (st == "StateWalk") { _sprintPhase = 3; _sprintFrames = 0; }
                    break;
                case 3: // release one frame: arms the double tap
                    _move = Vector2.Zero;
                    if (_sprintFrames >= 1) { _sprintPhase = 4; _sprintFrames = 0; }
                    break;
                case 4: // second press: DoSprintCheck -> StateSprint
                case 5:
                    _move = new Vector2(_sprintDir, 0f); _urgentMove = true;
                    _sprintPhase = 5; _sprintOwned = true;
                    if (_sprintFrames > 8) { Stat("sprint-fail"); _sprintPhase = 0; _sprintCd = 2f; }
                    break;
            }
            Intent = "sprint start";
        }

        /// <summary>Digital stick as seen by the game's double-tap detector: only while the bot is deliberately sprinting.</summary>
        private Vector2 SprintDigital(bool justPressed)
        {
            if ((_sprintPhase == 0 && !(_inSprint && _sprintOwned)) || _move.X == 0f) return Vector2.Zero;
            if (Math.Abs(_move.X) < Math.Abs(_move.Y)) return Vector2.Zero;
            return new Vector2(Math.Sign(_move.X), 0f);
        }
    }
}
