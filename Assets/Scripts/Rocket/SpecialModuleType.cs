public enum SpecialModuleType
{
    RegularModule,
    Fairing,                // Payload fairing, allows deploying payload (reducing mass) and protects it aerodynamically
    Parachute,              // Deployable parachute, slows down the rocket when deployed inside atmosphere
    Decoupler,              // Detachable module, allows dropping the attached modules and reducing mass
    HeatShield,             // Protects the payload from heating during atmospheric reentry
    AdvancedTelemetry,      // Provides more detailed telemetry data for the rocket flight
                            // Basic telemetry is altitude, velocity, and acceleration. Advanced telemetry adds pitch, roll, yaw, and angular velocity.
                            // Advanced telemetry allows seeing the trajectory in the map view, and seeing the rocket's orientation in the map view.
    AdvancedCommunication,  // Allows transmission of data, whilst basic commands are always available.
    PositioningSystem,      // Allows the rocket to know its position in the world, and allows the player to see the rocket's position in the map view.
                            // Lowers chance of loosing rocket in the map view, and allows the player to see the rocket's position in the map view.
}