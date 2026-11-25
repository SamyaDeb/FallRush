using FallRush.Shared;

namespace FallRush.OnChain
{
    /// <summary>
    /// harden #13: the "Session expired" banner. Set only when the program reports SessionInvalid (confirm status 5,
    /// custom error 6027); cleared as soon as one of our inputs is applied again (or a batch is accepted). Late inputs at
    /// the maximum lead (far from the ER validator) are lateness, not an expired session: they no longer raise it.
    /// Pure (tested in EditMode).
    /// </summary>
    public sealed class SessionHealth
    {
        public const byte StatusSessionInvalid = 5;
        public bool Expired { get; private set; }

        /// <summary>One input confirm (status as <see cref="InputConfirm.Status"/>). True when it raised the banner.</summary>
        public bool OnConfirm(byte status)
        {
            if (status == StatusSessionInvalid)
            {
                var raised = !Expired;
                Expired = true;
                return raised;
            }

            if (status == 0)
                Expired = false; // a batch landed: the key works
            return false;
        }

        /// <summary>A World for a tick we sent while playing: <paramref name="applied"/> = the arena's applied tick for our slot.</summary>
        public void OnSentTick(uint applied, uint tick)
        {
            if (applied >= tick)
                Expired = false;
        }

        public void Reset() => Expired = false;
    }

    /// <summary>
    /// harden #13: supervises the on-chain join of a confirmed whole match. If the join failed or has not resolved
    /// <see cref="RejoinSeconds"/> after the server confirmed the match, re-join once as a spectator; if that also fails
    /// (or hangs as long), give up (the session ends; the player is told to watch on /live). Pure (tested in EditMode).
    /// </summary>
    public sealed class JoinSupervisor
    {
        public enum Action : byte { None, RejoinSpectate, GiveUp }

        public const double RejoinSeconds = 15.0;
        private double _confirmedAt = -1, _rejoinAt = -1;
        private bool _gaveUp;

        public bool Rejoined => _rejoinAt >= 0;
        public double ConfirmedAt => _confirmedAt;

        /// <summary>Once per frame. <paramref name="error"/> = the transport's join / boot error (null = none yet).</summary>
        public Action Step(double now, bool fullMatch, bool confirmed, bool joined, string error)
        {
            if (!fullMatch || !confirmed || _gaveUp)
                return Action.None;
            if (_confirmedAt < 0)
                _confirmedAt = now;
            if (joined)
                return Action.None;
            if (_rejoinAt < 0)
            {
                if (error == null && now - _confirmedAt < RejoinSeconds)
                    return Action.None;
                _rejoinAt = now;
                return Action.RejoinSpectate;
            }

            if (error == null && now - _rejoinAt < RejoinSeconds)
                return Action.None;
            _gaveUp = true;
            return Action.GiveUp;
        }
    }

    /// <summary>
    /// harden #13 review: when an unconfirmed pre-join whose join never resolved must end: the game server plays the
    /// whole match itself (a Classic round is running) or is done with it (champion / lobby, 20 s after the pre-join, so a
    /// lobby state still in flight does not end it). Before, such a session lingered for good. Pure (tested in EditMode).
    /// </summary>
    public static class PrejoinRules
    {
        /// <param name="age">seconds since the session was created</param>
        public static bool Abandoned(MatchPhase server, bool fullMatch, double age)
        {
            if (fullMatch && server == MatchPhase.RoundActive)
                return true;
            return (server >= MatchPhase.Champion || server == MatchPhase.LobbyOpen) && age > 20;
        }
    }
}
