using FallRush.Shared;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// <see cref="IMotorWorld"/> backed by PhysX scene queries on the map's <see cref="PhysicsScene"/>, <c>Gameplay</c> layer only,
    /// triggers ignored. Physics simulation is never stepped (<see cref="SimulationMode.Script"/>); only queries are used.
    /// <para>Note: Unity sweeps do not report colliders the capsule already overlaps at the start; the motor
    /// depenetrates first (<see cref="ComputePenetration"/>) so that case does not arise in normal play.</para>
    /// </summary>
    public sealed class PhysxMotorWorld : IMotorWorld
    {
        public const string GameplayLayerName = "Gameplay";

        private readonly PhysicsScene _scene;
        private readonly int _mask;
        private readonly CapsuleCollider _probe;
        private readonly Collider[] _overlaps = new Collider[16];
        private readonly HazardSet _hazards;
        private readonly Collider[] _platforms;
        private readonly Collider[] _doors;
        private readonly Collider[] _tiles;
        private readonly Collider[] _blocks;
        private bool[] _tileOn, _blockOn;
        private readonly System.Collections.Generic.Dictionary<int, int> _surfaceOf = new System.Collections.Generic.Dictionary<int, int>();
        private uint _tick;
        private bool _posed;

        /// <param name="platforms">Collider of each <see cref="HazardSet.Platforms"/> entry (same order), moved by <see cref="BeginTick"/>.</param>
        public PhysxMotorWorld(PhysicsScene scene, int layerMask, CapsuleCollider probe, HazardSet hazards = null, Collider[] platforms = null)
            : this(scene, layerMask, probe, hazards, new MapInfo.Colliders { Platforms = platforms ?? System.Array.Empty<Collider>() })
        {
        }

        /// <param name="cols">Colliders of the hazard set in <see cref="HazardSet"/> order: platforms and doors are posed per tick;
        /// platforms, turn tables, conveyors and spring pads map to surface ids.</param>
        public PhysxMotorWorld(PhysicsScene scene, int layerMask, CapsuleCollider probe, HazardSet hazards, MapInfo.Colliders cols)
        {
            _scene = scene;
            _mask = layerMask;
            _probe = probe;
            _hazards = hazards ?? HazardSet.Empty;
            cols ??= new MapInfo.Colliders();
            _platforms = cols.Platforms ?? System.Array.Empty<Collider>();
            _doors = cols.Doors ?? System.Array.Empty<Collider>();
            _tiles = cols.Tiles ?? System.Array.Empty<Collider>();
            _blocks = cols.Blocks ?? System.Array.Empty<Collider>();
            _tileOn = new bool[_tiles.Length];
            _blockOn = new bool[_blocks.Length];
            for (var i = 0; i < _tiles.Length; i++)
                _tileOn[i] = _tiles[i] != null && _tiles[i].enabled;
            for (var i = 0; i < _blocks.Length; i++)
                _blockOn[i] = _blocks[i] != null && _blocks[i].enabled;
            Check(_tiles.Length, _hazards.Grids.Length > 0 ? _hazards.Grids[0].Count : 0, "crumble tile");
            Check(_blocks.Length, _hazards.Blocks.Length, "falling block");
            Check(_platforms.Length, _hazards.Platforms.Length, "platform");
            Check(_doors.Length, _hazards.Doors.Length, "door");
            Check(cols.TurnTables?.Length ?? 0, _hazards.TurnTables.Length, "turn table");
            Check(cols.Conveyors?.Length ?? 0, _hazards.Conveyors.Length, "conveyor");
            Check(cols.Pads?.Length ?? 0, _hazards.Pads.Length, "spring pad");
            for (var i = 0; i < _platforms.Length; i++)
                Map(_platforms[i], _hazards.PlatformSurface(i));
            for (var i = 0; i < _hazards.TurnTables.Length; i++)
                Map(cols.TurnTables[i], _hazards.TurnTableSurface(i));
            for (var i = 0; i < _hazards.Conveyors.Length; i++)
                Map(cols.Conveyors[i], _hazards.ConveyorSurface(i));
            for (var i = 0; i < _hazards.Pads.Length; i++)
                Map(cols.Pads[i], _hazards.PadSurface(i));
        }

        private static void Check(int cols, int defs, string what)
        {
            if (cols != defs)
                throw new System.ArgumentException($"{cols} {what} colliders for {defs} {what} defs");
        }

        private void Map(Collider c, int surface)
        {
            if (c != null)
                _surfaceOf[c.GetInstanceID()] = surface;
        }

        public uint PosedTick => _tick;
        public HazardSet Hazards => _hazards;

        /// <summary>Poses platforms and doors at <paramref name="tick"/> (round-relative hazard time) and syncs PhysX (queries only; nothing is simulated).</summary>
        public void BeginTick(uint tick)
        {
            if (_posed && tick == _tick)
                return;
            _tick = tick;
            _posed = true;
            // M4 reactive colliders: on/off from the replicated state at this tick (the same lookup on server and client).
            var rx = _hazards.Reactive;
            if (rx != null)
            {
                for (var i = 0; i < _tiles.Length; i++)
                {
                    var on = rx.TileSolid(i, tick);
                    if (on != _tileOn[i])
                    {
                        _tileOn[i] = on;
                        _tiles[i].enabled = on;
                    }
                }

                for (var i = 0; i < _blocks.Length; i++)
                {
                    var on = rx.BlockSolid(i, tick);
                    if (on != _blockOn[i])
                    {
                        _blockOn[i] = on;
                        _blocks[i].enabled = on;
                    }
                }
            }

            if (_platforms.Length == 0 && _doors.Length == 0)
                return;
            var t = _hazards.Time(tick);
            for (var i = 0; i < _platforms.Length; i++)
            {
                var c = HazardMath.PlatformCenter(in _hazards.Platforms[i], t);
                _platforms[i].transform.position = V(c);
            }

            for (var i = 0; i < _doors.Length; i++)
            {
                _hazards.DoorCenterMm(i, tick, out var x, out var y, out var z);
                _doors[i].transform.position = new Vector3(x * 0.001f, y * 0.001f, z * 0.001f);
            }

            Physics.SyncTransforms();
        }

        public bool SurfaceDelta(int surfaceId, Vec3 feet, out Vec3 delta) => _hazards.SurfaceDelta(surfaceId, _tick, feet, out delta);

        private int SurfaceOf(Collider c) => c != null && _surfaceOf.TryGetValue(c.GetInstanceID(), out var id) ? id : -1;

        /// <summary>Creates the hidden capsule used by <see cref="Physics.ComputePenetration"/> (layer Ignore Raycast, outside the query mask).</summary>
        public static CapsuleCollider CreateProbe(Transform parent, float radius, float height)
        {
            var go = new GameObject("MotorProbe") { layer = 2 };
            go.transform.SetParent(parent, false);
            var c = go.AddComponent<CapsuleCollider>();
            c.radius = radius;
            c.height = height;
            c.direction = 1;
            c.center = new Vector3(0f, height * 0.5f, 0f);
            c.isTrigger = true;
            return c;
        }

        public static int GameplayMask()
        {
            var layer = LayerMask.NameToLayer(GameplayLayerName);
            return layer >= 0 ? 1 << layer : Physics.DefaultRaycastLayers;
        }

        private static Vector3 V(Vec3 v) => new Vector3(v.X, v.Y, v.Z);
        private static Vec3 V(Vector3 v) => new Vec3(v.x, v.y, v.z);

        public bool CapsuleSweep(Vec3 feet, float radius, float height, Vec3 dir, float maxDistance, out MotorHit hit)
        {
            var f = V(feet);
            var p1 = new Vector3(f.x, f.y + radius, f.z);
            var p2 = new Vector3(f.x, f.y + height - radius, f.z);
            if (_scene.CapsuleCast(p1, p2, radius, V(dir), out var rh, maxDistance, _mask, QueryTriggerInteraction.Ignore))
            {
                hit = new MotorHit { Distance = rh.distance, Point = V(rh.point), Normal = V(rh.normal), SurfaceId = SurfaceOf(rh.collider) };
                return true;
            }

            hit = default;
            return false;
        }

        public bool Raycast(Vec3 origin, Vec3 dir, float maxDistance, out MotorHit hit)
        {
            if (_scene.Raycast(V(origin), V(dir), out var rh, maxDistance, _mask, QueryTriggerInteraction.Ignore))
            {
                hit = new MotorHit { Distance = rh.distance, Point = V(rh.point), Normal = V(rh.normal), SurfaceId = SurfaceOf(rh.collider) };
                return true;
            }

            hit = default;
            return false;
        }

        public bool ComputePenetration(Vec3 feet, float radius, float height, out Vec3 correction)
        {
            correction = Vec3.Zero;
            var f = V(feet);
            var p1 = new Vector3(f.x, f.y + radius, f.z);
            var p2 = new Vector3(f.x, f.y + height - radius, f.z);
            var n = _scene.OverlapCapsule(p1, p2, radius, _overlaps, _mask, QueryTriggerInteraction.Ignore);
            if (n <= 0)
                return false;

            var total = Vector3.zero;
            var any = false;
            for (var i = 0; i < n; i++)
            {
                var col = _overlaps[i];
                var t = col.transform;
                if (Physics.ComputePenetration(_probe, f, Quaternion.identity, col, t.position, t.rotation, out var dir, out var dist) && dist > 0f)
                {
                    total += dir * (dist + 0.0005f);
                    any = true;
                }
            }

            correction = V(total);
            return any;
        }
    }
}
