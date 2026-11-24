using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FallRush.Shared;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FallRush.Net
{
    /// <summary>
    /// The server side of a match (M4): the Unity host of the engine-free <see cref="MatchDirector"/>. Replaces M3's
    /// <c>ServerRace</c>. Owned by <see cref="ServerSim"/>.
    /// <list type="bullet">
    /// <item><b>Scene flow (M4-3, no NGO scene management):</b> the director asks for a map → the server loads it (single
    /// mode; the sim pauses for those frames), rebuilds the world and sends <c>round</c> (map + content hash); clients load
    /// it and answer <c>ready</c>; the round starts when every connected alive human is ready or after 10 s.</item>
    /// <item><b>Messages:</b> <c>match</c> (full state per recipient, on every change), <c>mevt</c> (transcript events),
    /// <c>evt</c> (gameplay events, one sequence per server process), <c>rstate</c>, <c>round</c>, <c>rx</c>.</item>
    /// <item><b>Joins and reconnects (M4-6):</b> lobby joins get a slot and a 16-byte reconnect token; during a match a
    /// <c>hello</c> carrying the token of a dropped slot takes it back (full resync); anyone else watches as a viewer
    /// until the next lobby.</item>
    /// <item><b>Event log:</b> every transcript event is appended to <c>match-logs/match-&lt;id&gt;.jsonl</c> (M5's outbox input).</item>
    /// </list>
    /// </summary>
    public sealed class ServerMatch : IMatchHost
    {
        private readonly ServerSim _sim;
        private readonly NetworkManager _nm;
        public MatchDirector Director { get; }

        private byte _wantMap = 255;
        private RoundParams _wantParams;
        private bool _loaded;
        private bool _loadingScene;
        private readonly HashSet<ulong> _viewers = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> _rxAt = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, (string name, byte[] token, byte preset, byte tier)> _viewerHello = new Dictionary<ulong, (string, byte[], byte, byte)>();
        private uint _evtSeq;
        private bool _matchDirty, _stateDirty;
        private readonly byte[] _buf = new byte[4096];
        private StreamWriter _log;
        private int _loggedSerial = -1;
        private readonly System.Security.Cryptography.RandomNumberGenerator _rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        private BotBrainSet _bots;
        /// <summary>P3-F: the Classic → on-chain switch at the Final (null unless <c>-fallrushOnchainFinal</c>).</summary>
        public readonly ChainFinalHandoff Handoff = ChainFinalHandoff.Enabled ? new ChainFinalHandoff() : null;
        private bool _foncSent;

        /// <summary>Server ticks simulated per network tick while no human is connected (soak; 1 = real time).</summary>
        public int SimSpeed = 1;
        public int MatchesTarget;
        public string LogDir;

        // ---- stats ----
        public long MatchBytes, MevtBytes, EvtBytes, RxBytes, RoundBytes, StateBytes;
        public long MatchMsgs, MevtMsgs, EvtMsgs, RxMsgs;
        public readonly List<string> MatchSummaries = new List<string>();

        public ServerMatch(ServerSim sim, NetworkManager nm)
        {
            _sim = sim;
            _nm = nm;
            Director = new MatchDirector(sim.Core, this);
            ConfigureFromArgs();
            NetBootstrap.RoomRefresh = PollRoom;
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Protocol.Msg.Ready, OnReady);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Protocol.Msg.Control, OnControl);
            // The initial scene is the lobby; the director's first LoadMap finds it already loaded.
            _wantMap = sim.Map != null ? sim.Map.MapId : (byte)255;
            Director.Start();
        }

        public void Dispose()
        {
            _nm.CustomMessagingManager?.UnregisterNamedMessageHandler(Protocol.Msg.Ready);
            _nm.CustomMessagingManager?.UnregisterNamedMessageHandler(Protocol.Msg.Control);
            _log?.Dispose();
        }

        private void ConfigureFromArgs()
        {
            var d = Director;
            var bots = ServerSim.ArgInt("-fallrushBots", "GAMESERVER_BOTS", 0);
            MatchesTarget = ServerSim.ArgInt("-fallrushMatches", "GAMESERVER_MATCHES", 0);
            d.Soak = MatchesTarget > 0 || ServerSim.ArgStr("-fallrushSoak", "GAMESERVER_SOAK") == "1";
            d.MatchSize = Mathf.Clamp(ServerSim.ArgInt("-fallrushMatchSize", "GAMESERVER_MATCH_SIZE", bots > 0 ? Math.Max(RoundTargets.MinPlayers, bots) : Protocol.MaxPlayers), RoundTargets.MinPlayers, Protocol.MaxPlayers);
            SimSpeed = Mathf.Clamp(ServerSim.ArgInt("-fallrushSimSpeed", "GAMESERVER_SIM_SPEED", 1), 1, 16);
            LogDir = ServerSim.ArgStr("-fallrushMatchLogDir", "GAMESERVER_MATCH_LOG_DIR") ?? Path.Combine(Directory.GetCurrentDirectory(), "match-logs");
            var plan = NetBootstrap.PlanFromArgs();
            if (plan != null)
                d.PlanOverride = plan;
            var rounds = ServerSim.ArgInt("-fallrushRounds", "GAMESERVER_ROUNDS", 0);
            if (rounds > 0 && rounds < 4 && plan == null)
                d.PlanOverride = new byte[] { 0, 1, 2, 3 }.AsSpan(0, rounds).ToArray(); // the deal's first N rounds (maps per round are fixed in pool v1)
            var v = ServerSim.ArgStr("-fallrushVariant", "GAMESERVER_VARIANT");
            if (!string.IsNullOrEmpty(v))
                d.VariantOverride = v == "B" || v == "b" || v == "1" ? 1 : 0;
            var m = ServerSim.ArgStr("-fallrushModifier", "GAMESERVER_MODIFIER");
            if (!string.IsNullOrEmpty(m) && ModifierEffects.TryParse(m, out var mid))
                d.ModifierOverride = mid;
            var seedArg = ServerSim.ArgStr("-fallrushSeed", "GAMESERVER_SEED");
            byte[] fixedSeed = null;
            if (!string.IsNullOrEmpty(seedArg))
                fixedSeed = ParseSeed(seedArg);
            byte[] prev = null;
            d.SeedSource = serial =>
            {
                if (fixedSeed != null)
                {
                    prev = prev == null ? fixedSeed : Sha256.Hash(prev); // match k: sha256^(k−1)(seed)
                    return prev;
                }

                var s = new byte[32];
                _rng.GetBytes(s);
                return s;
            };
            Debug.Log($"[gs] match mode: size={d.MatchSize} soak={d.Soak} matches={MatchesTarget} simSpeed={SimSpeed} plan={(d.PlanOverride == null ? "deal (4 rounds)" : string.Join(",", Array.ConvertAll(d.PlanOverride, x => MapPoolV1.KeyOf(x))))} seed={(fixedSeed != null ? Sha256.Hex(fixedSeed).Substring(0, 16) + "…" : "random per match")} log={LogDir}");
        }

        private float _statusAt;

        /// <summary>
        /// P5 directory (additive, read-only for the match): once a second the server writes <c>status.json</c> next to
        /// its match logs (atomic replace). The arena's keeper shares the log volume and publishes it through
        /// <c>/api/status</c> (players, phase, round, match id), which the landing page and Quick Play read.
        /// </summary>
        private static readonly string[] TierNames = { "Rookie", "Contender", "Champion" };

        /// <summary>
        /// P7-UX (OC-46): the private room file (GAMESERVER_ROOM_FILE, written by the indexer's room service): while it holds
        /// an unexpired code, the server admits only hellos with that code (NetBootstrap approval) and the director waits for
        /// the host's Start. Polled once a second with the status write; a missing / bad / expired file = a public server.
        /// </summary>
        private void PollRoom()
        {
            var f = RoomFile;
            string code = null;
            var mix = 1;
            var judge = false;
            if (!string.IsNullOrEmpty(f))
            {
                try
                {
                    if (File.Exists(f))
                    {
                        var j = JsonUtility.FromJson<RoomJson>(File.ReadAllText(f));
                        if (j != null && j.expiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        {
                            code = Protocol.CleanRoom(j.code);
                            mix = Mathf.Clamp(j.botMix, 0, 2);
                            judge = j.judge;
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[gs] room file: " + e.Message);
                }
            }

            if (code != NetBootstrap.RoomCode)
            {
                Debug.Log($"[gs] room {(code == null ? "closed: public server" : "open (private, host starts)")} mix={mix}");
                NetBootstrap.RoomCode = code;
                Director.JudgeAutoStartUsed = false; // a new room (or none): its first judge match may start by itself
                OnMatchChanged();
            }

            Director.PrivateRoom = code != null;
            Director.RoomBotMix = mix;
            Director.JudgeMode = code != null && judge;
        }

        [Serializable]
        private sealed class RoomJson
        {
            public string code;
            public long expiresAt;
            public int botMix = 1;
            public bool judge;
        }

        private static string RoomFile => ServerSim.ArgStr("-fallrushRoomFile", "GAMESERVER_ROOM_FILE");

        private void WriteStatus()
        {
            var now = Time.realtimeSinceStartup;
            if (now - _statusAt < 1f)
                return;
            _statusAt = now;
            PollRoom();
            if (string.IsNullOrEmpty(LogDir))
                return;
            try
            {
                var d = Director;
                var id = d.MatchId != null ? Sha256.Hex(d.MatchId) : "";
                var json = $"{{\"t\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"phase\":\"{d.Phase}\",\"inMatch\":{(d.InMatch ? "true" : "false")},\"round\":{d.Round},\"humans\":{d.HumansConnected()},\"size\":{d.MatchSize},\"serial\":{d.MatchSerial},\"completed\":{d.MatchesCompleted},\"match\":\"{id}\",\"onchainFinal\":{(Handoff != null ? "true" : "false")},\"room\":{(d.PrivateRoom ? "true" : "false")},\"judge\":{(d.JudgeMatch || d.JudgeMode ? "true" : "false")},\"lobbyTier\":\"{TierNames[d.LobbyTier]}\",\"conns\":{NetBootstrap.Instance?.ServerConnectedCount ?? 0},\"maxConns\":{NetBootstrap.MaxConnections},\"viewers\":{_viewers.Count}}}";
                Directory.CreateDirectory(LogDir);
                var tmp = Path.Combine(LogDir, "status.json.tmp");
                File.WriteAllText(tmp, json);
                var dst = Path.Combine(LogDir, "status.json");
                if (File.Exists(dst))
                    File.Delete(dst);
                File.Move(tmp, dst);
            }
            catch (Exception e)
            {
                if (_statusAt < 5f)
                    Debug.LogWarning("[gs] status.json: " + e.Message);
            }
        }

        /// <summary>64 hex chars = the 32-byte seed; anything else = sha256("fallrush-dev-seed:" + text).</summary>
        public static byte[] ParseSeed(string s)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);
            if (s.Length == 64)
            {
                var b = new byte[32];
                var ok = true;
                for (var i = 0; i < 32 && ok; i++)
                    ok = byte.TryParse(s.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i]);
                if (ok)
                    return b;
            }

            return Sha256.Hash(Encoding.UTF8.GetBytes("fallrush-dev-seed:" + s));
        }

        // =====================================================================
        // Tick
        // =====================================================================

        /// <summary>Before the sim step: the director. False = the server is loading a map this frame (skip the step).</summary>
        public bool BeforeStep()
        {
            // Soak: quit after N matches (checked before the lobby can start the next one).
            if (MatchesTarget > 0 && !_quitting && Director.MatchesCompleted + Director.MatchesAborted >= MatchesTarget)
            {
                _quitting = true;
                Debug.Log($"[gs] soak done: {Director.MatchesCompleted} complete, {Director.MatchesAborted} aborted of {MatchesTarget}");
                foreach (var line in MatchSummaries)
                    Debug.Log($"[gs] soak {line}");
                Application.Quit(Director.MatchesCompleted == MatchesTarget ? 0 : 3);
                return false;
            }

            if (_quitting)
                return false;
            // beta audit: an on-chain Final that never reports a result must not hold the arena forever
            if (Director.Rules is ChainFinalRules cf && cf.TimedOut && Director.InMatch && Director.Phase != MatchPhase.Aborted && Director.Phase < MatchPhase.Finalizing)
                Director.Abort(AbortReason.ServerStop);
            Director.PreStep();
            return !_loadingScene;
        }

        public void AfterStep()
        {
            Director.AfterStep();
            var rules = Director.Rules;
            if (rules != null && rules.Pending.Count > 0)
            {
                foreach (var e in rules.Pending)
                {
                    var x = e;
                    x.Seq = ++_evtSeq;
                    SendEvent(in x);
                }

                rules.Pending.Clear();
            }

            if (_stateDirty)
            {
                _stateDirty = false;
                BroadcastState();
            }

            // beta audit: also during the countdown: a lobby that already has humans goes LobbyOpen → Countdown on the
            // same tick, so watchers of the last match were never seated and watched match after match
            if ((Director.Phase == MatchPhase.LobbyOpen || Director.Phase == MatchPhase.Countdown) && _viewers.Count > 0 && _loaded && !_loadingScene)
                SeatViewers();
            WriteStatus();
            while (ShellCheck.Results.TryDequeue(out var sc))
            {
                // P5 task 4: apply a verified capsule shell (lobby only; a falsified id keeps the preset)
                var slot = _sim.Core.FindSlot(sc.client);
                Debug.Log($"[gs] capsule shell client={sc.client} slot={slot} asset={sc.asset}: {sc.why}");
                if (sc.preset >= 0 && slot >= 0 && !Director.InMatch)
                {
                    Director.Presets[slot] = CosmeticPresets.Clamp(sc.preset);
                    OnMatchChanged();
                }
            }
            if (Director.MatchId != null && Director.MatchSerial != _loggedSerial && Director.Phase >= MatchPhase.RosterLocked)
            {
                OpenLog();
                if (Handoff != null && Handoff.Rollup)
                    SendPrejoin();
            }
            if (_matchDirty)
            {
                _matchDirty = false;
                BroadcastMatch();
            }

            _bots?.AfterStep(_sim.Core);

        }

        private bool _quitting;

        // =====================================================================
        // IMatchHost
        // =====================================================================

        public void LoadMap(byte mapId, in RoundParams p)
        {
            if (Handoff != null)
                BeginHandoff(mapId, in p);
            _wantMap = mapId;
            _wantParams = p;
            _wantParams.RoundStartTick = mapId == MapPoolV1.Lobby ? 0u : WaitingRoundStart;
            _bots = null;
            var current = _sim.Map;
            if (current != null && current.MapId == mapId && current.gameObject.scene.isLoaded)
            {
                // Same scene (lobby → lobby, a dev plan repeating a map): new world for the new params, no reload.
                _sim.SwitchMap(current, _wantParams);
                _loaded = true;
                _loadingScene = false;
                _sim.BroadcastRound();
                return;
            }

            _loaded = false;
            _loadingScene = true;
            var scene = NetBootstrap.SceneForMap(mapId);
            Debug.Log($"[gs] load {scene} for {(mapId == MapPoolV1.Lobby ? "the lobby" : $"round {p.RoundIndex}")} (load serial {Director.LoadSerial})");
            SceneManager.LoadScene(scene);
        }

        /// <summary>Hazard time is far negative while a round map waits for its START (gates stay closed).</summary>
        public const uint WaitingRoundStart = 0x7FFF0000u;

        /// <summary>Called by <see cref="ServerSim.OnMapLoaded"/> when the requested scene finished loading.</summary>
        public void OnMapLoaded(MapInfo map)
        {
            if (map.MapId != _wantMap)
            {
                Debug.LogWarning($"[gs] loaded {map.gameObject.scene.name} but wanted map {_wantMap}");
                return;
            }

            _sim.SwitchMap(map, _wantParams);
            _loadingScene = false;
            _loaded = true;
            _sim.BroadcastRound(); // clients load it; they answer `ready`
            _matchDirty = true;
        }

        public bool MapLoaded => _loaded && !_loadingScene && (Handoff == null || Handoff.Phase != ChainFinalHandoff.State.Deciding);

        /// <summary>P3-F: at the last round (the Final), ask the arena for it; the round waits (≤ 8 s) for the answer.</summary>
        private void BeginHandoff(byte mapId, in RoundParams p)
        {
            _foncSent = false;
            if (mapId == MapPoolV1.Lobby || Director.Round != Director.RoundCount || Director.RoundCount < 2)
                return;
            var roster = new List<(int slot, string name, bool bot)>();
            for (var i = 0; i < Director.Roster.Length; i++)
            {
                var r = Director.Roster[i];
                if (r.Occupied && r.Alive)
                    roster.Add((i, r.Name, r.Bot));
            }

            Debug.Log($"[gs] on-chain Final: handing round {Director.Round} ({MapPoolV1.KeyOf(mapId)}) to {Handoff.ControlUrl} with {roster.Count} players");
            Handoff.TimeoutSeconds = Director.JudgeMatch ? ChainFinalHandoff.JudgeTimeoutSeconds : ChainFinalHandoff.LiveTimeoutSeconds;
            Handoff.Begin(Sha256.Hex(Director.MatchId), p.HazardSeed, roster.ToArray());
        }

        /// <summary>P3-M: rollup mode, at roster lock: clients pre-join the arena's lobby (their Classic slot).</summary>
        private void SendPrejoin()
        {
            var humans = new List<(int slot, string name, bool bot)>();
            for (var i = 0; i < Director.Roster.Length; i++)
                if (Director.Roster[i].Occupied && !Director.Roster[i].Bot)
                    humans.Add((i, Director.Roster[i].Name, false));
            var msg = Handoff.PrejoinMessage(Sha256.Hex(Director.MatchId), humans.ToArray());
            var b = Encoding.UTF8.GetBytes(msg);
            Buffer.BlockCopy(b, 0, _buf, 0, b.Length);
            long bytes = 0, count = 0;
            foreach (var id in Recipients())
                Send(ChainFinalHandoff.Msg, id, b.Length, ref bytes, ref count);
            Debug.Log($"[gs] fonc prejoin → {count} clients: {msg}");
        }

        /// <summary>P3-F: tells every client to play the Final on the arena (<c>fonc</c>, reliable, JSON).</summary>
        private void SendFinalOnchain()
        {
            var b = Encoding.UTF8.GetBytes(Handoff.Message);
            Buffer.BlockCopy(b, 0, _buf, 0, b.Length);
            long bytes = 0, count = 0;
            foreach (var id in Recipients())
                Send(ChainFinalHandoff.Msg, id, b.Length, ref bytes, ref count);
            Debug.Log($"[gs] fonc → {count} clients: {Handoff.Message}");
        }

        public IRoundRules CreateRules(byte mapId)
        {
            var map = _sim.Map;
            var def = map != null ? map.definition : null;
            var rules = RulesFactory.Create(map, mapId);
            if (Handoff != null && Handoff.Phase == ChainFinalHandoff.State.Live && mapId != MapPoolV1.Lobby && Director.Round == Director.RoundCount)
            {
                if (!_foncSent)
                {
                    _foncSent = true;
                    SendFinalOnchain();
                }

                return new ChainFinalRules(rules, Handoff);
            }

            return rules;
        }

        public Func<int, uint, MotorState, InputCmd> CreateBotInput(IRoundRules rules, RosterSlot[] roster, in RoundParams p)
        {
            _bots = new BotBrainSet(_sim.Map, _sim.Core, rules, roster, in p);
            return _bots.Input;
        }

        public void OnRoundStarted(in RoundParams p)
        {
            _sim.SetRound(p);
            var rules = Director.Rules;
            rules.PhaseChanged += _ => _stateDirty = true; // after this tick's events (AfterStep), so rstate never runs ahead of evt
            Debug.Log($"[gs] round {p.RoundIndex}/{Director.RoundCount} {MapPoolV1.KeyOf(p.MapId)} ({rules.Mode}) start: {p} rush@{p.RoundStartTick} participants={p.PlayerCount}");
            _sim.BroadcastRound();
            if (rules.Pending.Count > 0)
            {
                foreach (var e in rules.Pending)
                {
                    var x = e;
                    x.Seq = ++_evtSeq;
                    SendEvent(in x);
                }

                rules.Pending.Clear();
            }

            BroadcastState();
        }

        public void OnEvent(in MatchEvent e)
        {
            var n = MatchEventCodec.Encode(_buf, in e);
            foreach (var id in Recipients())
                Send(Protocol.Msg.MatchEvent, id, n, ref MevtBytes, ref MevtMsgs);
            WriteLog(in e);
            Debug.Log($"[gs] mevt {e} transcript={Sha256.Hex(e.TranscriptAfter).Substring(0, 16)}");
            if (e.Tag == Transcript.CompleteMatch || e.Tag == Transcript.Abort)
                OnMatchEnded(in e);
        }

        public void OnMatchChanged() => _matchDirty = true;

        // =====================================================================
        // Connections
        // =====================================================================

        public void OnClientConnected(ulong clientId)
        {
            var hello = NetBootstrap.TakeHello(clientId);
            var core = _sim.Core;
            // beta audit: the same player connected again (phone switched network, tab reloaded) while the server still
            // holds the old connection: drop the stale one first, so the new one resumes the seat instead of watching
            if (hello.Token != null)
            {
                var ghost = Director.FindConnectedSlot(hello.Token);
                if (ghost >= 0 && core.Slots[ghost].ClientId != clientId)
                {
                    var old = core.Slots[ghost].ClientId;
                    Debug.Log($"[gs] slot {ghost}: client={clientId} replaces the stale client={old}");
                    OnClientDisconnected(old, ghost);
                    try { _nm.DisconnectClient(old, "You joined again from another tab or network."); } catch (Exception e) { Debug.LogWarning("[gs] drop stale: " + e.Message); }
                }
            }

            if (Director.InMatch)
            {
                var slot = hello.Token != null ? Director.FindResumeSlot(hello.Token) : -1;
                if (slot >= 0)
                {
                    core.Attach(slot, clientId);
                    Director.OnHumanResumed(slot);
                    _sim.OnSlotAttached(slot);
                    Debug.Log($"[gs] resume slot {slot} → client={clientId} (grace {(Director.Roster[slot].GraceExpired ? "EXPIRED: spectates, DISCONNECT at round end" : "ok")})");
                }
                else
                {
                    _viewers.Add(clientId);
                    _viewerHello[clientId] = (hello.Name, hello.Token, hello.CosmeticPreset, hello.SkillTier);
                    Debug.Log($"[gs] client={clientId} watches (match in progress; no late joiners) viewers={_viewers.Count}");
                }
            }
            else
            {
                if (!Seat(clientId, hello.Name, hello.Token, hello.CosmeticPreset, hello.SkillTier))
                {
                    if (_viewers.Contains(clientId))
                        SendFullState(clientId);
                    return;
                }
                if (!string.IsNullOrEmpty(hello.ShellAsset))
                    ShellCheck.Begin(clientId, hello.ShellAsset, hello.ShellWallet);
            }

            SendFullState(clientId);
        }

        private bool Seat(ulong clientId, string name, byte[] token, byte preset = 0, byte tier = 1)
        {
            var core = _sim.Core;
            var slot = core.HumanCount < Protocol.MaxPlayers ? core.AddHuman(clientId) : -1;
            if (slot < 0)
            {
                // beta audit: every seat has a human: watch this match and take a seat in the next lobby (the server
                // admits a few more connections than seats for this) instead of being kicked
                if (!_viewers.Contains(clientId))
                {
                    _viewers.Add(clientId);
                    _viewerHello[clientId] = (name, token, preset, tier);
                    Debug.Log($"[gs] client={clientId}: lobby full, watches (viewers={_viewers.Count})");
                }

                return false;
            }

            // A token the client already holds (it stays valid while connected) is kept; otherwise a new one.
            var t = token != null && token.Length == 16 && !AllZero(token) ? token : NewToken();
            Director.OnHumanJoined(slot, t, name, preset, tier);
            _sim.OnSlotAttached(slot);
            Debug.Log($"[gs] slot {slot} → client={clientId} name=\"{Director.Names[slot]}\" humans={core.HumanCount} bots={core.BotCount}");
            return true;
        }

        private void SeatViewers()
        {
            var ids = new List<ulong>(_viewers);
            foreach (var id in ids)
            {
                _viewers.Remove(id);
                _viewerHello.TryGetValue(id, out var h);
                if (_sim.Core.HumanCount >= Protocol.MaxPlayers)
                    break; // the rest keep watching (lobby full)
                _viewerHello.Remove(id);
                if (Seat(id, h.name, h.token, h.preset, h.tier))
                    SendFullState(id);
            }
        }

        private static bool AllZero(byte[] b)
        {
            foreach (var x in b)
                if (x != 0)
                    return false;
            return true;
        }

        private byte[] NewToken()
        {
            var t = new byte[16];
            _rng.GetBytes(t);
            return t;
        }

        /// <summary>Returns true if the slot was freed (lobby), false if it was detached (in a match: 20 s grace).</summary>
        public void OnClientDisconnected(ulong clientId, int slot)
        {
            _rxAt.Remove(clientId);
            if (_viewers.Remove(clientId))
            {
                _viewerHello.Remove(clientId);
                return;
            }

            if (slot < 0)
                return;
            if (Director.OnHumanLeft(slot))
            {
                _sim.Core.Remove(slot);
                Debug.Log($"[gs] slot {slot} freed (lobby) humans={_sim.Core.HumanCount}");
            }
            else
            {
                _sim.Core.Detach(slot);
                Debug.Log($"[gs] slot {slot} detached (client={clientId}): {MatchDirector.GraceTicks / 30} s grace, Rushling on neutral input");
            }
        }

        public bool IsViewer(ulong clientId) => _viewers.Contains(clientId);
        public IEnumerable<ulong> Viewers => _viewers;

        private IEnumerable<ulong> Recipients()
        {
            foreach (var id in _nm.ConnectedClientsIds)
                if (id != NetworkManager.ServerClientId)
                    yield return id;
        }

        // =====================================================================
        // Incoming
        // =====================================================================

        private void OnReady(ulong sender, FastBufferReader reader)
        {
            var len = reader.Length - reader.Position;
            if (len != ReadyMessage.Size)
                return;
            var buf = _buf;
            reader.ReadBytesSafe(ref buf, len);
            if (!ReadyMessage.TryDecode(new ReadOnlySpan<byte>(buf, 0, len), out var m))
                return;
            var slot = _sim.Core.FindSlot(sender);
            if (slot < 0)
                return;
            if (m.MapHashPrefix != ReadyMessage.Prefix(_sim.MapHash))
            {
                Debug.LogWarning($"[gs] ready from slot {slot} with a different map hash ({m.MapHashPrefix:x8}); ignored");
                return;
            }

            Director.MarkReady(slot, m.LoadSerial);
        }

        private void OnControl(ulong sender, FastBufferReader reader)
        {
            var len = reader.Length - reader.Position;
            if (len != ControlCodec.Size)
                return;
            var buf = _buf;
            reader.ReadBytesSafe(ref buf, len);
            if (!ControlCodec.TryDecode(new ReadOnlySpan<byte>(buf, 0, len), out var cmd, out var arg))
                return;
            var slot = _sim.Core.FindSlot(sender);
            switch (cmd)
            {
                case ControlCmd.Start:
                    if (Director.HostStart(slot))
                        Debug.Log($"[gs] host (slot {slot}) started the match");
                    break;
                case ControlCmd.Back:
                    Director.Back(slot);
                    break;
                case ControlCmd.RxResend:
                    // beta audit: at most one full reactive resend per client per second
                    var nowRx = Time.realtimeSinceStartup;
                    if (_rxAt.TryGetValue(sender, out var lastRx) && nowRx - lastRx < 1f)
                        break;
                    _rxAt[sender] = nowRx;
                    _sim.SendReactiveFull(sender);
                    break;
                case ControlCmd.Emote:
                    RelayEmote(slot, (int)arg);
                    break;
            }
        }

        private readonly uint[] _emoteNext = new uint[Protocol.MaxPlayers];

        /// <summary>G1: relays an emote to every recipient (rate-limited to one per second per slot; outside round play only).</summary>
        private void RelayEmote(int slot, int id)
        {
            if (slot < 0 || slot >= Protocol.MaxPlayers || id < 1 || id > EmoteCodec.MaxId || !EmoteCodec.PhaseAllows(Director.Phase))
                return;
            var now = _sim.Core.Tick;
            if (now < _emoteNext[slot])
                return;
            _emoteNext[slot] = now + 30;
            var n = EmoteCodec.Encode(_buf, slot, id);
            long b = 0, c = 0;
            foreach (var to in Recipients())
                Send(Protocol.Msg.Emote, to, n, ref b, ref c);
        }

        // =====================================================================
        // Outgoing
        // =====================================================================

        private void Send(string msg, ulong id, int n, ref long bytes, ref long count)
        {
            using var w = new FastBufferWriter(n, Allocator.Temp);
            w.WriteBytesSafe(_buf, n);
            _nm.CustomMessagingManager.SendNamedMessage(msg, id, w, NetworkDelivery.ReliableSequenced);
            bytes += n;
            count++;
        }

        public void SendEvent(in RaceEvent e)
        {
            var n = e.Encode(_buf);
            foreach (var id in Recipients())
                Send(Protocol.Msg.Event, id, n, ref EvtBytes, ref EvtMsgs);
        }

        public RaceStateMessage StateMessage()
        {
            var rules = Director.Rules;
            var m = rules != null ? rules.StateMessage() : new RaceStateMessage { Phase = RoundPhase.Waiting, FinisherSlots = Array.Empty<byte>(), FinisherTicks = Array.Empty<uint>() };
            m.LastEventSeq = _evtSeq;
            return m;
        }

        public void SendState(ulong clientId)
        {
            var m = StateMessage();
            var n = m.Encode(_buf);
            long c = 0;
            Send(Protocol.Msg.RaceState, clientId, n, ref StateBytes, ref c);
        }

        public void BroadcastState()
        {
            foreach (var id in Recipients())
                SendState(id);
        }

        private void BroadcastMatch()
        {
            foreach (var id in Recipients())
                SendMatch(id);
        }

        public void SendMatch(ulong clientId)
        {
            var m = BuildMatchMessage(_sim.Core.FindSlot(clientId));
            var n = m.Encode(_buf);
            Send(Protocol.Msg.Match, clientId, n, ref MatchBytes, ref MatchMsgs);
        }

        /// <summary>Join / reconnect: everything needed to show the right screen without any earlier message.</summary>
        public void SendFullState(ulong clientId)
        {
            SendMatch(clientId);
            foreach (var e in Director.Events)
            {
                var n = MatchEventCodec.Encode(_buf, in e);
                Send(Protocol.Msg.MatchEvent, clientId, n, ref MevtBytes, ref MevtMsgs);
            }

            _sim.SendRound(clientId);
            SendState(clientId);
            _sim.SendReactiveFull(clientId);
        }

        public MatchStateMessage BuildMatchMessage(int ownSlot)
        {
            var d = Director;
            var core = _sim.Core;
            var m = new MatchStateMessage
            {
                Phase = d.Phase, PhaseStartTick = d.PhaseStartTick, PhaseEndTick = d.PhaseEndTick, ServerTick = core.Tick,
                Round = (byte)d.Round, RoundCount = (byte)d.RoundCount, LoadSerial = d.LoadSerial,
                Flags = (byte)(MatchStateMessage.FlagPractice | (d.Soak ? MatchStateMessage.FlagSoak : 0) | (d.Loading ? MatchStateMessage.FlagLoading : 0) | (d.PrivateRoom ? MatchStateMessage.FlagPrivate : 0)),
                HostSlot = d.HostSlot >= 0 ? (byte)d.HostSlot : (byte)255,
                OwnSlot = ownSlot >= 0 ? (byte)ownSlot : (byte)255,
                Champion = d.Champion >= 0 ? (byte)d.Champion : (byte)255,
                PlayerCount = (byte)d.PlayerCount,
                MatchId = d.MatchId ?? new byte[16], Seed = d.MatchSeed ?? new byte[32], Transcript = d.TranscriptHash ?? new byte[32],
                EventCount = (uint)d.Events.Count,
                Maps = d.MatchId != null ? d.PlanMaps : new byte[4],
                Variants = d.MatchId != null ? d.DealResult.Variants : new byte[4],
                Modifiers = d.MatchId != null ? d.DealResult.Modifiers : new byte[4],
                Targets = d.Targets ?? new byte[4],
                Token = ownSlot >= 0 && d.Tokens[ownSlot] != null ? d.Tokens[ownSlot] : new byte[16],
                MatchSerial = (byte)d.MatchSerial,
            };
            var rows = new List<MatchSlotInfo>();
            for (var i = 0; i < MatchDirector.MaxSlots; i++)
            {
                ref readonly var r = ref d.Roster[i];
                ref readonly var s = ref core.Slots[i];
                if (!r.Occupied && !s.Active)
                    continue;
                byte f = 0;
                if (s.Active) f |= MatchSlotInfo.Active;
                if (s.Bot || r.Bot) f |= MatchSlotInfo.Bot;
                if (r.Occupied ? r.Alive : s.Active) f |= MatchSlotInfo.Alive;
                if (s.Bot || (s.Active && !s.Detached)) f |= MatchSlotInfo.Connected;
                if (r.GraceExpired) f |= MatchSlotInfo.GraceExpired;
                if (i == d.HostSlot) f |= MatchSlotInfo.Host;
                if (d.IsReady(i)) f |= MatchSlotInfo.Ready;
                if (r.Occupied) f |= MatchSlotInfo.Rostered;
                byte late = 0;
                if (r.Late != null)
                    for (var k = 0; k < 4; k++)
                        if (r.Late[k])
                            late |= (byte)(1 << k);
                rows.Add(new MatchSlotInfo
                {
                    Slot = (byte)i, Flags = f, Tier = r.Tier, Reason = r.Reason, EliminatedRound = r.EliminatedRound,
                    Places = r.Places ?? new byte[4], LateMask = late,
                    Preset = r.Occupied ? r.Preset : s.Bot ? CosmeticPresets.ForBot(i) : d.Presets[i],
                    Name = r.Occupied ? r.Name : s.Bot ? MatchDirector.BotNames[i % MatchDirector.BotNames.Length] : d.Names[i] ?? $"Player {i + 1}",
                });
            }

            m.Slots = rows.ToArray();
            return m;
        }

        // =====================================================================
        // Event log (jsonl, M5 outbox input)
        // =====================================================================

        private void OpenLog()
        {
            _loggedSerial = Director.MatchSerial;
            _log?.Dispose();
            _log = null;
            try
            {
                Directory.CreateDirectory(LogDir);
                var d = Director;
                var path = Path.Combine(LogDir, $"match-{Sha256.Hex(d.MatchId)}.jsonl");
                _log = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
                var roster = new StringBuilder();
                for (var i = 0; i < d.PlayerCount; i++)
                {
                    ref readonly var r = ref d.Roster[i];
                    if (i > 0) roster.Append(',');
                    roster.Append("{\"slot\":").Append(i).Append(",\"bot\":").Append(r.Bot ? "true" : "false")
                        .Append(",\"userId\":\"").Append(RosterHash.PlaceholderUserId(i, r.Bot)).Append("\",\"name\":\"").Append(Escape(r.Name))
                        .Append("\",\"tier\":\"").Append(r.Tier).Append("\"}");
                }

                _log.WriteLine("{\"type\":\"match\",\"version\":1,\"matchId\":\"" + Sha256.Hex(d.MatchId) + "\",\"serial\":" + d.MatchSerial +
                               ",\"practice\":true," + (d.JudgeMatch ? "\"judge\":true," : "") + "\"seed\":\"" + Sha256.Hex(d.MatchSeed) + "\",\"seedSource\":\"local\",\"rosterHash\":\"" + Sha256.Hex(d.RosterHashBytes) +
                               "\",\"transcriptInit\":\"" + Sha256.Hex(d.TranscriptInit) + "\",\"players\":" + d.PlayerCount +
                               ",\"targets\":[" + string.Join(",", d.Targets) + "],\"deal\":{\"maps\":[" + string.Join(",", d.DealResult.Maps) + "],\"variants\":[" +
                               string.Join(",", d.DealResult.Variants) + "],\"modifiers\":[" + string.Join(",", d.DealResult.Modifiers) + "]},\"plan\":[" +
                               string.Join(",", d.PlanMaps.AsSpan(0, d.RoundCount).ToArray()) + "],\"roster\":[" + roster + "],\"startTick\":" + d.MatchStartTick +
                               ",\"utc\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\"}");
                Debug.Log($"[gs] match {d.MatchSerial} id={Sha256.Hex(d.MatchId).Substring(0, 16)}… log → {path}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[gs] event log unavailable: {ex.Message}");
            }
        }

        private static string Escape(string s) => (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        private void WriteLog(in MatchEvent e)
        {
            if (_log == null && Director.MatchId != null)
                OpenLog();
            _log?.WriteLine("{\"type\":\"event\",\"seq\":" + e.Seq + ",\"tag\":" + e.Tag + ",\"name\":\"" + Transcript.TagName(e.Tag) + "\",\"round\":" + e.Round +
                            ",\"slot\":" + e.Slot + ",\"arg\":\"" + Sha256.Hex(e.Arg ?? Array.Empty<byte>()) + "\",\"tick\":" + e.Tick + ",\"transcript\":\"" + Sha256.Hex(e.TranscriptAfter) + "\"}");
        }

        private void OnMatchEnded(in MatchEvent e)
        {
            var d = Director;
            var dur = (d.Tick - d.MatchStartTick) / 30.0;
            _sim.StepMs.Percentiles(0.5f, 0.95f, out var s50, out var s95);
            var rssMb = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1048576.0;
            var monoMb = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / 1048576.0;
            var unityMb = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576.0;
            var summary = $"match {d.MatchSerial} {(e.Tag == Transcript.CompleteMatch ? "COMPLETE" : "ABORT")} champion={(e.Tag == Transcript.CompleteMatch ? e.Slot.ToString() : "-")} rounds={d.Results.Count} duration={dur:F1}s events={d.Events.Count} transcript={Sha256.Hex(d.TranscriptHash)} simP50={s50:F3}ms simP95={s95:F3}ms rss={rssMb:F0}MB mono={monoMb:F1}MB unity={unityMb:F0}MB";
            MatchSummaries.Add(summary);
            _log?.WriteLine("{\"type\":\"end\",\"result\":\"" + (e.Tag == Transcript.CompleteMatch ? "complete" : "abort") + "\",\"champion\":" + (e.Tag == Transcript.CompleteMatch ? e.Slot : 255) +
                            ",\"rounds\":" + d.Results.Count + ",\"events\":" + d.Events.Count + ",\"durationTicks\":" + (d.Tick - d.MatchStartTick) + ",\"transcript\":\"" + Sha256.Hex(d.TranscriptHash) + "\"}");
            _log?.Flush();
            Debug.Log($"[gs] {summary}");
        }
    }
}
