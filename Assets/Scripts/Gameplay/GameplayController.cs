using System.Collections.Generic;
using System.Text;
using TMPro;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Mission-control gameplay on the Panel.png layout.
// Plan: the knobs set the pitch program and the planned line is re-baked. Launch freezes that line and starts LiveFlight.
// Flying: telemetry, warp, the chute altitude, and a predicted line from the current state (TrajectoryJob, refreshed every 0.5 s).
// Result: touchdown summary. GameplayBootstrap attaches this when GameplayScene loads, so the scene needs no wiring.
// Art coordinates are 1920x1080 with y from the top. The stage is shrunk to fit the window.
// shortcut: art loads through AssetDatabase, so a player build shows a blank panel until the art is moved into Resources.
// shortcut: knobs step on clicks only, with no hold-to-repeat or wheel.
public class GameplayController : MonoBehaviour
{
    enum Phase { Plan, Flying, Result }
    enum Dial { Nav, Engine, Weather, Status }

    struct Knob
    {
        public float Value, Step, Min, Max;
        public string Unit;
        public TMP_Text Readout;
    }

    // Panel art coordinates (top-left origin).
    static readonly Rect PlotBox = new Rect(298, 172, 612, 436);
    static readonly Rect ChuteBox = new Rect(1018, 180, 688, 440);
    static readonly Rect InfoBox = new Rect(578, 712, 327, 223);
    static readonly Rect DevBox = new Rect(985, 698, 235, 80);
    static readonly Rect SpeedBox = new Rect(985, 812, 173, 44);
    static readonly Rect AltBox = new Rect(985, 888, 260, 65);
    static readonly Rect NoteRest = new Rect(1330, 190, 340, 330);
    static readonly float NoteHidden = 1960f;
    static readonly float[] KnobBarY = { 295f, 440f, 585f };
    static readonly string[] KnobNames = { "TURN START", "PITCH", "DURATION" };
    static readonly Rect[] DialZones =
    {
        new Rect(1520, 690, 80, 60), new Rect(1670, 800, 80, 60), new Rect(1385, 810, 80, 60), new Rect(1525, 930, 80, 60),
    };

    static readonly Rect DialKnob = new Rect(1525, 803, 90, 90);            // the dial centre, art coordinates
    static readonly float[] DialAngle = { 90f, 0f, 180f, -90f };           // knob pointer (sprite points right) toward N, E, W, S

    const string PanelPath = "Assets/Sprites/Panel.png";
    const string NotePath = "Assets/Sprites/StickyNote.png";
    const string KnobPath = "Assets/Sprites/knob.png";
    const string InfoIconPath = "Assets/Sprites/info-icon-thick.png";
    const string GuidePath = "Assets/Sprites/flight-panel-guide-en.png";
    const int PlotML = 52, PlotMB = 28, PlotMR = 14, PlotMT = 14;  // plot margins in texture pixels
    const int PredictStride = 50;  // predicted samples are 0.01 s apart; the plot and the apogee read every 0.5 s

    static readonly Color Ink = new Color(1f, 0.9f, 0.62f);
    static readonly Color Dim = new Color(1f, 0.9f, 0.62f, 0.4f);
    static readonly Color NoteInk = new Color(0.12f, 0.08f, 0.02f);
    static readonly Color ButtonBg = new Color(0.16f, 0.1f, 0.04f, 0.95f);
    static readonly Color ActiveBg = new Color(0.5f, 0.3f, 0.08f, 1f);
    static readonly Color Clear = new Color(0f, 0f, 0f, 0f);
    static readonly Color ScreenBg = new Color(0.08f, 0.05f, 0.02f, 1f);
    static readonly Color GridColor = new Color(0.4f, 0.25f, 0.1f, 1f);
    static readonly Color BoundaryColor = new Color(1f, 0.55f, 0.2f, 1f);
    static readonly Color PlannedColor = new Color(0.9f, 0.9f, 0.95f, 1f);
    static readonly Color PredictedColor = new Color(0.4f, 0.9f, 1f, 1f);
    static readonly Color FlownColor = new Color(1f, 0.92f, 0.5f, 1f);
    static readonly Color EventColor = new Color(0.6f, 1f, 0.5f, 1f);
    static readonly Color RocketColor = Color.white;
    static readonly Color SpeedColor = new Color(0.4f, 0.9f, 1f, 1f);
    static readonly Color ShockColor = new Color(1f, 0.7f, 0.2f, 1f);
    static readonly Color UnsafeColor = new Color(1f, 0.25f, 0.2f, 1f);
    static readonly Color SuggestColor = new Color(0.5f, 1f, 0.5f, 1f);

    TMP_FontAsset font;
    Sprite noteSprite;
    RocketData rocket;
    PlanetData planet;
    RectTransform stage, chuteRect, noteRect;
    Raster plotRaster, chuteRaster;
    Labels plotLabels, chuteLabels;
    Knob[] knobs = new Knob[3];
    Image[] warpImages = new Image[3];
    Image tabZone, lamp, knobImg, tipBox;
    const int ActStage = 0, ActDrogue = 1, ActPayload = 2, ActTest = 3, ActExperiment = 4, ActTransmit = 5;
    readonly Button[] actions = new Button[6];  // the action row along the bottom of the panel, indexed by the Act constants
    Image expLamp;
    float holdTimer;               // knob hold-to-repeat
    int holdKnob = -1, holdDir;
    TMP_Text header, statusText, devText, speedText, altText, infoText, chuteText, warnText, tipText, noteText, resultText;
    GameObject resultOverlay;
    GameObject guideRoot;          // the flight panel guide, shown over the panel while open
    readonly float[] warps = { 1f, 5f, 20f };
    int warpIndex;
    Dial dial = Dial.Nav;
    float plotX0, plotX1 = 1f, plotTop = 1f;  // axes of the last drawn plot, for hover
    float tipAlt = -1f, tipImpact, tipQ;       // chute tooltip cache: altitude it was flown for, and the result

    // Planned flight: knobs -> pitch (2 keys, edited in place), and the baked line for them.
    NativeArray<float2> pitch;
    NativeList<TrajectorySample> baked, descent;  // descent: no-chute flight, for the chute graph
    TrajectoryParams bakeParams;
    List<FlightEvent> bakedEvents = new List<FlightEvent>(), flownEvents = new List<FlightEvent>();
    float chuteAlt, suggestedChute;               // m, the chute altitude in use and the solver's suggestion
    bool chuteSetByPlayer, dragChute;
    float cursorImpact, cursorQ;                  // m/s and Pa, readout for the chute altitude in use

    // Flight.
    Phase phase = Phase.Plan;
    LiveFlight live;
    MissionProgress progress;      // null in free flight
    int failedMask;                // modules that failed their roll on this flight
    string reliabilityNote = "";   // set at touchdown when a test was passed
    NativeList<TrajectorySample> predicted;
    float flightAcc, predictTimer, rebakeTimer = -1f, redrawTimer, readoutTimer;
    bool plotDirty = true;
    readonly List<Vector2> pts = new List<Vector2>();

    // Sticky note.
    bool noteOpen;
    float noteX = NoteHidden, pulse;
    string advice = "";

    void Start()
    {
        if (!LiveFlight.SelfCheck()) Debug.LogError("LiveFlight self-check failed");
        if (!AdvisorRules.SelfCheck()) Debug.LogError("AdvisorRules self-check failed");
        if (!MissionEvaluator.SelfCheck()) Debug.LogError("MissionEvaluator self-check failed");
        if (!MissionProgress.SelfCheck()) Debug.LogError("MissionProgress self-check failed");
        if (!ReliabilityStore.SelfCheck()) Debug.LogError("ReliabilityStore self-check failed");
        if (!FailureModes.SelfCheck()) Debug.LogError("FailureModes self-check failed");

        rocket = MissionSession.Rocket ? MissionSession.Rocket : FallbackRocket();
        planet = MissionSession.Planet ? MissionSession.Planet : ScriptableObject.CreateInstance<PlanetData>();  // PlanetData defaults are Earth
        font = FontFactory.GetPixelifyFont();
        pitch = new NativeArray<float2>(2, Allocator.Persistent);
        InitKnobs();
        progress = NewProgress();
        BuildEventSystem();
        BuildUi();
        RebakePlan();
    }

