using System;
using System.Collections.Generic;

namespace FallRush.OnChain
{
    /// <summary>One World account notification (or backup-poll reply) as received.</summary>
    public struct WorldMsg
    {
        public byte[] Acc;
        public int Len;
        public uint Seq;
        public double RecvAt; // Time.realtimeSinceStartupAsDouble (ClientSim.Now)
        public bool FromPoll;
    }

    /// <summary>An input transaction's confirmation.</summary>
    public struct InputConfirm
    {
        public uint TxId;
        public byte Status; // 0 ok, 1 late, 2 out of window, 3 bad slot, 255 failed / timed out
        public uint ChainTick;
        public float LatencyMs;
    }

    [Serializable]
    public sealed class RosterEntry
    {
        public int slot;
        public string name;
        public string kind; // "human" | "bot"
    }

    /// <summary>The join reply (wire-v0 §4 <c>joined</c>).</summary>
    [Serializable]
    public sealed class JoinInfo
    {
        public string t;
        public int slot = 255;
        public string arena;
        public string map;
        public int rate = 30;
        public uint coreVersion;
        public string match;
        public string seed;
        public RosterEntry[] roster = Array.Empty<RosterEntry>();
        public string why;
    }

    /// <summary>
    /// P3-D: the client's chain I/O, one interface over the emulator WebSocket (Editor / Mac player:
    /// <see cref="WsChainTransport"/>) and the browser bundle (WebGL: <see cref="BundleChainTransport"/>, which itself talks
    /// to the emulator or the real rollup). Main-thread API; <see cref="Pump"/> once per frame.
    /// </summary>
    public interface IChainTransport : IDisposable
    {
        string Name { get; }
        /// <summary>Null while fine; a readable reason once it failed.</summary>
        string Error { get; }
        bool Connected { get; }
        void Connect(ChainConfig cfg);
        /// <summary>Asks to join (slot −1 = any free human slot) or to spectate.</summary>
        /// <param name="match">Optional 16-byte match id (hex): join only that match's lobby (hybrid pre-join).</param>
        void Join(string name, int slot, bool spectate, string match = null);
        /// <summary>The join reply once it arrived (slot 255 = spectator), else null.</summary>
        JoinInfo Joined { get; }
        /// <summary>Fire-and-forget <c>submit_input</c>; returns a local tx id.</summary>
        uint SendInputs(byte[] batch, int len);
        void Pump(double now);
        int DrainWorlds(List<WorldMsg> into);
        int DrainConfirms(List<InputConfirm> into);
        /// <summary>Round-trip estimate (ms, median of recent probes), NaN before the first sample.</summary>
        float RttMs { get; }
        /// <summary>Notification gaps seen (by sequence) and backup polls sent.</summary>
        long Gaps { get; }
        long Polls { get; }
        long Reconnects { get; }
        /// <summary>P4 economy: an async bundle call (<c>chips | state | bet | drop | receipt | claim</c>, args JSON);
        /// its JSON reply arrives through <see cref="EconResult"/>.</summary>
        void Econ(int id, string action, string argsJson);
        /// <summary>The reply of economy call <paramref name="id"/> once it arrived (taken once), else null.</summary>
        string EconResult(int id);
    }
}
