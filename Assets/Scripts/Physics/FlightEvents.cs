using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

public enum FlightEventType { MaxQ, MaxHeating, MECO, Apogee, Boundary100km, Impact }

public struct FlightEvent
{
    public FlightEventType Type;
    public float Time, Altitude, Downrange, Speed, VerticalVel, Mass, Pitch, Q, Heating;  // SI
}

// Detects events from baked samples. Crossings are linearly interpolated between the two samples around them.
// Max Q and max heating are taken at the sample with the peak value (0.01 s resolution).
public static class FlightEvents
{
    public const float BoundaryAltitude = 100000f;  // m, gameplay/reference threshold

    public static string Name(FlightEventType t) => t switch
    {
        FlightEventType.MaxQ => "Max Q",
        FlightEventType.MaxHeating => "Max Heating",
        FlightEventType.MECO => "MECO",
        FlightEventType.Apogee => "Apogee",
        FlightEventType.Boundary100km => "100 km Boundary",
        FlightEventType.Impact => "Impact",
        _ => t.ToString(),
    };

    public static string Description(FlightEventType t) => t switch
    {
        FlightEventType.MaxQ => "Peak dynamic pressure: the highest aerodynamic load on the airframe. Structure and payload must survive it.",
        FlightEventType.MaxHeating => "Peak aerodynamic heating (relative index). Sets the thermal demand on the nose and fairing.",
        FlightEventType.MECO => "Propellant is spent and thrust stops. From here the vehicle coasts under gravity and drag, and speed falls.",
        FlightEventType.Apogee => "Highest point of the flight. Vertical speed is zero here, so this is where the vehicle stops climbing.",
        FlightEventType.Boundary100km => "Climbing through 100 km, the usual reference for 'space'. A gameplay line, not an atmospheric edge.",
        FlightEventType.Impact => "The vehicle reaches the surface and the flight ends. Downrange here is where it lands.",
        _ => "",
    };

    // Telemetry at the event, formatted for the tooltip.
    public static string Details(in FlightEvent e) =>
        $"T+{e.Time:0.0} s   Altitude: {e.Altitude / 1000f:0.0} km   Downrange: {e.Downrange / 1000f:0.0} km\n" +
        $"Speed: {e.Speed:0} m/s   Vertical: {e.VerticalVel:0} m/s   Pitch: {math.degrees(e.Pitch):0}°\n" +
        $"Q: {e.Q / 1000f:0.0} kPa   Heating: {e.Heating:0.0e0}   Mass: {e.Mass:0.0} kg";

    public static List<FlightEvent> Detect(NativeList<TrajectorySample> s)
    {
        var events = new List<FlightEvent>();
        int n = s.Length;
        if (n < 2) return events;

        int iq = 0, ih = 0;
        for (int i = 1; i < n; i++)
        {
            if (s[i].Q > s[iq].Q) iq = i;
            if (s[i].Heating > s[ih].Heating) ih = i;
        }
        events.Add(Make(s[iq], s[iq], 0f, FlightEventType.MaxQ));
        events.Add(Make(s[ih], s[ih], 0f, FlightEventType.MaxHeating));

        for (int i = 1; i < n; i++)
            if (s[i - 1].ThrustOn && !s[i].ThrustOn) { events.Add(Make(s[i], s[i], 0f, FlightEventType.MECO)); break; }

        for (int i = 1; i < n; i++)
        {
            float v0 = s[i - 1].VerticalVel, v1 = s[i].VerticalVel;
            if (v0 > 0f && v1 <= 0f) { events.Add(Make(s[i - 1], s[i], v0 / (v0 - v1), FlightEventType.Apogee)); break; }
        }

        for (int i = 1; i < n; i++)
        {
            float a0 = s[i - 1].Altitude, a1 = s[i].Altitude;
            if (a0 < BoundaryAltitude && a1 >= BoundaryAltitude)
            {
                events.Add(Make(s[i - 1], s[i], (BoundaryAltitude - a0) / (a1 - a0), FlightEventType.Boundary100km));
                break;
            }
        }

        if (n >= 2 && s[n - 2].Altitude > 0f && s[n - 1].Altitude <= 0f)
        {
            float a0 = s[n - 2].Altitude, a1 = s[n - 1].Altitude;
            events.Add(Make(s[n - 2], s[n - 1], a0 / (a0 - a1), FlightEventType.Impact));
        }

        return events;
    }

    static FlightEvent Make(in TrajectorySample a, in TrajectorySample b, float f, FlightEventType type) => new FlightEvent
    {
        Type = type,
        Time = math.lerp(a.Time, b.Time, f),
        Altitude = math.lerp(a.Altitude, b.Altitude, f),
        Downrange = math.lerp(a.Downrange, b.Downrange, f),
        Speed = math.lerp(a.Speed, b.Speed, f),
        VerticalVel = math.lerp(a.VerticalVel, b.VerticalVel, f),
        Mass = math.lerp(a.Mass, b.Mass, f),
        Pitch = math.lerp(a.Pitch, b.Pitch, f),
        Q = math.lerp(a.Q, b.Q, f),
        Heating = math.lerp(a.Heating, b.Heating, f),
    };
}