    void OnDestroy()
    {
        live?.Dispose();
        Free(ref baked);
        Free(ref descent);
        Free(ref predicted);
        if (pitch.IsCreated) pitch.Dispose();
        plotRaster?.Dispose();
        chuteRaster?.Dispose();
    }

    void Update()
    {
        if (rebakeTimer >= 0f)
        {
            rebakeTimer -= Time.unscaledDeltaTime;
            if (rebakeTimer < 0f && phase == Phase.Plan) RebakePlan();
        }
        if (phase == Phase.Flying) Advance();
        HandleKeys();
        if (!guideRoot.activeSelf)  // with the guide open, the panel controls are covered and do not respond
        {
            HandleKnobs();
            HandleChuteDrag();
        }
        UpdateHover();
        UpdateNote();

        redrawTimer -= Time.unscaledDeltaTime;
        if (plotDirty || (phase == Phase.Flying && redrawTimer <= 0f))
        {
            redrawTimer = 0.1f;
            plotDirty = false;
            DrawPlot();
            DrawChute();
        }

        readoutTimer -= Time.unscaledDeltaTime;
        if (readoutTimer <= 0f)
        {
            readoutTimer = 0.1f;
            UpdateReadouts();
        }
    }

    // ---- Planning -------------------------------------------------------------------------------------------------

    static RocketData FallbackRocket()
    {
#if UNITY_EDITOR
        var sample = UnityEditor.AssetDatabase.LoadAssetAtPath<RocketData>("Assets/Scripts/ScriptableObjects/SampleRocket.asset");
        if (sample) return sample;
#endif
        Debug.LogWarning("GameplayScene: no rocket from the builder, using a blank RocketData.");
        return ScriptableObject.CreateInstance<RocketData>();
    }

    void InitKnobs()
    {
        float start, deg, dur;
        var keys = rocket.pitchProgram;
        if (keys != null && keys.Length >= 2)
        {
            start = keys[0].time;
            deg = keys[1].pitchDeg;
            dur = keys[1].time - keys[0].time;
        }
        else
        {
            var solved = PitchSolver.Solve(rocket, planet, FlightEvents.BoundaryAltitude);
            start = solved.TurnStart;
            deg = solved.TurnDeg;
            dur = PitchSolver.TurnDuration;
        }
        knobs[0] = new Knob { Value = Mathf.Clamp(start, 0f, 600f), Step = 1f, Min = 0f, Max = 600f, Unit = " s" };
        knobs[1] = new Knob { Value = Mathf.Clamp(deg, 0f, 90f), Step = 1f, Min = 0f, Max = 90f, Unit = " deg" };
        knobs[2] = new Knob { Value = Mathf.Clamp(dur, 0.5f, 120f), Step = 0.5f, Min = 0.5f, Max = 120f, Unit = " s" };
    }

    void FillPitch()
    {
        pitch[0] = new float2(knobs[0].Value, math.radians(90f));
        pitch[1] = new float2(knobs[0].Value + knobs[2].Value, math.radians(knobs[1].Value));
    }

    // The planned flight for the knobs and chute setting. Frozen at Launch, after which only the live flight follows the knobs.
    void RebakePlan()
    {
        rebakeTimer = -1f;
        tipAlt = -1f;
        FillPitch();
        var p = FlightSetup.Params(rocket, planet);
        if (p.ChuteCdA > 0f)
        {
            suggestedChute = ChuteSolver.Solve(rocket, planet, pitch).Altitude;
            if (!chuteSetByPlayer) chuteAlt = suggestedChute;
        }
        p.ChuteDeployAltitude = chuteAlt;
        bakeParams = p;

        var pad = FlightSetup.Pad(p);
        Free(ref baked);
        Free(ref descent);
        baked = Bake(p, pad);
        bakedEvents = FlightEvents.Detect(baked);
        if (p.ChuteCdA > 0f)
        {
            p.ChuteCdA = 0f;  // no canopy: the descent under the drogue alone, which is what the canopy would open into
            descent = Bake(p, pad);
        }
        RefreshChuteReadout();
        plotDirty = true;
    }

    NativeList<TrajectorySample> Bake(TrajectoryParams p, TrajectoryState start)
    {
        var samples = new NativeList<TrajectorySample>(4096, Allocator.Persistent);
        new TrajectoryJob { P = p, Pitch = pitch, Start = start, Samples = samples }.Schedule().Complete();
        return samples;
    }

    static void Free<T>(ref NativeList<T> list) where T : unmanaged
    {
        if (list.IsCreated) list.Dispose();
        list = default;
    }

    void Nudge(int i, int dir)
    {
        if (phase == Phase.Result) return;
        bool flying = phase == Phase.Flying;
        if (flying && live.Time >= knobs[0].Value) return;  // the turn has begun: the program is locked
        float v = Mathf.Clamp(knobs[i].Value + dir * knobs[i].Step, knobs[i].Min, knobs[i].Max);
        if (flying && i == 0) v = Mathf.Max(v, live.Time);  // a turn can still be set, but not in the past
        knobs[i].Value = v;
        plotDirty = true;
        if (flying)
        {
            FillPitch();
            RefreshPrediction();
        }
        else rebakeTimer = 0.1f;
    }

    void SetChute(float altitude)
    {
        if (phase == Phase.Result) return;
        chuteAlt = altitude;
        chuteSetByPlayer = true;
        plotDirty = true;
        if (phase == Phase.Plan)
        {
            rebakeTimer = 0.1f;
            RefreshChuteReadout();
        }
        else
        {
            live.SetChuteAltitude(altitude);
            RefreshPrediction();
        }
    }

    // Impact speed and opening shock for the chute altitude in use. Flown from the live state when flying.
    void RefreshChuteReadout()
    {
        if (rocket.chuteCdA <= 0f) return;
        if (live != null && (live.State.Flags & FlightFlags.ChuteOn) != 0) return;
        (cursorImpact, cursorQ) = ChuteOutcome(chuteAlt);
    }

    // Landing speed and opening shock if the canopy opened at this altitude.
    (float impact, float shock) ChuteOutcome(float altitude)
    {
        var p = live != null ? live.Nominal : bakeParams;
        var st = live != null ? live.State : FlightSetup.Pad(bakeParams);
        p.ChuteDeployAltitude = altitude;
        var flight = Bake(p, st);
        float impact = flight[flight.Length - 1].Speed, shock = 0f;
        for (int i = 0; i < flight.Length; i++)
            if ((flight[i].Flags & FlightFlags.ChuteOn) != 0) shock = Mathf.Max(shock, flight[i].Q);
        flight.Dispose();
        return (impact, shock);
    }

    // ---- Flight ----------------------------------------------------------------------------------------------------

    void Launch()
    {
        if (phase != Phase.Plan) return;
        if (rebakeTimer >= 0f) RebakePlan();  // the planned line must match the knobs it is frozen with
        failedMask = ReliabilityStore.RollFailures(rocket, MissionSession.Seed);
        progress?.StartFlight(rocket, failedMask);
        live = new LiveFlight(rocket, bakeParams, FlightSetup.Pad(bakeParams), pitch, MissionSession.Seed, false, failedMask);
        phase = Phase.Flying;
        flightAcc = 0f;
        RefreshPrediction();
        plotDirty = true;
    }

