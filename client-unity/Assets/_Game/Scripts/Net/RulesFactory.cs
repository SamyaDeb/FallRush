using FallRush.Shared;

namespace FallRush.Net
{
    /// <summary>Round rules for a loaded map, by its <see cref="MapDefinition.mode"/> (M4): Race, Survival, Skill, Final, Lobby.</summary>
    public static class RulesFactory
    {
        public static IRoundRules Create(MapInfo map, byte mapId)
        {
            if (mapId == MapPoolV1.Lobby || map == null || map.definition == null)
                return new LobbyRules();
            var def = map.definition;
            var timer = (uint)(def.timerCapSeconds * 30);
            switch (def.mode)
            {
                case "Race":
                    return new RaceRules(map.CheckpointDefs(), map.CourseLineMm(), timer);
                case "Survival":
                {
                    var cps = map.CheckpointDefs();
                    // the summit pad = the map's last "checkpoint"; OC-51: falls respawn on the wide route points
                    return new SurvivalRules(cps.Length > 0 ? cps[cps.Length - 1] : default, timer, RespawnSpots(map.botRoutes));
                }
                case "Skill":
                    return new SkillRules(timer);
                case "Final":
                    return new FinalRules(timer);
                default:
                    return new LobbyRules();
            }
        }

        /// <summary>
        /// OC-51: Survival respawn spots (x, y, z, yaw256 quadruples, mm) from the bot routes: the wide "run" points (radius
        /// ≥ 1 m: Goo Tower's corner landings between the sides), facing the next point of the route; duplicates within
        /// 0.5 m dropped. Server only, like the routes (not part of the map content hash).
        /// </summary>
        public static int[] RespawnSpots(BotRoute[] routes)
        {
            var r = new System.Collections.Generic.List<int>();
            if (routes == null)
                return r.ToArray();
            foreach (var route in routes)
            {
                var pts = route.Points;
                if (pts == null)
                    continue;
                for (var i = 0; i + 1 < pts.Length; i++)
                {
                    var p = pts[i];
                    if (p.Action != WaypointAction.Run || p.RadiusMm < 1000)
                        continue;
                    var dup = false;
                    for (var k = 0; k < r.Count && !dup; k += 4)
                    {
                        long dx = r[k] - p.XMm, dy = r[k + 1] - p.YMm, dz = r[k + 2] - p.ZMm;
                        dup = dx * dx + dz * dz < 500L * 500L && System.Math.Abs(dy) < 500;
                    }

                    if (dup)
                        continue;
                    var n = pts[i + 1];
                    var yaw = UnityEngine.Mathf.Atan2(n.XMm - p.XMm, n.ZMm - p.ZMm) * UnityEngine.Mathf.Rad2Deg;
                    r.Add(p.XMm);
                    r.Add(p.YMm);
                    r.Add(p.ZMm);
                    r.Add(UnityEngine.Mathf.RoundToInt(UnityEngine.Mathf.Repeat(yaw, 360f) / 360f * 256f) & 255);
                }
            }

            return r.ToArray();
        }
    }
}
