// Hands the built rocket from the builder to the gameplay scene. Null means the gameplay scene runs on its own, with fallbacks.
public static class MissionSession
{
    public static RocketData Rocket;
    public static PlanetData Planet;
    public static MissionData Mission;  // null = free flight, no objectives
    public static int Seed = 12345;     // seeds the disturbances and the experiment altitude, so a flight can be replayed
    public static PartNode RocketTree, PayloadTree;  // the design, kept across scene changes so the builder can be returned to
}