    void Advance()
    {
        flightAcc += Time.unscaledDeltaTime * warps[warpIndex];
        for (int n = 0; n < 5000 && flightAcc >= TrajectoryJob.Dt && !live.Landed; n++)
        {
            live.Step();
            progress?.Step(live);
            flightAcc -= TrajectoryJob.Dt;
        }
        if (live.Landed)
        {
            EndFlight();
            return;
        }
        predictTimer -= Time.unscaledDeltaTime;
        if (predictTimer <= 0f) RefreshPrediction();
    }

    void RefreshPrediction()
    {
        predictTimer = 0.5f;
        if (live == null || live.Landed) return;
        Free(ref predicted);
        predicted = live.Predict();
        flownEvents = FlightEvents.Detect(live.Samples);
        RefreshChuteReadout();
    }

    void EndFlight()
    {
        phase = Phase.Result;
        Free(ref predicted);
        flownEvents = FlightEvents.Detect(live.Samples);
        var m = MissionSession.Mission;
        bool testPassed = progress != null && MissionEvaluator.TestPassed(m, rocket, failedMask, progress.TestDone);
        SpecialModuleType? tested = testPassed ? m.moduleUnderTest : (SpecialModuleType?)null;
        reliabilityNote = ReliabilityStore.RecordFlight(rocket, failedMask, tested, MissionSession.Seed);
        ShowResult();
        plotDirty = true;
    }

    void ShowResult()
    {
        var last = live.Latest;
        var planned = baked[baked.Length - 1];
        float maxQ = 0f, maxHeat = 0f;
        for (int i = 0; i < live.Samples.Length; i++)
        {
            maxQ = Mathf.Max(maxQ, live.Samples[i].Q);
            maxHeat = Mathf.Max(maxHeat, live.Samples[i].Heating);
        }
        bool chute = (live.State.Flags & FlightFlags.ChuteOn) != 0;
        float dev = LiveFlight.Deviation(baked, live.Samples);
        string canopy = chute ? "opened" : "did not open";
        string verdict = "<b>TOUCHDOWN</b>", checklist = "";
        if (progress != null)
        {
            var list = MissionEvaluator.Checklist(MissionSession.Mission, progress, rocket, failedMask, MaxAltitude(live.Samples), !live.Broken, last.Speed, live.ProbeFree);
            verdict = MissionEvaluator.AllMet(list) ? "<b>MISSION COMPLETE</b>" : "<b>MISSION FAILED</b>";
            foreach (var q in list) checklist += $"{(q.Met ? "[x]" : "[ ]")} {q.Text}\n";
            checklist += "\n";
        }
        checklist += FailureLines();
        for (int i = Mathf.Max(0, live.Log.Count - 4); i < live.Log.Count; i++)
            checklist += $"<color=#FF6B5C>{live.Log[i]}</color>\n";
        if (reliabilityNote.Length > 0) checklist += reliabilityNote + "\n\n";
        string end = live.Broken
            ? $"Broke up at T+{last.Time:0.0} s, {last.Altitude / 1000f:0.0} km up, no landing"
            : $"Impact  {last.Speed:0.0} m/s at {last.Downrange / 1000f:0.0} km downrange";
        resultText.text = verdict + "\n\n" + checklist +
            $"{end} (planned {planned.Downrange / 1000f:0.0} km)\n" +
            $"Apogee  {MaxAltitude(live.Samples) / 1000f:0.0} km flown, {MaxAltitude(baked) / 1000f:0.0} km planned\n" +
            $"Max Q  {maxQ / 1000f:0.0} kPa     Max heating  {maxHeat:0.0e0}\n" +
            $"Parachute {canopy}     Deviation at landing  {dev / 1000f:0.0} km";
        resultOverlay.SetActive(true);
    }

    void ResetFlight()
    {
        live?.Dispose();
        live = null;
        Free(ref predicted);
        flownEvents = new List<FlightEvent>();
        phase = Phase.Plan;
        dragChute = false;
        flightAcc = 0f;
        rebakeTimer = 0f;  // the planned line is re-baked from the current knobs
        progress = NewProgress();
        reliabilityNote = "";
        resultOverlay.SetActive(false);
        plotDirty = true;
    }

    // Fitted and not failed on this flight.
    bool HasWorking(SpecialModuleType t) =>
        rocket.Has(t) && (failedMask & (1 << (int)t)) == 0 && (live == null || (live.Damaged & (1 << (int)t)) == 0);

    // One line per module that failed on this flight, or nothing.
    string FailureLines()
    {
        var sb = new StringBuilder();
        foreach (var t in ReliabilityStore.Rolled)
            if ((failedMask & (1 << (int)t)) != 0)
                sb.Append($"<color=#FF6B5C>! {ReliabilityStore.DisplayName(t)} failed: {FailureModes.Cause(t, MissionSession.Seed)}</color>\n");
        return sb.ToString();
    }

    static MissionProgress NewProgress() =>
        MissionSession.Mission ? new MissionProgress(MissionSession.Mission, MissionSession.Seed) : null;

    void SetWarp(int i)
    {
        warpIndex = i;
        for (int j = 0; j < warpImages.Length; j++) warpImages[j].color = j == i ? ActiveBg : ButtonBg;
    }

    void SetDial(Dial d)
    {
        dial = d;
        knobImg.rectTransform.localRotation = Quaternion.Euler(0f, 0f, DialAngle[(int)d]);
    }

    void HandleKeys()
    {
        var k = Keyboard.current;
        if (k == null) return;
        if (k.spaceKey.wasPressedThisFrame) Launch();
        if (k.digit1Key.wasPressedThisFrame) SetWarp(0);
        if (k.digit2Key.wasPressedThisFrame) SetWarp(1);
        if (k.digit3Key.wasPressedThisFrame) SetWarp(2);
        if (k.rKey.wasPressedThisFrame) ResetFlight();
        if (k.sKey.wasPressedThisFrame) DoStage();
        if (k.dKey.wasPressedThisFrame) DoDrogue();
        if (k.pKey.wasPressedThisFrame) DoPayload();
        if (k.tKey.wasPressedThisFrame) DoTest();
        if (k.eKey.wasPressedThisFrame) DoExperiment();
        if (k.xKey.wasPressedThisFrame) DoTransmit();
        if (k.iKey.wasPressedThisFrame) ToggleGuide();
    }

    // The info button and the I key open and close the flight panel guide.
    void ToggleGuide() => guideRoot.SetActive(!guideRoot.activeSelf);

    // ---- Player actions. Each is also on the action row; the keys are S, D, P, T, E, X. ----------------------------

    void DoStage() { if (phase == Phase.Flying && live != null) live.Stage(); }

    void DoDrogue() { if (phase == Phase.Flying && live != null) live.DeployDrogue(); }

    // DEPLOY PAYLOAD: the fairing drops and the probe separates from the rocket.
    void DoPayload()
    {
        if (phase != Phase.Flying || live == null || live.ProbeFree) return;
        live.SeparateProbe(FlightSetup.Probe(rocket, planet, live.Nominal.ChuteDeployAltitude));
        plotDirty = true;
        RefreshPrediction();
    }

    void DoTest()
    {
        if (phase == Phase.Flying && progress != null && live != null) progress.Test(live);
    }

    void DoExperiment()
    {
        if (phase == Phase.Flying && progress != null && live != null) progress.ToggleRun(live);
    }

    void DoTransmit()
    {
        if (phase == Phase.Flying && progress != null && live != null && progress.CanTransmit(live, HasWorking(SpecialModuleType.AdvancedCommunication)))
            progress.StartTransmit(live);
    }

