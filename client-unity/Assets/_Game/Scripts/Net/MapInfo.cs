using System.Collections.Generic;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// Per-map scene data both sides read from the loaded scene: the <see cref="MapDefinition"/>, 12 spawn points, the
    /// ordered checkpoints, the course polyline and every hazard authoring component (in a fixed order, the same on both
    /// sides). <see cref="BuildHazards"/> resolves them for a round; <see cref="BuildContent"/> feeds the map content hash.
    /// </summary>
    public sealed class MapInfo : MonoBehaviour
    {
        public MapDefinition definition;
        public Transform[] spawns = new Transform[Protocol.MaxPlayers];
        public Vector3 botAreaCenter = Vector3.zero;
        public Vector2 botAreaHalfExtents = new Vector2(10f, 10f);

        [Tooltip("Client-only placeholder material for player capsules (URP Lit; colour set per slot with a property block).")]
        public Material playerMaterial;

        [Tooltip("Client-only unlit material (URP Unlit) for the Rushling screen face and effects.")]
        public Material unlitMaterial;

        public HazardSweeper[] sweepers = new HazardSweeper[0];
        public HazardPlatform[] platforms = new HazardPlatform[0];
        public HazardConveyor[] conveyors = new HazardConveyor[0];
        public HazardSpringPad[] pads = new HazardSpringPad[0];
        public HazardTurnTable[] turnTables = new HazardTurnTable[0];
        public HazardGateDoor[] doors = new HazardGateDoor[0];
        public HazardMallet[] mallets = new HazardMallet[0];
        public HazardBumper[] bumpers = new HazardBumper[0];

        [Tooltip("Ordered checkpoints (race maps); the last is the finish.")]
        public RaceCheckpoint[] checkpoints = new RaceCheckpoint[0];
        [Tooltip("Course centre line from the start to the finish (race ranking distance and bots).")]
        public Vector3[] courseLine = new Vector3[0];

        [Tooltip("Waypoint bot routes (server only; two or more per section). Not part of the map content hash.")]
        public BotRoute[] botRoutes = new BotRoute[0];

        public static MapInfo Current { get; private set; }

        private void OnEnable() => Current = this;

        private void OnDisable()
        {
            if (Current == this)
                Current = null;
        }

        public byte MapId => definition != null ? definition.mapId : MapPoolV1.Sandbox;

        public int KillYMm => Mathf.RoundToInt((definition != null ? definition.killY : MovementConfig.Default.KillY) * 1000f);

        /// <summary>The colliders the motor world poses / maps to surface ids, in <see cref="HazardSet"/> order.</summary>
        public sealed class Colliders
        {
            public Collider[] Platforms = new Collider[0];
            public Collider[] TurnTables = new Collider[0];
            public Collider[] Conveyors = new Collider[0];
            public Collider[] Pads = new Collider[0];
            public Collider[] Doors = new Collider[0];
            public Collider[] Tiles = new Collider[0];
            public Collider[] Blocks = new Collider[0];
        }

        /// <summary>M2 form: variant A, no modifier, M2 phases (tests and tools).</summary>
        public HazardSet BuildHazards(out Collider[] platformColliders)
        {
            var h = BuildHazards(default, out var cols);
            platformColliders = cols.Platforms;
            return h;
        }

        /// <summary>The map's hazards resolved for <paramref name="p"/> (variant, seed, modifier, round start), same order on both sides.</summary>
        public HazardSet BuildHazards(in RoundParams p, out Colliders cols, bool zeroPhase = false)
        {
            var q = p;
            cols = new Colliders();
            var sw = new SweeperDef[Len(sweepers)];
            for (var i = 0; i < sw.Length; i++)
                sw[i] = sweepers[i].Resolve(in q, zeroPhase);
            var pl = new PlatformDef[Len(platforms)];
            cols.Platforms = new Collider[pl.Length];
            for (var i = 0; i < pl.Length; i++)
            {
                pl[i] = platforms[i].Resolve(in q, zeroPhase);
                cols.Platforms[i] = platforms[i].motorCollider;
            }

            var cv = new ConveyorDef[Len(conveyors)];
            cols.Conveyors = new Collider[cv.Length];
            for (var i = 0; i < cv.Length; i++)
            {
                cv[i] = conveyors[i].Resolve(in q, zeroPhase);
                cols.Conveyors[i] = conveyors[i].motorCollider;
            }

            var pd = new SpringPadDef[Len(pads)];
            cols.Pads = new Collider[pd.Length];
            for (var i = 0; i < pd.Length; i++)
            {
                pd[i] = pads[i].Resolve(in q, zeroPhase);
                cols.Pads[i] = pads[i].motorCollider;
            }

            var tt = new TurnTableDef[Len(turnTables)];
            cols.TurnTables = new Collider[tt.Length];
            for (var i = 0; i < tt.Length; i++)
            {
                tt[i] = turnTables[i].Resolve(in q, zeroPhase);
                cols.TurnTables[i] = turnTables[i].motorCollider;
            }

            var dr = new GateDoorDef[Len(doors)];
            cols.Doors = new Collider[dr.Length];
            for (var i = 0; i < dr.Length; i++)
            {
                dr[i] = doors[i].Resolve(in q, zeroPhase);
                cols.Doors[i] = doors[i].motorCollider;
            }

            var ml = new MalletDef[Len(mallets)];
            for (var i = 0; i < ml.Length; i++)
                ml[i] = mallets[i].Resolve(in q, zeroPhase);
            var bm = new BumperDef[Len(bumpers)];
            for (var i = 0; i < bm.Length; i++)
                bm[i] = bumpers[i].Resolve(in q, zeroPhase);

            return new HazardSet(sw, pl, cv, pd, tt, dr, ml, bm) { RoundStartTick = p.RoundStartTick, KillYMm = definition != null ? KillYMm : int.MinValue };
        }

        private static int Len<T>(T[] a) => a?.Length ?? 0;

        /// <summary>PhysX-backed motor world for this map (server simulation or client prediction).</summary>
        public PhysxMotorWorld CreateWorld(Transform probeParent, MovementConfig cfg, out HazardSet hazards) =>
            CreateWorld(probeParent, cfg, default, out hazards);

        public PhysxMotorWorld CreateWorld(Transform probeParent, MovementConfig cfg, in RoundParams p, out HazardSet hazards) =>
            CreateWorld(PhysxMotorWorld.CreateProbe(probeParent, cfg.Radius, cfg.Height), cfg, in p, out hazards);

        /// <summary>Same, reusing an existing penetration probe (a new round on the same map).</summary>
        public PhysxMotorWorld CreateWorld(CapsuleCollider probe, MovementConfig cfg, in RoundParams p, out HazardSet hazards)
        {
            hazards = BuildHazards(in p, out var cols);
            return new PhysxMotorWorld(gameObject.scene.GetPhysicsScene(), PhysxMotorWorld.GameplayMask(), probe, hazards, cols);
        }

        public Vec3[] SpawnPoints()
        {
            var r = new Vec3[Protocol.MaxPlayers];
            for (var i = 0; i < r.Length; i++)
            {
                var p = spawns != null && i < spawns.Length && spawns[i] != null ? spawns[i].position : new Vector3(-11f + 2f * i, 0.1f, 0f);
                r[i] = new Vec3(p.x, p.y, p.z);
            }

            return r;
        }

        /// <summary>Spawn facings (Yaw256), from each spawn transform's forward.</summary>
        public byte[] SpawnYaws()
        {
            var r = new byte[Protocol.MaxPlayers];
            for (var i = 0; i < r.Length; i++)
            {
                if (spawns == null || i >= spawns.Length || spawns[i] == null)
                    continue;
                var f = spawns[i].forward;
                r[i] = Yaw256.FromVector(Mathf.RoundToInt(f.x * 1000f), Mathf.RoundToInt(f.z * 1000f), 0);
            }

            return r;
        }

        public CheckpointDef[] CheckpointDefs()
        {
            var r = new CheckpointDef[Len(checkpoints)];
            for (var i = 0; i < r.Length; i++)
                r[i] = checkpoints[i].Resolve();
            return r;
        }

        public int[] CourseLineMm()
        {
            var r = new int[Len(courseLine) * 3];
            for (var i = 0; i < Len(courseLine); i++)
            {
                r[i * 3] = Mathf.RoundToInt(courseLine[i].x * 1000f);
                r[i * 3 + 1] = Mathf.RoundToInt(courseLine[i].y * 1000f);
                r[i * 3 + 2] = Mathf.RoundToInt(courseLine[i].z * 1000f);
            }

            return r;
        }

        /// <summary>Everything the simulation depends on, for the map content hash (§3.3): both variants, phase ranges, checkpoints, spawns, kill-Y.</summary>
        public MapContent BuildContent()
        {
            var c = new MapContent
            {
                MapId = MapId,
                Version = definition != null ? definition.version : 0,
                KillYMm = KillYMm,
            };
            var sp = SpawnPoints();
            var yaw = SpawnYaws();
            c.SpawnsMm = new int[sp.Length * 4];
            for (var i = 0; i < sp.Length; i++)
            {
                c.SpawnsMm[i * 4] = MotorState.QuantizeMm(sp[i].X);
                c.SpawnsMm[i * 4 + 1] = MotorState.QuantizeMm(sp[i].Y);
                c.SpawnsMm[i * 4 + 2] = MotorState.QuantizeMm(sp[i].Z);
                c.SpawnsMm[i * 4 + 3] = yaw[i];
            }

            c.Checkpoints = CheckpointDefs();
            var variants = definition != null ? Mathf.Max(1, definition.variants) : 2;
            c.Variants = new HazardSet[variants];
            for (var v = 0; v < variants; v++)
                c.Variants[v] = BuildHazards(new RoundParams { Variant = (byte)v }, out _, zeroPhase: true);

            var ids = new List<string>();
            var ranges = new List<int>();
            foreach (var a in AllAuthoring())
            {
                ids.Add(a.HashId);
                ranges.Add(a.phaseMin16);
                ranges.Add(a.phaseMax16);
            }

            c.ObstacleIds = ids.ToArray();
            c.PhaseRanges = ranges.ToArray();
            c.CourseLineMm = CourseLineMm();
            return c;
        }

        public IEnumerable<HazardAuthoring> AllAuthoring()
        {
            foreach (var a in sweepers) yield return a;
            foreach (var a in platforms) yield return a;
            foreach (var a in conveyors) yield return a;
            foreach (var a in pads) yield return a;
            foreach (var a in turnTables) yield return a;
            foreach (var a in doors) yield return a;
            foreach (var a in mallets) yield return a;
            foreach (var a in bumpers) yield return a;
        }

        private byte[] _hash;

        /// <summary>Map content hash of the loaded scene (cached).</summary>
        public byte[] ContentHash => _hash ??= BuildContent().Hash();

        /// <summary>The allowlist problems of the defs resolved for <paramref name="p"/> (empty = valid).</summary>
        public List<string> Validate(in RoundParams p) => HazardAllowlist.Validate(BuildHazards(in p, out _));
    }
}
