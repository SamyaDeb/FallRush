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
                default:
                    return new LobbyRules();
            }
        }
    }
}