    // The action row: each button is enabled only when its action can be done now, and its label says where it stands.
    void UpdateActions()
    {
        bool flying = phase == Phase.Flying && live != null;
        bool mission = progress != null;
        bool test = mission && MissionSession.Mission.kind == MissionKind.TestFlight;
        bool science = mission && MissionSession.Mission.kind == MissionKind.Science;

        Enable(ActStage, flying && live.CanStage, live != null && live.Staged ? "STAGED" : "STAGE");
        Enable(ActDrogue, flying && live.CanDeployDrogue, live != null && live.DrogueOut ? "DROGUE OUT" : "DROGUE");
        Enable(ActPayload, flying && !live.ProbeFree, live != null && live.ProbeFree ? "PROBE OUT" : "PAYLOAD");
        Enable(ActTest, flying && mission && progress.CanTest(live), progress != null && progress.TestDone ? "TESTED" : "TEST");
        Enable(ActExperiment, flying && science && (progress.CanRun(live) || progress.Running),
            progress != null && progress.DataCollected ? "DATA IN" : progress != null && progress.Running ? "PAUSE" : "EXPERIMENT");
        Enable(ActTransmit, flying && mission && progress.CanTransmit(live, HasWorking(SpecialModuleType.AdvancedCommunication)),
            progress != null && progress.Transmitted ? "SENT" : "TRANSMIT");

        actions[ActTest].gameObject.SetActive(test);
        actions[ActExperiment].gameObject.SetActive(science);
        actions[ActTransmit].gameObject.SetActive(mission);
    }

    void Enable(int index, bool on, string label)
    {
        actions[index].interactable = on;
        actions[index].GetComponentInChildren<TMP_Text>().text = label;
    }

    // What the player can do right now. Shown in the top bar, so the player is told what is open to them.
    string Prompt()
    {
        if (phase == Phase.Plan) return "set the turn, then LAUNCH";
        if (phase != Phase.Flying || live == null) return "";
        var c = live.Latest;
        var hints = new List<string>();
        if (live.CanStage && !c.ThrustOn) hints.Add("booster burnt out: S stages it away");
        if (progress != null && progress.CanTest(live)) hints.Add($"T tests the {ReliabilityStore.DisplayName(progress.Mission.moduleUnderTest)} at test altitude");
        if (progress != null && progress.Mission.kind == MissionKind.Science && (progress.CanRun(live) || progress.Running))
            hints.Add(progress.Running ? "experiment running: E pauses it" : "inside the target window: E runs the experiment");
        if (progress != null && progress.CanTransmit(live, HasWorking(SpecialModuleType.AdvancedCommunication))) hints.Add("data in hand: X transmits it");
        if (live.CanDeployDrogue) hints.Add("falling: D deploys the drogue");
        if (!live.ProbeFree && (c.Altitude >= FlightEvents.BoundaryAltitude || c.VerticalVel < 0f))
            hints.Add("P deploys the payload: fairing off, probe separated");
        if (live.ProbeFree && c.VerticalVel < 0f && (live.State.Flags & FlightFlags.ChuteOn) == 0)
            hints.Add($"probe alone: its canopy opens at {live.Actual.ChuteDeployAltitude / 1000f:0.0} km");
        return hints.Count == 0 ? "watch the telemetry" : string.Join("   |   ", hints.GetRange(0, Mathf.Min(2, hints.Count)));
    }

    // Drag on the chute graph to move the opening altitude. Ignored once the canopy is open.
    void HandleChuteDrag()
    {
        bool open = live != null && (live.State.Flags & FlightFlags.ChuteOn) != 0;
        if (rocket.chuteCdA <= 0f || open || phase == Phase.Result || Mouse.current == null || !Mouse.current.leftButton.isPressed)
        {
            dragChute = false;
            return;
        }
        RectTransformUtility.ScreenPointToLocalPointInRectangle(chuteRect, Mouse.current.position.ReadValue(), null, out var local);
        if (!dragChute && !chuteRect.rect.Contains(local)) return;
        dragChute = true;
        float t = Mathf.Clamp01((local.x - PlotML) / (ChuteBox.width - PlotML - PlotMR));
        float alt = t * ChuteRange();
        if (Mathf.Abs(alt - chuteAlt) > 1f) SetChute(alt);
    }

    // Knobs: click or hold the -/+ to step, and scroll the wheel over a knob. Art coordinates, so it lines up with the drawn knobs.
    const float KnobHoldDelay = 0.4f, KnobHoldRate = 0.08f;

    void HandleKnobs()
    {
        if (Mouse.current == null || phase == Phase.Result)
        {
            holdKnob = -1;
            return;
        }
        Vector2 art = ArtMouse();
        float wheel = Mouse.current.scroll.ReadValue().y;
        if (wheel != 0f)
            for (int i = 0; i < knobs.Length; i++)
                if (KnobColumn(i).Contains(art)) Nudge(i, wheel > 0f ? 1 : -1);

        if (Mouse.current.leftButton.wasPressedThisFrame)
            for (int i = 0; i < knobs.Length; i++)
                for (int dir = -1; dir <= 1; dir += 2)
                    if (KnobButton(i, dir).Contains(art))
                    {
                        holdKnob = i;
                        holdDir = dir;
                        holdTimer = KnobHoldDelay;
                        Nudge(i, dir);
                    }

        if (holdKnob < 0) return;
        if (!Mouse.current.leftButton.isPressed)
        {
            holdKnob = -1;
            return;
        }
        holdTimer -= Time.unscaledDeltaTime;
        if (holdTimer <= 0f)
        {
            holdTimer = KnobHoldRate;
            Nudge(holdKnob, holdDir);
        }
    }

    // The -/+ boxes of a knob row, and the column that takes the wheel, in art coordinates.
    static Rect KnobButton(int i, int dir) => new Rect(dir < 0 ? 112 : 212, KnobBarY[i], 42, 40);
    static Rect KnobColumn(int i) => new Rect(100, KnobBarY[i] - 55, 170, 115);

