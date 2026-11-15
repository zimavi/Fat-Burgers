using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// What a module did on its last flight.
public enum FlightResult { NotFlown, Worked, Failed, Tested }

// Per-module reliability, kept between sessions.
// Every completed flight rolls each fitted module that has an effect against its reliability (seeded). A failure disables it for that flight.
// Completed flights raise the reliability of every fitted module (passively, failed ones included), and a passed test raises it more.
// Reliability is capped below 100%, so a module can never be made fully reliable.
public static class ReliabilityStore
{
    public const float Start = 70f;         // %, for a module never flown
    public const float Max = 99f;           // %, a module is never certain
    public const float PassiveGain = 0.5f;  // % per completed flight in which the module was fitted
    public const float TestGain = 5f;       // % per passed test

    // Modules with an effect in the flight. Others are not rolled.
    public static readonly SpecialModuleType[] Rolled =
    {
        SpecialModuleType.Parachute, SpecialModuleType.Decoupler, SpecialModuleType.HeatShield,
        SpecialModuleType.Fairing, SpecialModuleType.AdvancedCommunication,
    };

    struct Record { public float Percent; public FlightResult Last; public string Cause; }

    [Serializable] public struct Entry { public int type; public float percent; public int last; public string cause; }
    [Serializable] public struct SaveData { public Entry[] entries; }

    static Dictionary<SpecialModuleType, Record> records;

    static Dictionary<SpecialModuleType, Record> Records
    {
        get
        {
            if (records != null) return records;
            records = new Dictionary<SpecialModuleType, Record>();
            try
            {
                if (File.Exists(FilePath))
                {
                    var save = JsonUtility.FromJson<SaveData>(File.ReadAllText(FilePath));
                    if (save.entries != null)
                        foreach (var e in save.entries)
                            records[(SpecialModuleType)e.type] = new Record { Percent = e.percent, Last = (FlightResult)e.last, Cause = e.cause ?? "" };
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Reliability file unreadable, starting from defaults: {ex.Message}");
            }
            return records;
        }
    }

    static string FilePath => Path.Combine(Application.persistentDataPath, "reliability.json");

    static Record Of(SpecialModuleType t) => Records.TryGetValue(t, out var r) ? r : new Record { Percent = Start };

    public static float Get(SpecialModuleType t) => Of(t).Percent;

    public static FlightResult LastResult(SpecialModuleType t) => Of(t).Last;

    // Why the module failed on its last flight, or "" when it did not fail.
    public static string LastCause(SpecialModuleType t) => Of(t).Cause ?? "";

    // "HeatShield" -> "heat shield", for sentences.
    public static string DisplayName(SpecialModuleType t) =>
        System.Text.RegularExpressions.Regex.Replace(t.ToString(), "(?<!^)([A-Z])", " $1").ToLower();

    // Short phrase for the module tooltip: what the last flight did, and the cause if it failed.
    public static string LastText(SpecialModuleType t) => LastResult(t) switch
    {
        FlightResult.Worked => "worked",
        FlightResult.Failed => $"failed ({LastCause(t)})",
        FlightResult.Tested => "passed its test",
        _ => "not flown yet",
    };

    // Updates every fitted module with an effect after a completed flight, then saves.
    // tested: the module whose test was passed on this flight, or null. seed picks the cause of any failure.
    // Returns one line per module for the result screen.
    public static string RecordFlight(RocketData rocket, int failed, SpecialModuleType? tested, int seed)
    {
        var lines = new List<string>();
        foreach (var t in Rolled)
        {
            if (!rocket.Has(t)) continue;
            var before = Of(t);
            var now = before;
            bool failedHere = (failed & (1 << (int)t)) != 0;
            now.Percent = Mathf.Min(Max, now.Percent + PassiveGain);  // every flight teaches the module something, failures included
            if (failedHere)
            {
                now.Last = FlightResult.Failed;
                now.Cause = FailureModes.Cause(t, seed);
            }
            else
            {
                now.Last = FlightResult.Worked;
                now.Cause = "";
            }
            if (!failedHere && tested == t)
            {
                now.Last = FlightResult.Tested;
                now.Cause = "";
                now.Percent = Mathf.Min(Max, now.Percent + TestGain);
            }
            Records[t] = now;
            lines.Add($"{t} reliability {before.Percent:0.0}% -> {now.Percent:0.0}%");
        }
        Save();
        return string.Join("    ", lines);
    }

    static void Save()
    {
        var entries = new List<Entry>();
        foreach (var kv in Records)
            entries.Add(new Entry { type = (int)kv.Key, percent = kv.Value.Percent, last = (int)kv.Value.Last, cause = kv.Value.Cause ?? "" });
        try
        {
            File.WriteAllText(FilePath, JsonUtility.ToJson(new SaveData { entries = entries.ToArray() }));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Could not save reliability: {ex.Message}");
        }
    }

    // Seeded: the same seed and reliability give the same failures. Returns a mask of (1 << SpecialModuleType) bits.
    public static int RollFailures(RocketData rocket, int seed)
    {
        var rng = new System.Random(seed + 2);
        int failed = 0;
        foreach (var t in Rolled)
        {
            double roll = rng.NextDouble();  // drawn for every module, so fitting one does not change the others' rolls
            if (rocket.Has(t) && roll * 100.0 >= Get(t)) failed |= 1 << (int)t;
        }
        return failed;
    }

    // Failures can only hit modules that are fitted.
    public static bool SelfCheck()
    {
        var r = ScriptableObject.CreateInstance<RocketData>();
        r.specialMask = 1 << (int)SpecialModuleType.Parachute;
        bool ok = true;
        for (int seed = 1; seed <= 200; seed++)
            ok &= (RollFailures(r, seed) & ~r.specialMask) == 0;
        UnityEngine.Object.DestroyImmediate(r);
        return ok;
    }
}
