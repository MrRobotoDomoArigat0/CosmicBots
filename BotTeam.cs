using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Paris.Engine.Player;
using Paris.Engine.Scene;
using Paris.Game.Actor;
using Paris.Game.Data;

namespace CosmicBots
{
    /// <summary>
    /// situational "Cosmic Assists" and team specials.
    /// In the game code a Cosmic Assist is Player.TeamHelp: hold TeamSwap and press a button, and the player's OWN
    /// off-screen partner hero (each player picks two heroes) jumps in for one move:
    ///   Swap+Attack = ComboAttack, Swap+Power = PowerAttack, Swap+Dodge/Block/Parry = Defense, Swap+Jump = Launcher
    ///   (after a launcher), Swap+Special = the partner's special (paid from the partner's special meter), and
    ///   Swap+Special while a team-up special is charged = TeamSpecial.
    /// Attack/Power/Defense/Launcher assists are paid from the active hero's energy (CharacterInfo.TeamHelp*Action.EnergyCost).
    /// Energy is per hero (HeroInfo.Energy) - bots never spend a human's meter, so the "leave the human a reserve" rule
    /// becomes "leave this bot enough for its own special unless it is an emergency".
    /// </summary>
    public partial class BotController
    {
        internal enum AssistKind { Attack, Power, Defense, Launcher, Special }

        private float _assistCheckCd;

        private int AssistCost(Player me, AssistKind k)
        {
            Player partner = me.SecondaryPlayer; CharacterInfo pci = partner?.CurrentCharacterInfo;
            if (pci == null) return -1;
            switch (k)
            {
                case AssistKind.Attack: return pci.TeamHelpAttackAction?.EnergyCost ?? -1;
                case AssistKind.Power: return pci.TeamHelpPowerAttackAction?.EnergyCost ?? -1;
                case AssistKind.Defense: return pci.TeamHelpDefenseAction?.EnergyCost ?? -1;
                case AssistKind.Launcher: return pci.TeamHelpLauncherAction?.EnergyCost ?? -1;
                case AssistKind.Special: return pci.TeamHelpSpecialAttackAction?.EnergyCost ?? -1;
            }
            return -1;
        }

        /// <summary>Can the partner hero be called in for this assist now, without starving our own special (unless emergency)?</summary>
        private bool AssistAffordable(Player me, AssistKind k, bool emergency)
        {
            try
            {
                if (!PartnerReady(me)) return false;
                if (!emergency && _teamCd > 0f) return false;
                int cost = AssistCost(me, k);
                if (cost < 0) return false;
                if (k == AssistKind.Special)
                    return me.SecondaryPlayer.HasEnergyRequired(cost, true);   // the partner's own special meter
                if (cost > me.Energy) return false;
                if (emergency) return true;
                int special = GameRefl.SpecialCost(me);
                return special <= 0 || me.Energy - cost >= special / 2 || _energyFullT > 3f;
            }
            catch { return false; }
        }

        private void DoAssist(AssistKind k, string why)
        {
            AbortPlan();
            Hold(A_SWAP, 0.08f);
            switch (k)
            {
                case AssistKind.Attack: Tap(A_ATTACK); break;
                case AssistKind.Power: Tap(A_POWER); break;
                case AssistKind.Defense: Tap(A_DODGE); break;
                case AssistKind.Launcher: JumpWhy("assist-launcher"); Tap(A_JUMP); break;
                case AssistKind.Special: Tap(A_SPECIAL); break;
            }
            _teamCd = Jit(k == AssistKind.Special ? 10f : 6f);
            Stat("assist"); Stat("assist-" + k.ToString().ToLowerInvariant());
            Intent = "cosmic assist: " + k.ToString().ToLowerInvariant() + " (" + why + ")";
            Status = "fighting";
        }

        /// <summary>Use an assist only when it matters: rescue, crowd, staggered boss, or a partner meter that is just sitting full.</summary>
        private bool TryAssist(Player me, List<Foe> foes, Foe target, int near, Player needy, float lowestHumanHp, Vector2 pos, float dt)
        {
            Tick(ref _assistCheckCd, dt);
            if (_assistCheckCd > 0f || foes.Count == 0 || !me.IsOnGround || me.IsInStateHurt || _def != null) return false;
            _assistCheckCd = 0.25f;
            if (!PartnerReady(me)) return false;

            // a human (or this bot) is low and surrounded
            int aroundNeedy = 0;
            if (needy != null && lowestHumanHp < 0.3f && Vector2.Distance(needy.Position2D, pos) < 170f)
                foreach (Foe f in foes) if (Vector2.Distance(f.P, needy.Position2D) < 90f) aroundNeedy++;
            int aroundMe = 0; foreach (Foe f in foes) if (Vector2.Distance(f.P, pos) < 80f) aroundMe++;
            bool emergency = aroundNeedy >= 2 || (me.HPRatio < 0.3f && aroundMe >= 2);
            if (emergency)
            {
                if (AssistAffordable(me, AssistKind.Special, true)) { DoAssist(AssistKind.Special, "rescue"); return true; }
                if (AssistAffordable(me, AssistKind.Power, true)) { DoAssist(AssistKind.Power, "rescue"); return true; }
                if (AssistAffordable(me, AssistKind.Attack, true)) { DoAssist(AssistKind.Attack, "rescue"); return true; }
            }
            if (!Chance(TeamChance)) return false;
            // boss is staggered / reeling and in reach
            if (target != null && target.Boss && Math.Abs(target.P.X - pos.X) < 120f)
            {
                bool vuln = false; try { vuln = target.E.IsStaggered || target.E.IsInStateHurt || target.E.IsInStateKnockdown; } catch { }
                if (vuln)
                {
                    if (AssistAffordable(me, AssistKind.Special, false)) { DoAssist(AssistKind.Special, "boss staggered"); return true; }
                    if (AssistAffordable(me, AssistKind.Attack, false)) { DoAssist(AssistKind.Attack, "boss staggered"); return true; }
                }
            }
            // many enemies on screen
            int crowd = new[] { 5, 4, 4 }[Skill];
            if (near >= 3 && foes.Count >= crowd)
            {
                if (AssistAffordable(me, AssistKind.Special, false)) { DoAssist(AssistKind.Special, "crowd"); return true; }
                if (AssistAffordable(me, AssistKind.Power, false)) { DoAssist(AssistKind.Power, "crowd"); return true; }
            }
            // our meter has been sitting full: spend the surplus on an assist instead of wasting the gain
            if (_energyFullT > 8f && near >= 1 && AssistAffordable(me, AssistKind.Power, false)) { DoAssist(AssistKind.Power, "meter full"); return true; }
            return false;
        }

        /// <summary>Join anybody's charging team special right away (even mid-combo), independent of the special cooldown.</summary>
        private bool TryJoinTeamSpecial(Player me, List<Player> humans)
        {
            if (_specialHolding || me.IsInSpecialAttack || !RegHasSpecial || me.IsInStateHurt) return false;
            bool someone = false;
            foreach (Player h in humans) if (GameRefl.TeamUpReady(h)) { someone = true; break; }
            if (!someone) foreach (BotController b in Live) if (b != this && b.RegHoldingSpecial) { someone = true; break; }
            if (!someone) return false;
            BeginSpecialHold(true, "join team special");
            return true;
        }
    }
}