    Vector2 ArtMouse()
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(stage, Mouse.current.position.ReadValue(), null, out var local);
        return new Vector2(local.x + 960f, 540f - local.y);  // the stage is 1920x1080, centred on the screen
    }

    float ChuteRange() => Mathf.Max(2f * rocket.chuteDeployMax, 1000f);

    // ---- Hover tooltips ------------------------------------------------------------------------------------------

    void UpdateHover()
    {
        tipBox.gameObject.SetActive(false);
        if (Mouse.current == null) return;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(stage, Mouse.current.position.ReadValue(), null, out var local);
        Vector2 art = new Vector2(local.x + 960f, 540f - local.y);  // the stage is 1920x1080, centred on the screen
        string text = null;
        if (PlotBox.Contains(art)) text = PlotTip(art);
        else if (ChuteBox.Contains(art) && rocket.chuteCdA > 0f) text = ChuteTip(art);
        if (text == null) return;
        tipText.text = text;
        tipBox.gameObject.SetActive(true);
        tipBox.rectTransform.anchoredPosition = new Vector2(Mathf.Min(art.x + 18f, 1476f), -Mathf.Min(art.y + 18f, 776f));
    }

    // Graph key: one line per colour, written in that colour.
    static string Key(Color c, string label, string desc) =>
        $"<color=#{ColorUtility.ToHtmlStringRGB(c)}><b>{label}</b></color>  {desc}\n";

    static string PlotKey() =>
        Key(PlannedColor, "Planned", "dotted: the flight the knobs predict.") +
        Key(FlownColor, "Flown", "solid: the actual path so far.") +
        Key(PredictedColor, "Predicted", "faint: where the rocket goes from its current state.") +
        Key(BoundaryColor, "100 km", "the usual edge of space.") +
        Key(EventColor, "Events", "dots: planned and flown events. Hover a dot for details.") +
        Key(SuggestColor, "Target", "science missions: the experiment runs near this altitude.") +
        Key(RocketColor, "Rocket", "white dot: where the rocket is now.");

    static string ChuteKey() =>
        Key(SpeedColor, "Speed", "descent speed at each altitude, under the drogue, before the canopy opens.") +
        Key(ShockColor, "Opening shock", "dynamic pressure a canopy would take if it opened there.") +
        Key(UnsafeColor, "Limit", "largest opening shock the canopy can take. Red dots: over it.") +
        Key(SuggestColor, "Suggested", "the altitude the solver picked for the best landing.") +
        Key(RocketColor, "Marker", "the altitude in use. Drag the graph to move it.");

    // The nearest planned or flown event within 14 px. Anywhere else on the plot, the colour key.
    string PlotTip(Vector2 art)
    {
        FlightEvent? hit = null;
        float best = 14f;
        foreach (var list in new[] { bakedEvents, flownEvents })
            foreach (var e in list)
            {
                float d = Vector2.Distance(art, PlotArt(e.Downrange, e.Altitude));
                if (d < best) { best = d; hit = e; }
            }
        if (!hit.HasValue) return "<b>Graph key</b>\n" + PlotKey();
        var h = hit.Value;
        return $"<b>{FlightEvents.Name(h.Type)}</b>\n{FlightEvents.Description(h.Type)}\n\n{FlightEvents.Details(h)}";
    }

    // The landing for a canopy opened where the cursor is. Re-flown only after the cursor moves 50 m.
    string ChuteTip(Vector2 art)
    {
        if (live != null && (live.State.Flags & FlightFlags.ChuteOn) != 0) return "<b>Canopy open</b>\nOpening altitude is locked.";
        float xmax = ChuteRange();
        float alt = (art.x - ChuteBox.x - PlotML) / (ChuteBox.width - PlotML - PlotMR) * xmax;
        if (alt < 0f || alt > xmax) return null;
        if (tipAlt < 0f || Mathf.Abs(alt - tipAlt) > 50f)
        {
            (tipImpact, tipQ) = ChuteOutcome(alt);
            tipAlt = alt;
        }
        string verdict = tipQ > rocket.maxChuteQ ? "Over the shock limit: the canopy may tear." : "Within the shock limit.";
        return $"<b>Deploy at {tipAlt / 1000f:0.0} km</b>\n" +
               $"Landing {tipImpact:0.0} m/s     Opening shock {tipQ / 1000f:0.0} kPa (limit {rocket.maxChuteQ / 1000f:0})\n{verdict}\n\n" +
               "<b>Graph key</b>\n" + ChuteKey();
    }

    // Art coordinates of a downrange / altitude point on the plot, using the axes of the last drawn plot.
    Vector2 PlotArt(float dr, float alt) => new Vector2(
        PlotBox.x + PlotML + (dr - plotX0) / (plotX1 - plotX0) * (PlotBox.width - PlotML - PlotMR),
        PlotBox.y + PlotBox.height - (PlotMB + alt / plotTop * (PlotBox.height - PlotMB - PlotMT)));

    // ---- Sticky note ----------------------------------------------------------------------------------------------

    void UpdateNote()
    {
        float target = noteOpen ? NoteRest.x : NoteHidden;
        noteX = Mathf.Lerp(noteX, target, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
        noteRect.anchoredPosition = new Vector2(noteX, -NoteRest.y);
        noteRect.gameObject.SetActive(noteX < NoteRest.x + 100f);
        pulse = Mathf.Max(0f, pulse - Time.unscaledDeltaTime);
        tabZone.color = new Color(1f, 0.85f, 0f, pulse * 0.35f);
    }

    AdvisorState Snapshot(TrajectorySample c)
    {
        bool landed = live != null && live.Landed;
        bool flying = live != null && !landed;
        return new AdvisorState
        {
            Launched = live != null,
            Landed = landed,
            HasChute = rocket.chuteCdA > 0f,
            ChuteOn = live != null && (live.State.Flags & FlightFlags.ChuteOn) != 0,
            Ascending = c.VerticalVel > 0f,
            PredictedApogee = flying ? MaxAltitude(predicted, PredictStride) : MaxAltitude(baked),
            TargetApogee = FlightEvents.BoundaryAltitude,
            ChuteQ = cursorQ,
            ChuteQLimit = rocket.maxChuteQ,
            Deviation = live != null ? LiveFlight.Deviation(baked, live.Samples) : 0f,
            Battery = live != null ? live.Battery : 100f,
            ImpactSpeed = landed ? c.Speed : 0f,
            Q = c.Q,
            QLimit = rocket.maxQ,
            Altitude = c.Altitude,
            Speed = c.Speed,
            Descending = c.VerticalVel < 0f,
            ChuteAlt = chuteAlt,
            Broken = live != null && live.Broken,
        };
    }

    // ---- Readouts ------------------------------------------------------------------------------------------------

    TrajectorySample Current()
    {
        if (live != null) return live.Latest;
        return baked.IsCreated && baked.Length > 0 ? baked[0] : default;
    }

    void UpdateReadouts()
    {
        var c = Current();
        bool landed = live != null && live.Landed;
        float t = live != null ? live.Time : 0f;
        float battery = live != null ? live.Battery : 100f;

        statusText.text = phase switch
        {
            Phase.Plan => (progress != null ? MissionSession.Mission.title + "   " : "") + "PLAN  set the turn, then LAUNCH",
            Phase.Flying => $"FLYING  x{warps[warpIndex]:0}   |   {Prompt()}",
            _ => "LANDED",
        };
        header.text = $"T+ {Clock(t)}     BAT {battery:0}%";
        devText.text = live != null ? $"DEVIATION\n{LiveFlight.Deviation(baked, live.Samples) / 1000f:0.00} km" : "DEVIATION\n--";
        speedText.text = $"SPEED  {c.Speed:0} m/s";
        altText.text = $"ALTITUDE  {c.Altitude / 1000f:0.00} km";
        infoText.text = DialText(c, battery);
        chuteText.text = ChuteText();
        warnText.text = rocket.chuteCdA <= 0f && phase != Phase.Result ? "NO PARACHUTE INSTALLED. The descent ends at full speed." : "";

        bool locked = live != null && (live.Time >= knobs[0].Value || landed);
        for (int i = 0; i < knobs.Length; i++)
        {
            knobs[i].Readout.text = $"{knobs[i].Value:0.#}{knobs[i].Unit}";
            knobs[i].Readout.color = locked ? Dim : Ink;
        }

        bool chuteOn = live != null && (live.State.Flags & FlightFlags.ChuteOn) != 0;
        lamp.color = chuteOn ? new Color(0.4f, 1f, 0.4f) : Dim;

        // Experiment lamp: green while the experiment runs, amber once its data is in.
        bool running = progress != null && progress.Running;
        expLamp.color = running ? new Color(0.4f, 1f, 0.4f) : progress != null && progress.DataCollected ? new Color(1f, 0.8f, 0.2f) : Dim;
        UpdateActions();

        string next = AdvisorRules.Pick(Snapshot(c));
        if (next != advice)
        {
            advice = next;
            noteText.text = advice;
            pulse = 1f;
        }
    }

    string DialText(TrajectorySample c, float battery)
    {
        var p = FlightSetup.Params(rocket, planet);
        bool flying = live != null && !live.Landed;
        var sb = new StringBuilder();
        switch (dial)
        {
            case Dial.Nav:
            {
                string apogee = flying ? $"predicted {MaxAltitude(predicted, PredictStride) / 1000f:0.0} km" : $"planned {MaxAltitude(baked) / 1000f:0.0} km";
                sb.Append($"<b>NAVIGATION</b>\nDownrange  {c.Downrange / 1000f:0.00} km\n");
                sb.Append($"Vertical  {c.VerticalVel:0} m/s\nHorizontal  {c.HorizontalVel:0} m/s\n");
                sb.Append($"Pitch  {math.degrees(c.Pitch):0} deg\nApogee  {apogee}");
                break;
            }
            case Dial.Engine:
            {
                sb.Append("<b>ENGINE</b>\n");
                sb.Append($"Thrust  {(c.ThrustOn ? "ON" : "OFF")}  {rocket.thrust:0} N\n");
                sb.Append($"Propellant  {c.Fuel:0.0} kg\nMass  {c.Mass:0.0} kg\n");
                sb.Append($"Battery  {battery:0.0}%  (-{LiveFlight.BatteryPerSecond}%/s)");
                break;
            }
            case Dial.Weather:
            {
                sb.Append("<b>ATMOSPHERE</b>\n");
                sb.Append($"Altitude  {c.Altitude / 1000f:0.00} km\n");
                sb.Append($"Density  {FlightStep.Density(c.Altitude, p):0.0000} kg/m3\n");
                sb.Append($"Pressure  {101.325f * math.exp(-math.max(c.Altitude, 0f) / planet.scaleHeight):0.0} kPa\n");
                sb.Append($"Dynamic pressure  {c.Q / 1000f:0.0} kPa\nHeating index  {c.Heating:0.0e0}");
                break;
            }
            default:
            {
                sb.Append($"<b>STATUS</b>  {(flying ? "in flight" : live != null ? "landed" : "planned")}\n");
                if (progress != null)
                {
                    float apogee = live != null ? MaxAltitude(live.Samples) : 0f;
                    foreach (var q in MissionEvaluator.Checklist(MissionSession.Mission, progress, rocket, failedMask, apogee, false, 0f, live != null && live.ProbeFree))
                        sb.Append($"{(q.Met ? "[x]" : "[ ]")} {q.Text}\n");
                }
                sb.Append(FailureLines());
                if (live != null)
                    for (int i = Mathf.Max(0, live.Log.Count - 3); i < live.Log.Count; i++)
                        sb.Append($"<color=#FF6B5C>{live.Log[i]}</color>\n");
                var events = live != null ? flownEvents : bakedEvents;
                int shown = progress != null ? 2 : 4;
                for (int i = 0; i < events.Count && i < shown; i++)
                    sb.Append($"T+{events[i].Time:0.0}  {FlightEvents.Name(events[i].Type)}\n");
                break;
            }
        }
        return sb.ToString();
    }

    string ChuteText()
    {
        if (rocket.chuteCdA <= 0f) return "";  // the warning is centred, see UpdateReadouts
        if (live != null && (live.State.Flags & FlightFlags.ChuteOn) != 0) return "CANOPY OPEN. Opening altitude is locked.";
        return $"Deploy at {chuteAlt / 1000f:0.0} km     Landing {cursorImpact:0.0} m/s     " +
               $"Opening shock {cursorQ / 1000f:0.0} kPa (limit {rocket.maxChuteQ / 1000f:0})\n" +
               $"Suggested {suggestedChute / 1000f:0.0} km. Drag on the graph to move the marker.";
    }

    static string Clock(float t) => $"{Mathf.Floor(t / 60f):00}:{t % 60f:00.0}";

    static float MaxAltitude(NativeList<TrajectorySample> s, int stride = 1)
    {
        float m = 0f;
        if (s.IsCreated)
            for (int i = 0; i < s.Length; i += stride) m = Mathf.Max(m, s[i].Altitude);
        return m;
    }

    static void Extent(NativeList<TrajectorySample> s, ref float x0, ref float x1, ref float maxAlt, int stride = 1)
    {
        if (!s.IsCreated) return;
        for (int i = 0; i < s.Length; i += stride)
        {
            x0 = Mathf.Min(x0, s[i].Downrange);
            x1 = Mathf.Max(x1, s[i].Downrange);
            maxAlt = Mathf.Max(maxAlt, s[i].Altitude);
        }
    }

    // 1-2-5 step, so the grid lands on round numbers.
    static float NiceStep(float range, int target)
    {
        float raw = Mathf.Max(range, 1e-3f) / target;
        float mag = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(raw)));
        float n = raw / mag;
        float nice = n <= 1f ? 1f : n <= 2f ? 2f : n <= 5f ? 5f : 10f;
        return nice * mag;
    }

    // ---- Drawing -------------------------------------------------------------------------------------------------

    // Downrange (x) against altitude (y). The axes scale independently, so a short flight still fills the screen.
    void DrawPlot()
    {
        float x0 = float.MaxValue, x1 = float.MinValue, maxAlt = 0f;
        Extent(baked, ref x0, ref x1, ref maxAlt);
        if (live != null) Extent(live.Samples, ref x0, ref x1, ref maxAlt);
        Extent(predicted, ref x0, ref x1, ref maxAlt, PredictStride);
        if (x0 > x1) { x0 = -1000f; x1 = 1000f; }
        if (x1 - x0 < 1000f)
        {
            float mid = 0.5f * (x0 + x1);
            x0 = mid - 500f;
            x1 = mid + 500f;
        }
        float pad = 0.05f * (x1 - x0);
        x0 -= pad;
        x1 += pad;
        float top = Mathf.Max(1.1f * FlightEvents.BoundaryAltitude, 1.1f * maxAlt);
        plotX0 = x0; plotX1 = x1; plotTop = top;

        int W = plotRaster.Width, H = plotRaster.Height;
        float iw = W - PlotML - PlotMR, ih = H - PlotMB - PlotMT;
        Vector2 P(float dr, float alt) => new Vector2(PlotML + (dr - x0) / (x1 - x0) * iw, PlotMB + alt / top * ih);

        plotRaster.Clear(ScreenBg);
        plotLabels.Begin();
        float sx = NiceStep(x1 - x0, 4), sy = NiceStep(top, 5);
        for (float v = Mathf.Ceil(x0 / sx) * sx; v <= x1; v += sx)
        {
            float px = P(v, 0f).x;
            plotRaster.Line(new Vector2(px, PlotMB), new Vector2(px, H - PlotMT), GridColor);
            plotLabels.Add(px, 8f, $"{v / 1000f:0.#}");
        }
        for (float v = 0f; v <= top; v += sy)
        {
            float py = P(0f, v).y;
            plotRaster.Line(new Vector2(PlotML, py), new Vector2(W - PlotMR, py), GridColor);
            plotLabels.Add(PlotML - 26f, py, $"{v / 1000f:0}");
        }
        float y100 = P(0f, FlightEvents.BoundaryAltitude).y;
        plotRaster.Line(new Vector2(PlotML, y100), new Vector2(W - PlotMR, y100), BoundaryColor, 5);
        plotLabels.Add(W - PlotMR - 40f, y100 + 10f, "100 km");
        if (progress != null)
        {
            float yt = P(0f, progress.TargetAltitude).y;
            plotRaster.Line(new Vector2(PlotML, yt), new Vector2(W - PlotMR, yt), SuggestColor, 4);
            plotLabels.Add(W - PlotMR - 40f, yt + 10f, "target");
        }

        void Trace(NativeList<TrajectorySample> s, Color c, int dash, int stride = 1)
        {
            if (!s.IsCreated || s.Length < 2) return;
            pts.Clear();
            for (int i = 0; i < s.Length; i += stride) pts.Add(P(s[i].Downrange, s[i].Altitude));
            plotRaster.Polyline(pts, c, dash);
        }
        Trace(baked, PlannedColor, 4);
        Trace(predicted, PredictedColor, 2, PredictStride);
        if (live != null) Trace(live.Samples, FlownColor, 0);

        foreach (var e in bakedEvents) plotRaster.Dot(P(e.Downrange, e.Altitude), 3, EventColor);
        foreach (var e in flownEvents) plotRaster.Dot(P(e.Downrange, e.Altitude), 3, EventColor);
        if (live != null) plotRaster.Dot(P(live.Latest.Downrange, live.Latest.Altitude), 3, RocketColor);

        plotRaster.Apply();
        plotLabels.End();
    }

    // Descent speed and opening shock against deployment altitude. Red where opening would exceed the limit.
    void DrawChute()
    {
        int W = chuteRaster.Width, H = chuteRaster.Height;
        float iw = W - PlotML - PlotMR, ih = H - PlotMB - PlotMT;
        float xmax = ChuteRange();
        float X(float alt) => PlotML + alt / xmax * iw;

        chuteRaster.Clear(ScreenBg);
        chuteLabels.Begin();
        if (rocket.chuteCdA > 0f && descent.IsCreated)
        {
            float vmax = 1f, qmax = 1.25f * rocket.maxChuteQ;
            for (int i = 0; i < descent.Length; i++)
            {
                var s = descent[i];
                if (s.VerticalVel >= 0f || s.Altitude < 0f || s.Altitude > xmax) continue;
                vmax = Mathf.Max(vmax, s.Speed);
                qmax = Mathf.Max(qmax, s.Q);
            }

            var speed = new List<Vector2>();
            var shock = new List<Vector2>();
            for (int i = 0; i < descent.Length; i++)
            {
                var s = descent[i];
                if (s.VerticalVel >= 0f || s.Altitude < 0f || s.Altitude > xmax) continue;
                float x = X(s.Altitude);
                speed.Add(new Vector2(x, PlotMB + s.Speed / vmax * ih));
                var q = new Vector2(x, PlotMB + s.Q / qmax * ih);
                shock.Add(q);
                if (s.Q > rocket.maxChuteQ) chuteRaster.Dot(q, 2, UnsafeColor);
            }
            chuteRaster.Polyline(speed, SpeedColor);
            chuteRaster.Polyline(shock, ShockColor);
            float yl = PlotMB + rocket.maxChuteQ / qmax * ih;
            chuteRaster.Line(new Vector2(PlotML, yl), new Vector2(W - PlotMR, yl), UnsafeColor, 5);

            float sg = X(suggestedChute);
            chuteRaster.Line(new Vector2(sg, PlotMB), new Vector2(sg, H - PlotMT), SuggestColor, 3);
            float cx = X(chuteAlt);
            chuteRaster.Line(new Vector2(cx, PlotMB), new Vector2(cx, H - PlotMT), RocketColor);
        }

        float step = NiceStep(xmax, 5);
        for (float v = 0f; v <= xmax; v += step)
        {
            float px = X(v);
            chuteRaster.Line(new Vector2(px, PlotMB), new Vector2(px, H - PlotMT), GridColor);
            chuteLabels.Add(px, 8f, $"{v / 1000f:0.#} km");
        }
        chuteRaster.Apply();
        chuteLabels.End();
    }

    // ---- UI ------------------------------------------------------------------------------------------------------

    void BuildEventSystem()
    {
        // UI pointer input needs an EventSystem using the new Input System module, not StandaloneInputModule.
        var events = FindFirstObjectByType<EventSystem>();
        if (!events) events = new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
        var legacy = events.GetComponent<StandaloneInputModule>();
        if (legacy) Destroy(legacy);
        if (!events.GetComponent<InputSystemUIInputModule>()) events.gameObject.AddComponent<InputSystemUIInputModule>();
    }

    void BuildUi()
    {
        var canvasGo = new GameObject("GameplayCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Shrink;
        var root = (RectTransform)canvasGo.transform;

        var backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
        backdrop.transform.SetParent(root, false);
        var bd = (RectTransform)backdrop.transform;
        bd.anchorMin = Vector2.zero;
        bd.anchorMax = Vector2.one;
        bd.offsetMin = bd.offsetMax = Vector2.zero;
        backdrop.GetComponent<Image>().color = Color.black;

        var stageGo = new GameObject("Stage", typeof(RectTransform));
        stageGo.transform.SetParent(root, false);
        stage = (RectTransform)stageGo.transform;
        stage.anchorMin = stage.anchorMax = stage.pivot = new Vector2(0.5f, 0.5f);
        stage.sizeDelta = new Vector2(1920f, 1080f);

        // The art, then the screens and controls on top of it.
        var art = Place(stage, new Rect(0, 0, 1920, 1080), "Panel").gameObject.AddComponent<Image>();
        art.sprite = LoadSprite(PanelPath);
        art.preserveAspect = true;
        if (!art.sprite) art.color = new Color(0.4f, 0.4f, 0.42f);
        art.raycastTarget = false;

        // Top bar.
        statusText = MakeText(stage, new Rect(560, 104, 820, 32), 18, font);
        Btn(stage, new Rect(170, 100, 170, 38), "LAUNCH", ButtonBg, Launch, font);
        for (int i = 0; i < warps.Length; i++)
        {
            int idx = i;
            var b = Btn(stage, new Rect(360 + i * 70, 100, 62, 38), $"{warps[i]:0}x", ButtonBg, () => SetWarp(idx), font);
            warpImages[i] = (Image)b.targetGraphic;
        }
        Btn(stage, new Rect(1395, 100, 120, 38), "RESET", ButtonBg, ResetFlight, font);
        Btn(stage, new Rect(1525, 100, 150, 38), "BUILDER", ButtonBg, () => SceneManager.LoadScene("BuilderScene"), font);

        // Trajectory screen.
        plotRaster = new Raster((int)PlotBox.width, (int)PlotBox.height);
        Raw(stage, PlotBox, plotRaster.Texture);
        plotLabels = new Labels(stage, PlotBox, font, 24);
        MakeText(stage, new Rect(312, 178, 580, 16), 12, font, "dotted: planned   solid: flown   faint: predicted   (km)");

        // Knobs: turn start, pitch, duration. The art draws the knobs and the brown readout windows.
        for (int i = 0; i < knobs.Length; i++)
        {
            knobs[i].Readout = MakeText(stage, new Rect(156, KnobBarY[i] + 8, 58, 24), 14, font, "", TextAlignmentOptions.Center);
            MakeText(stage, new Rect(112, KnobBarY[i] + 42, 150, 16), 11, font, KnobNames[i]);
        }

        // Chute graph.
        chuteRaster = new Raster((int)ChuteBox.width, (int)ChuteBox.height);
        chuteRect = Raw(stage, ChuteBox, chuteRaster.Texture).rectTransform;
        chuteLabels = new Labels(stage, ChuteBox, font, 10);
        chuteText = MakeText(stage, new Rect(1000, 632, 700, 56), 14, font);

        // Info dial and its header.
        Panel(InfoBox, ScreenBg);
        header = MakeText(stage, new Rect(586, 718, 310, 22), 14, font);
        infoText = MakeText(stage, new Rect(586, 742, 312, 190), 13, font);
        for (int i = 0; i < DialZones.Length; i++)
        {
            int idx = i;
            Btn(stage, DialZones[i], null, Clear, () => SetDial((Dial)idx), font);
        }
        var knobRect = Place(stage, DialKnob, "DialKnob");
        knobRect.pivot = new Vector2(0.5f, 0.5f);  // rotates about its centre
        knobRect.anchoredPosition = new Vector2(DialKnob.center.x, -DialKnob.center.y);
        knobImg = knobRect.gameObject.AddComponent<Image>();
        knobImg.rectTransform.sizeDelta = new Vector2(DialKnob.width * 2, DialKnob.height * 2);
        knobImg.sprite = LoadSprite(KnobPath);
        knobImg.preserveAspect = true;
        knobImg.raycastTarget = false;

        // Small screens: deviation, speed, altitude.
        Panel(DevBox, ScreenBg);
        Panel(SpeedBox, ScreenBg);
        Panel(AltBox, ScreenBg);
        devText = MakeText(stage, new Rect(DevBox.x + 8, DevBox.y + 4, DevBox.width - 16, DevBox.height - 8), 15, font);
        speedText = MakeText(stage, new Rect(SpeedBox.x + 8, SpeedBox.y + 6, SpeedBox.width - 16, SpeedBox.height - 8), 15, font);
        altText = MakeText(stage, new Rect(AltBox.x + 8, AltBox.y + 6, AltBox.width - 16, AltBox.height - 8), 15, font);

        lamp = Place(stage, new Rect(1252, 744, 14, 14), "Lamp").gameObject.AddComponent<Image>();
        lamp.raycastTarget = false;
        expLamp = Place(stage, new Rect(1204, 818, 14, 14), "ExpLamp").gameObject.AddComponent<Image>();
        expLamp.raycastTarget = false;

        // The action row along the bottom of the panel. Buttons are enabled only when their action is open now.
        string[] labels = { "STAGE", "DROGUE", "PAYLOAD", "TEST", "EXPERIMENT", "TRANSMIT" };
        UnityAction[] handlers = { DoStage, DoDrogue, DoPayload, DoTest, DoExperiment, DoTransmit };
        for (int i = 0; i < actions.Length; i++)
            actions[i] = Btn(stage, new Rect(520 + i * 170, 966, 160, 44), labels[i], ButtonBg, handlers[i], font);

        // Sticky note: slides out over the chute graph when its tab is clicked.
        noteSprite = LoadSprite(NotePath);
        noteRect = Place(stage, NoteRest, "StickyNote");
        var noteImg = noteRect.gameObject.AddComponent<Image>();
        noteImg.sprite = noteSprite;
        noteImg.preserveAspect = true;
        noteImg.raycastTarget = false;
        noteText = MakeText(noteRect, new Rect(34, 56, 272, 240), 17, font);
        noteText.color = NoteInk;
        noteRect.gameObject.SetActive(false);
        var tab = Btn(stage, new Rect(1738, 480, 130, 350), null, Clear, () => noteOpen = !noteOpen, font);
        tabZone = (Image)tab.targetGraphic;

        // No parachute: a large warning centred on the chute graph (top right of the panel).
        warnText = MakeText(stage, new Rect(ChuteBox.x, ChuteBox.y + ChuteBox.height / 2f - 70f, ChuteBox.width, 140), 44, font, "", TextAlignmentOptions.Center);
        warnText.color = UnsafeColor;

        // Result overlay, on top of everything.
        var overlayRect = Place(stage, new Rect(330, 200, 1260, 420), "Result");
        var overlayImg = overlayRect.gameObject.AddComponent<Image>();
        overlayImg.color = new Color(0.03f, 0.02f, 0.01f, 0.92f);
        resultOverlay = overlayRect.gameObject;
        resultText = MakeText(overlayRect, new Rect(30, 30, 1200, 290), 18, font);
        Btn(overlayRect, new Rect(30, 340, 220, 48), "RETRY", ButtonBg, ResetFlight, font);
        Btn(overlayRect, new Rect(270, 340, 300, 48), "BACK TO BUILDER", ButtonBg, () => SceneManager.LoadScene("BuilderScene"), font);
        Btn(overlayRect, new Rect(590, 340, 220, 48), "MISSIONS", ButtonBg, () => SceneManager.LoadScene("MissionSelect"), font);
        resultOverlay.SetActive(false);

        // Info button, top right of the top bar: opens the flight panel guide. A dark backing keeps it readable on any art.
        Panel(new Rect(1690, 96, 48, 48), ButtonBg);
        var info = Btn(stage, new Rect(1690, 96, 48, 48), null, Clear, ToggleGuide, font);
        var infoImg = (Image)info.targetGraphic;
        infoImg.sprite = LoadSprite(InfoIconPath);
        //if (infoImg.sprite)
        //{
        //    infoImg.preserveAspect = true;
        //    //infoImg.color = Color.white;  // Btn sets the background colour, which would tint the icon
        //}
        //else
        MakeText(info.transform, new Rect(0, 0, 48, 48), 26, font, "i", TextAlignmentOptions.Center);  // icon not found: a text fallback

        // Flight panel guide: a dark cover with the guide art fitted in the middle. A click anywhere on the cover, or I, closes it.
        var cover = Place(stage, new Rect(0, 0, 1920, 1080), "Guide");
        var coverImg = cover.gameObject.AddComponent<Image>();
        coverImg.color = new Color(0f, 0f, 0f, 0.85f);
        var coverBtn = cover.gameObject.AddComponent<Button>();
        coverBtn.targetGraphic = coverImg;
        coverBtn.transition = Selectable.Transition.None;
        coverBtn.onClick.AddListener(ToggleGuide);
        var guideArt = Place(cover, new Rect(480, 270, 1280, 720), "GuideArt").gameObject.AddComponent<Image>();
        guideArt.sprite = LoadSprite(GuidePath);
        guideArt.preserveAspect = true;
        guideArt.raycastTarget = false;
        guideRoot = cover.gameObject;
        guideRoot.SetActive(false);

        // Hover tooltip for trajectory events and the chute graph. Created last, so it draws on top.
        tipBox = Place(stage, new Rect(0, 0, 440, 300), "Tip").gameObject.AddComponent<Image>();
        tipBox.color = ScreenBg;
        tipBox.raycastTarget = false;
        tipText = MakeText(tipBox.rectTransform, new Rect(10, 8, 420, 284), 14, font);
        tipBox.gameObject.SetActive(false);

        SetWarp(0);
        SetDial(Dial.Nav);
    }

    // Places a rect in art coordinates: top-left origin, relative to the parent's top-left corner.
    static RectTransform Place(Transform parent, Rect r, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(r.x, -r.y);
        rt.sizeDelta = new Vector2(r.width, r.height);
        return rt;
    }

    void Panel(Rect r, Color c)
    {
        var img = Place(stage, r, "Screen").gameObject.AddComponent<Image>();
        img.color = c;
        img.raycastTarget = false;
    }

    static RawImage Raw(Transform parent, Rect r, Texture tex)
    {
        var raw = Place(parent, r, "Plot").gameObject.AddComponent<RawImage>();
        raw.texture = tex;
        raw.raycastTarget = false;
        return raw;
    }

    static TextMeshProUGUI MakeText(Transform parent, Rect r, float size, TMP_FontAsset font, string text = "", TextAlignmentOptions align = TextAlignmentOptions.TopLeft)
    {
        var rt = Place(parent, r, "Text");
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.font = font;
        t.fontSize = size;
        t.color = Ink;
        t.text = text;
        t.alignment = align;
        t.textWrappingMode = TextWrappingModes.Normal;
        t.raycastTarget = false;
        return t;
    }

    static Button Btn(Transform parent, Rect r, string text, Color bg, UnityAction onClick, TMP_FontAsset font)
    {
        var rt = Place(parent, r, "Button");
        var img = rt.gameObject.AddComponent<Image>();
        img.color = bg;
        var b = rt.gameObject.AddComponent<Button>();
        b.targetGraphic = img;
        b.transition = Selectable.Transition.None;
        b.onClick.AddListener(onClick);
        if (!string.IsNullOrEmpty(text)) MakeText(rt, new Rect(0, 0, r.width, r.height), 14, font, text, TextAlignmentOptions.Center);
        return b;
    }

    static Sprite LoadSprite(string path)
    {
#if UNITY_EDITOR
        foreach (var asset in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(path))
            if (asset is Sprite s) return s;
#endif
        return null;
    }

    // Axis labels for one plot box, from a fixed pool. Positions are texture pixels from the box's bottom-left.
    sealed class Labels
    {
        readonly Rect box;
        readonly List<TMP_Text> items = new List<TMP_Text>();
        int used;

        public Labels(Transform parent, Rect box, TMP_FontAsset font, int count)
        {
            this.box = box;
            for (int i = 0; i < count; i++)
                items.Add(MakeText(parent, new Rect(0, 0, 60, 16), 11, font, "", TextAlignmentOptions.Center));
        }

        public void Begin() => used = 0;

        public void Add(float px, float py, string text)
        {
            if (used >= items.Count) return;
            var t = items[used++];
            t.text = text;
            t.gameObject.SetActive(true);
            float top = box.y + box.height - py - 8f;
            ((RectTransform)t.transform).anchoredPosition = new Vector2(box.x + px - 30f, -top);
        }

        public void End()
        {
            for (int i = used; i < items.Count; i++) items[i].gameObject.SetActive(false);
        }
    }
}

// Attaches the controller when GameplayScene loads, so the scene needs no wiring.
public static class GameplayBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Attach()
    {
        if (SceneManager.GetActiveScene().name == "GameplayScene")
            new GameObject("GameplayController").AddComponent<GameplayController>();
    }
}
