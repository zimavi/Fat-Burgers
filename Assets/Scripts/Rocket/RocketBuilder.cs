using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Two-tree rocket builder on one shared workspace. Both trees sit in bounding frames placed side by side, so they cannot overlap.
// Workspace: right-drag to pan, scroll wheel to zoom around the cursor.
// Right panel: allowed children of the selected node. Drag a module onto a node or free slot to attach it.
// Drag an attached node onto the right panel to detach it (with everything above it).
// Each change re-totals the rocket and re-bakes TrajectoryView.
// Trajectory button: hides the build screen and shows the predicted flight. Set a target altitude and PitchSolver picks the turn.
// Trajectory mode: right-drag pans the camera, scroll wheel zooms around the cursor.
// The UI is built in code, so the scene needs only this component plus its references.
// Available panel scrolls with the mouse wheel. Dragging on a card drags the card, so scroll by wheel or on empty space.
// shortcut: the workspace has no minimap or reset view, and no scrollbar is drawn.
public class RocketBuilder : MonoBehaviour
{
    [Header("Base modules (tree roots)")]
    public ModuleData payloadBase;
    public ModuleData rocketBase;
    [Tooltip("Deepest level a tree may grow to. Safety net against modules that keep stacking on themselves.")]
    [Min(1)] public int maxDepth = 6;

    [Header("Flight")]
    public TrajectoryView trajectory;
    [Tooltip("Supplies the pitch program. Its physical stats are overwritten by the assembled rocket.")]
    public RocketData flightProgram;
    [Tooltip("Blueprint grid behind the workspace. Assign Assets/Sprites/derevo bg.png.")]
    public Sprite blueprintBackground;

    static readonly Color PanelColor = new Color(0.03f, 0.06f, 0.09f, 0.92f);
    static readonly Color FrameColor = new Color(0.05f, 0.09f, 0.13f, 0.6f);
    static readonly Color FrameOutline = new Color(0.4f, 0.5f, 0.6f, 0.8f);
    static readonly Color ButtonColor = new Color(0.12f, 0.18f, 0.24f, 1f);
    static readonly Color DisabledColor = new Color(0.07f, 0.08f, 0.1f, 0.9f);
    static readonly Color SelectedColor = new Color(0.1f, 0.19f, 0.23f, 0.6f);
    static readonly Color DangerColor = new Color(0.55f, 0.15f, 0.12f, 1f);
    static readonly Color SlotColor = new Color(0.1f, 0.14f, 0.18f, 0.5f);
    static readonly Color SlotOutline = new Color(0.45f, 0.55f, 0.65f, 0.9f);
    static readonly Color SlotText = new Color(0.6f, 0.65f, 0.7f, 1f);
    static readonly Color LinkColor = new Color(0.6f, 0.7f, 0.8f, 0.8f);
    static readonly Color IconMass = new Color(0.75f, 0.75f, 0.8f);
    static readonly Color IconFuel = new Color(0.95f, 0.8f, 0.3f);
    static readonly Color IconThrust = new Color(0.9f, 0.3f, 0.25f);
    static readonly Color IconIsp = new Color(0.95f, 0.55f, 0.2f);
    static readonly Color IconDrag = new Color(0.35f, 0.6f, 0.95f);
    static readonly Color IconArea = new Color(0.3f, 0.8f, 0.8f);
    static readonly Color IconScience = new Color(0.5f, 0.9f, 0.5f);

    private static TMP_FontAsset TmpFont = null;
    
    const float GhostAlpha = 0.45f;
    const float CardWidth = 240f;    // node cards and free slots share this rectangle; the card's circle icon is CardHeight across
    const float CardHeight = 84f;
    const float HGap = 24f;          // between neighbouring columns
    const float VGap = 40f;          // between tree rows, where the connector lines run
    const float LinkWidth = 3f;
    const float HeaderHeight = 62f;
    const float BoxPad = 24f;        // frame margin around a tree
    const float BoxGap = 80f;        // between the two tree frames
    const float Step = CardWidth + HGap;     // width of one column
    const float RowHeight = CardHeight + VGap;
    const float TooltipWidth = 320f;
    const float TooltipOffset = 18f; // gap between the cursor and the tooltip corner
    const float ZoomStep = 1.1f;     // per scroll notch
    const float MinZoom = 0.3f;
    const float MaxZoom = 2.5f;
    const float MinOrtho = 0.5f;     // trajectory camera zoom limits, orthographic half-height in world units
    const float MaxOrtho = 5000f;

    PartNode payloadRoot, rocketRoot, selected;
    RocketData runtimeRocket;
    Canvas canvas;
    RectTransform viewport, world, availableList, dragLayer;
    RectTransform payloadGhost, rocketGhost;  // static background stacks: left and right halves of the workspace
    float contentW, contentH;
    bool centered, panning;

    // Trajectory mode: the build screen is hidden, the camera frames the predicted flight, and the turn is solved for targetAltitude.
    RectTransform availablePanel;
    float targetAltitude = 100000f;  // m
    bool trajectoryMode;
    GameObject targetRow;
    TextMeshProUGUI toggleLabel, targetLabel, resultLabel, missionLabel, launchLabel;
    Button launchButton;
    Camera cam;
    bool savedOrtho;
    float savedSize;
    Vector3 savedPos;

    // Hover tooltip: one shared panel that follows the cursor while a card is hovered.
    RectTransform tooltipRect;
    bool tooltipShown;
    static Sprite circleSprite;

    void Start()
    {
        if (!RocketAssembly.SelfCheck()) Debug.LogError("RocketAssembly self-check failed");
        if (!payloadBase || !rocketBase)
        {
            Debug.LogError("RocketBuilder needs payloadBase and rocketBase assigned.", this);
            enabled = false;
            return;
        }
        // UI pointer input needs an EventSystem using the new Input System module, not StandaloneInputModule.
        var events = FindFirstObjectByType<EventSystem>();
        if (!events) events = new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
        var legacy = events.GetComponent<StandaloneInputModule>();
        if (legacy) Destroy(legacy);
        if (!events.GetComponent<InputSystemUIInputModule>()) events.gameObject.AddComponent<InputSystemUIInputModule>();

        runtimeRocket = ScriptableObject.CreateInstance<RocketData>();
        runtimeRocket.pitchProgram = flightProgram ? flightProgram.pitchProgram : null;
        // A design from an earlier visit (the gameplay scene, a mission) is kept, so returning here does not start over.
        payloadRoot = MissionSession.PayloadTree ?? new PartNode(payloadBase, null);
        rocketRoot = MissionSession.RocketTree ?? new PartNode(rocketBase, null);
        selected = rocketRoot;

        BuildLayout();
        Refresh();
        // The doctor's first builder line, once per player, as soon as the builder opens.
        if (!Story.Seen("calculate"))
        {
            Story.MarkSeen("calculate");
            DialogueBox.Play(new[] { Story.Calculate }, null);
        }
    }

    void OnDestroy()
    {
        if (runtimeRocket && runtimeRocket != MissionSession.Rocket) Destroy(runtimeRocket);  // a handed-over rocket lives on in the gameplay scene
    }

    // Hands the rocket to the gameplay scene. The mission, if any, travels in MissionSession.
    void LaunchMission()
    {
        if (MissionEvaluator.LaunchBlocker(MissionSession.Mission, runtimeRocket) != null) return;
        if (!Story.Seen("finished"))
        {
            Story.MarkSeen("finished");
            DialogueBox.Play(new[] { Story.Finished }, GoToGameplay);
            return;
        }
        GoToGameplay();
    }

    void GoToGameplay()
    {
        MissionSession.Rocket = runtimeRocket;
        MissionSession.RocketTree = rocketRoot;
        MissionSession.PayloadTree = payloadRoot;
        MissionSession.Planet = trajectory ? trajectory.planet : null;
        SceneManager.LoadScene("GameplayScene");
    }

    void BuildLayout()
    {
        // Without GraphicRaycaster the canvas never receives pointer events, so no button or card reacts.
        var canvasGo = new GameObject("RocketBuilderCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        var root = (RectTransform)canvasGo.transform;

        // Workspace: the mask clips it, and the world pans and zooms. The blueprint grid sits behind everything.
        viewport = MakePanel(root, new Vector2(0f, 0f), new Vector2(0.75f, 1f), "Workspace", PanelColor);
        viewport.gameObject.AddComponent<RectMask2D>();
        if (blueprintBackground)
        {
            var grid = viewport.GetComponent<Image>();
            grid.sprite = blueprintBackground;
            grid.color = Color.white;
        }
        payloadGhost = GhostRoot(viewport, 0.25f);  // static: not inside the world, so it does not pan or zoom
        rocketGhost = GhostRoot(viewport, 0.75f);
        world = NewRect(viewport, "World", new Vector2(0.5f, 0.5f));

        // Available panel: a ScrollRect whose content grows with the list. The panel is also the drop target for detaching nodes.
        availablePanel = MakePanel(root, new Vector2(0.75f, 0f), new Vector2(1f, 1f), "Available", PanelColor);
        availablePanel.gameObject.AddComponent<PanelDrop>().owner = this;
        var scroll = availablePanel.gameObject.AddComponent<ScrollRect>();
        var viewportRect = MakePanel(availablePanel, Vector2.zero, Vector2.one, "AvailableViewport", null);
        viewportRect.gameObject.AddComponent<RectMask2D>();
        var content = MakePanel(viewportRect, new Vector2(0f, 1f), new Vector2(1f, 1f), "AvailableContent", null);
        content.pivot = new Vector2(0.5f, 1f);
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = viewportRect;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 40f;
        availableList = Column(content);

        // Toolbar in the top-left corner: the view toggle, and in trajectory mode the target controls.
        var toolbar = MakePanel(root, new Vector2(0f, 1f), new Vector2(0f, 1f), "Toolbar", PanelColor);
        toolbar.pivot = new Vector2(0f, 1f);
        toolbar.anchoredPosition = new Vector2(16f, -16f);
        var bar = toolbar.gameObject.AddComponent<VerticalLayoutGroup>();
        bar.padding = new RectOffset(10, 10, 10, 10);
        bar.spacing = 6f;
        bar.childControlWidth = true;
        bar.childControlHeight = true;
        bar.childForceExpandWidth = false;
        bar.childForceExpandHeight = false;
        var barFit = toolbar.gameObject.AddComponent<ContentSizeFitter>();
        barFit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        barFit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        toggleLabel = MakeButton(Row(toolbar), "Trajectory", ButtonColor, ToggleTrajectory);
        launchLabel = MakeButton(Row(toolbar), "Launch to gameplay", ButtonColor, LaunchMission);
        launchButton = launchLabel.GetComponentInParent<Button>();
        MakeButton(Row(toolbar), "Missions", ButtonColor, () => SceneManager.LoadScene("MissionSelect"));
        var targets = Row(toolbar);
        targetRow = targets.gameObject;
        MakeButton(targets, "-100 km", ButtonColor, () => AdjustTarget(-100f));
        MakeButton(targets, "-10 km", ButtonColor, () => AdjustTarget(-10f));
        targetLabel = Label(targets, "", 16f);
        MakeButton(targets, "+10 km", ButtonColor, () => AdjustTarget(10f));
        MakeButton(targets, "+100 km", ButtonColor, () => AdjustTarget(100f));
        resultLabel = Label(toolbar, "", 14f);
        resultLabel.gameObject.AddComponent<LayoutElement>().preferredWidth = 460f;  // wrap long results instead of widening the bar
        targetRow.SetActive(false);
        resultLabel.gameObject.SetActive(false);
        missionLabel = Label(toolbar, "", 14f);
        missionLabel.gameObject.AddComponent<LayoutElement>().preferredWidth = 460f;
        missionLabel.text = MissionSession.Mission
            ? $"<b>{MissionSession.Mission.title}</b>\n{MissionSession.Mission.briefing}{MissionEvaluator.ModuleStatus(MissionSession.Mission)}"
            : "<b>Free flight</b>  no mission, no objectives.";

        dragLayer = MakePanel(root, Vector2.zero, Vector2.one, "DragLayer", null);  // so dragged cards draw on top
        BuildTooltip(root);  // last, so the tooltip draws over everything
    }

    // Right-drag pans the workspace, the scroll wheel zooms around the cursor.
    // Polled from Mouse.current, like TrajectoryView, so panning works even when the press lands on a card.
    void Update()
    {
        if (Mouse.current == null) return;
        if (tooltipShown) PositionTooltip();
        if (trajectoryMode)
        {
            NavigateCamera();
            return;
        }
        if (!world) return;
        // Cursor position in the viewport's local space (origin at its pivot, the centre). Overlay canvas: screen px = world units.
        Vector2 local = viewport.InverseTransformPoint(Mouse.current.position.ReadValue());
        bool inside = viewport.rect.Contains(local);

        if (Mouse.current.rightButton.wasPressedThisFrame) panning = inside;
        if (!Mouse.current.rightButton.isPressed) panning = false;
        if (panning) world.anchoredPosition += Mouse.current.delta.ReadValue() / canvas.scaleFactor;

        float scroll = Mouse.current.scroll.ReadValue().y;
        if (inside && scroll != 0f) Zoom(local, Mathf.Sign(scroll) > 0f ? ZoomStep : 1f / ZoomStep);
    }

    // Keeps the workspace point under the cursor fixed while the scale changes. local is in viewport space.
    void Zoom(Vector2 local, float factor)
    {
        float s0 = world.localScale.x;
        float s1 = Mathf.Clamp(s0 * factor, MinZoom, MaxZoom);
        if (Mathf.Approximately(s0, s1)) return;

        Vector2 worldPoint = (local - world.anchoredPosition) / s0;
        world.localScale = Vector3.one * s1;
        world.anchoredPosition = local - worldPoint * s1;
    }

    // Shows the predicted flight in place of the build screen, or returns to the build screen.
    void ToggleTrajectory()
    {
        trajectoryMode = !trajectoryMode;
        viewport.gameObject.SetActive(!trajectoryMode);
        availablePanel.gameObject.SetActive(!trajectoryMode);
        targetRow.SetActive(trajectoryMode);
        resultLabel.gameObject.SetActive(trajectoryMode);
        toggleLabel.text = trajectoryMode ? "Build" : "Trajectory";

        if (trajectoryMode)
        {
            SolveAndBake();  // solve first, so the camera frames the new flight
            BeginCamera();
        }
        else EndCamera();
    }

    void AdjustTarget(float km)
    {
        targetAltitude = Mathf.Clamp(targetAltitude + km * 1000f, 1000f, 2000000f);
        if (trajectoryMode) SolveAndBake();
    }

    // Solves the turn for the target altitude, then bakes the flight with that turn.
    void SolveAndBake()
    {
        targetLabel.text = $"Target {targetAltitude / 1000f:0} km";
        if (!trajectory || !trajectory.planet)
        {
            resultLabel.text = "Assign the planet on TrajectoryView to solve.";
            return;
        }

        var r = PitchSolver.Solve(runtimeRocket, trajectory.planet, targetAltitude);
        runtimeRocket.pitchProgram = PitchSolver.Program(r.TurnStart, r.TurnDeg);
        trajectory.rocket = runtimeRocket;
        trajectory.Bake();

        float off = Mathf.Abs(r.Apogee - targetAltitude) / 1000f;
        resultLabel.text = $"Apogee {r.Apogee / 1000f:0.0} km, {off:0.0} km from target\n" +
                           $"Vertical until {r.TurnStart:0.0} s, then turn to {r.TurnDeg:0.0}°";
    }

    // Switches the camera to orthographic and frames the trajectory. The previous camera state is restored by EndCamera.
    void BeginCamera()
    {
        cam = trajectory && trajectory.viewCamera ? trajectory.viewCamera : Camera.main;
        if (!cam)
        {
            resultLabel.text = "No camera found for the trajectory view.";
            return;
        }
        savedOrtho = cam.orthographic;
        savedSize = cam.orthographicSize;
        savedPos = cam.transform.position;
        cam.orthographic = true;

        var line = trajectory.GetComponent<LineRenderer>();
        Bounds b = line && line.positionCount > 0 ? line.bounds : new Bounds(trajectory.transform.position, Vector3.one);
        float halfHeight = Mathf.Max(Mathf.Max(b.extents.y, b.extents.x / cam.aspect), 1f);  // fit both width and height
        cam.orthographicSize = Mathf.Clamp(halfHeight * 1.15f, MinOrtho, MaxOrtho);
        cam.transform.position = new Vector3(b.center.x, b.center.y, cam.transform.position.z);
    }

    void EndCamera()
    {
        if (!cam) return;
        cam.orthographic = savedOrtho;
        cam.orthographicSize = savedSize;
        cam.transform.position = savedPos;
        cam = null;
    }

    // Trajectory mode: right-drag pans, scroll wheel zooms around the cursor. Pan speed follows the zoom level.
    void NavigateCamera()
    {
        if (!cam) return;
        Vector2 mouse = Mouse.current.position.ReadValue();
        if (Mouse.current.rightButton.wasPressedThisFrame) panning = true;
        if (!Mouse.current.rightButton.isPressed) panning = false;
        if (panning)
        {
            Vector2 d = Mouse.current.delta.ReadValue();
            float unitsPerPixel = 2f * cam.orthographicSize / cam.pixelHeight;
            cam.transform.position += new Vector3(-d.x, -d.y, 0f) * unitsPerPixel;
        }

        float scroll = Mouse.current.scroll.ReadValue().y;
        if (scroll != 0f)
        {
            Vector3 before = cam.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, 10f));
            cam.orthographicSize = Mathf.Clamp(cam.orthographicSize * (scroll > 0f ? 1f / ZoomStep : ZoomStep), MinOrtho, MaxOrtho);
            Vector3 after = cam.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, 10f));
            cam.transform.position += before - after;
        }
    }

    // Rebuilds both trees, their frames and the available panel from the current state.
    void Refresh()
    {
        HideTooltip();  // the cards it was hovering are about to be destroyed
        RocketAssembly.Apply(rocketRoot, payloadRoot, runtimeRocket);
        if (trajectory)
        {
            trajectory.rocket = runtimeRocket;
            trajectory.Bake();
        }

        Clear(world);
        Vector2 payload = DrawTreeBox(payloadRoot, 0f, "<size=18>PAYLOAD</size>");
        string totals = $"Mass {runtimeRocket.dryMass + runtimeRocket.fuelMass:0} kg  |  Thrust {runtimeRocket.thrust:0} N";
        if (runtimeRocket.thrust > 0f) totals += $"  |  Isp {runtimeRocket.isp:0} s";
        Vector2 rocket = DrawTreeBox(rocketRoot, payload.x + BoxGap, $"<size=18>SOUNDING ROCKET</size>\n<size=14>{totals}</size>");
        contentW = payload.x + BoxGap + rocket.x;
        contentH = Math.Max(payload.y, rocket.y);

        // Start with both trees centred in the workspace. After that, the view stays where the user left it.
        if (!centered)
        {
            world.anchoredPosition = new Vector2(-contentW / 2f, contentH / 2f);
            centered = true;
        }

        DrawStack(payloadGhost, payloadRoot);
        DrawStack(rocketGhost, rocketRoot);
        Clear(availableList);
        DrawAvailable();
        UpdateLaunchGate();
    }

    // Launch stays blocked until the mission's requirement is met, and the button says what is missing.
    void UpdateLaunchGate()
    {
        string blocker = MissionEvaluator.LaunchBlocker(MissionSession.Mission, runtimeRocket);
        launchButton.interactable = blocker == null;
        launchLabel.text = blocker ?? "Launch to gameplay";
    }

    // One tree in a bounding frame. The frame's left edge sits at left, in world space.
    // Returns the frame size. Frames are placed from measured sizes, so they cannot overlap.
    Vector2 DrawTreeBox(PartNode root, float left, string title)
    {
        var container = NewRect(world, "Tree", new Vector2(0.5f, 0.5f));
        Vector2 size = DrawDiagram(container, root);
        var frameSize = new Vector2(size.x + 2f * BoxPad, size.y + 2f * BoxPad + HeaderHeight);
        container.anchoredPosition = new Vector2(left + frameSize.x / 2f, 0f);

        var frameGo = new GameObject("Frame", typeof(RectTransform), typeof(Image), typeof(Outline));
        frameGo.transform.SetParent(container, false);
        frameGo.GetComponent<Image>().color = FrameColor;
        var outline = frameGo.GetComponent<Outline>();
        outline.effectColor = FrameOutline;
        outline.effectDistance = new Vector2(2f, -2f);
        var frame = (RectTransform)frameGo.transform;
        frame.anchorMin = frame.anchorMax = new Vector2(0.5f, 1f);
        frame.pivot = new Vector2(0.5f, 1f);
        frame.sizeDelta = frameSize;
        frame.anchoredPosition = new Vector2(0f, BoxPad + HeaderHeight);
        frame.SetAsFirstSibling();  // behind the cards and links

        Header(frame).text = title;
        return frameSize;
    }

    // Lays the tree out as a diagram. A subtree takes the columns of its children, then of its free slots,
    // and each node is centred over that span. Free slots are empty rectangles, one per allowed child not yet attached.
    // Positions are relative to area's top-centre. Returns the size of the diagram without its frame.
    Vector2 DrawDiagram(RectTransform area, PartNode root)
    {
        var nodes = new List<(PartNode node, float col, int depth)>();
        var slots = new List<(PartNode parent, float col, int depth)>();
        var groups = new List<(float col, int depth, List<float> childCols)>();
        int cols = 0;

        float Place(PartNode n, int depth)
        {
            int start = cols;
            var childCols = new List<float>();
            foreach (var c in n.children) childCols.Add(Place(c, depth + 1));
            bool canGrow = depth + 1 < maxDepth;
            int free = canGrow ? Math.Max(0, SlotCount(n) - n.children.Count) : 0;
            for (int i = 0; i < free; i++)
            {
                float col = cols + 0.5f;
                slots.Add((n, col, depth + 1));
                childCols.Add(col);
                cols++;
            }
            if (cols == start) cols++;  // a leaf still takes one column
            float center = (start + cols) / 2f;
            nodes.Add((n, center, depth));
            if (childCols.Count > 0) groups.Add((center, depth, childCols));
            return center;
        }

        Place(root, 0);
        float X(float col) => (col - cols / 2f) * Step;
        float Y(int depth) => -depth * RowHeight;  // top edge of a row

        foreach (var g in groups)
        {
            float px = X(g.col);
            float bottom = Y(g.depth) - CardHeight;
            float midY = bottom - VGap / 2f;
            Link(area, new Vector2(px, bottom), new Vector2(px, midY));
            var xs = g.childCols.Select(c => X(c)).ToList();
            Link(area, new Vector2(xs.Min(), midY), new Vector2(xs.Max(), midY));
            foreach (var x in xs) Link(area, new Vector2(x, midY), new Vector2(x, Y(g.depth + 1)));
        }
        foreach (var s in slots) FreeSlot(area, s.parent, new Vector2(X(s.col), Y(s.depth)));
        foreach (var item in nodes) NodeCard(area, item.node, new Vector2(X(item.col), Y(item.depth)));

        int maxRow = Math.Max(nodes.Max(t => t.depth), slots.Count > 0 ? slots.Max(t => t.depth) : 0);
        return new Vector2(cols * Step - HGap, maxRow * RowHeight + CardHeight);
    }

    void NodeCard(RectTransform area, PartNode n, Vector2 pos)
    {
        var card = MakeCard(area, n.data, n == selected ? SelectedColor : ButtonColor, () =>
        {
            selected = n;
            Refresh();
        });
        Position(card.transform, pos);

        var drop = card.AddComponent<NodeDrop>();
        drop.owner = this;
        drop.node = n;
        if (n.parent != null)  // the root cannot be detached
        {
            var drag = card.AddComponent<CardDrag>();
            drag.owner = this;
            drag.module = n.data;
            drag.attached = n;
        }
    }

    // Empty slot under a node. Dropping a module here attaches it; clicking selects the parent.
    void FreeSlot(RectTransform area, PartNode parent, Vector2 pos)
    {
        var go = new GameObject("Free slot", typeof(RectTransform), typeof(Image), typeof(Outline), typeof(Button));
        go.transform.SetParent(area, false);
        go.GetComponent<Image>().color = SlotColor;
        var outline = go.GetComponent<Outline>();
        outline.effectColor = SlotOutline;
        outline.effectDistance = new Vector2(2f, -2f);
        Position(go.transform, pos);

        var drop = go.AddComponent<NodeDrop>();
        drop.owner = this;
        drop.node = parent;
        go.GetComponent<Button>().onClick.AddListener(() =>
        {
            selected = parent;
            Refresh();
        });

        var label = Label(go.transform, "Free slot", 13f);
        label.color = SlotText;
        label.alignment = TextAlignmentOptions.Center;
        Fill((RectTransform)label.transform);
    }

    void DrawAvailable()
    {
        Label(availableList, "AVAILABLE", 18f);
        Label(availableList, $"Attach to: {Colored(selected.data)}", 14f);

        if (selected.parent != null)
        {
            MakeButton(availableList, "Remove this module and everything above it", DangerColor, () => Detach(selected));
        }

        var parent = selected;
        string blocked = WhyBlocked(parent);
        if (blocked != null) Label(availableList, blocked, 14f);

        int shown = 0;
        foreach (var m in parent.data.allowedChildren ?? Array.Empty<ModuleData>())
        {
            if (!m) continue;
            var module = m;
            bool ok = CanAttach(parent, module);
            var card = MakeCard(availableList, module, ok ? ButtonColor : DisabledColor, () => Attach(parent, module));
            card.GetComponent<Button>().interactable = ok;
            if (ok)
            {
                var drag = card.AddComponent<CardDrag>();
                drag.owner = this;
                drag.module = module;
            }
            shown++;
        }
        if (shown == 0) Label(availableList, "No modules can attach to this node.", 14f);
    }

    // Attaches only if CanAttach allows it. Selects the new node.
    void Attach(PartNode parent, ModuleData m)
    {
        if (!CanAttach(parent, m)) return;
        selected = parent.AddChild(m);
        Refresh();
    }

    // Listed in the parent's allowedChildren, a free child slot, room below the depth limit,
    // and under the module's instance cap across the whole build.
    bool CanAttach(PartNode parent, ModuleData m)
    {
        if (WhyBlocked(parent) != null) return false;
        if (parent.data.allowedChildren == null || Array.IndexOf(parent.data.allowedChildren, m) < 0) return false;
        return m.maxInstances == 0 || CountOf(m) < m.maxInstances;
    }

    // Reason no child can be attached under this node right now, or null if one can.
    string WhyBlocked(PartNode parent)
    {
        if (Depth(parent) + 1 >= maxDepth) return "Maximum tree depth reached.";
        if (parent.children.Count >= SlotCount(parent)) return $"Node is full ({SlotCount(parent)} children max).";
        return null;
    }

    // Child positions a node offers: its maxChildren cap, or all of allowedChildren when maxChildren is 0.
    static int SlotCount(PartNode n)
    {
        int allowed = n.data.allowedChildren?.Length ?? 0;
        return n.data.maxChildren > 0 ? Math.Min(allowed, n.data.maxChildren) : allowed;
    }

    static int Depth(PartNode n)
    {
        int d = 0;
        for (var p = n.parent; p != null; p = p.parent) d++;
        return d;
    }

    int CountOf(ModuleData m) => rocketRoot.Walk().Concat(payloadRoot.Walk()).Count(n => n.data == m);

    // Removes a node and its subtree. Selection moves to the parent if it was inside the removed part.
    void Detach(PartNode node)
    {
        if (node.parent == null) return;
        var p = node.parent;
        if (node.Walk().Contains(selected)) selected = p;
        node.Remove();
        Refresh();
    }

    // Anchored at the middle of its half of the workspace, with the stack's bottom-centre at the anchor.
    static RectTransform GhostRoot(Transform parent, float anchorX)
    {
        var r = NewRect(parent, "Ghost", new Vector2(0.5f, 0f));
        r.anchorMin = r.anchorMax = new Vector2(anchorX, 0.5f);
        return r;
    }

    // Rebuilds one tree's static ghost, centred vertically in its half.
    static void DrawStack(RectTransform ghostRoot, PartNode root)
    {
        Clear(ghostRoot);
        float height = StackGhost(ghostRoot, root.Walk().Select(n => n.data));
        ghostRoot.anchoredPosition = new Vector2(0f, -height / 2f);
    }

    // Modules stacked bottom-up by stackOrder. OrderBy is stable, so equal orders keep tree order (base first). Returns the stack height.
    static float StackGhost(Transform parent, IEnumerable<ModuleData> modules)
    {
        float y = 0f;
        foreach (var d in modules.OrderBy(d => d.stackOrder))
        {
            GhostImage(parent, d, 0f, y, d.size);
            y += d.size.y;
        }
        return y;
    }

    // Positions are relative to the bottom-centre of parent.
    static void GhostImage(Transform parent, ModuleData m, float x, float y, Vector2 size)
    {
        var go = new GameObject(m.displayName, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = size;

        var img = go.GetComponent<Image>();
        img.sprite = m.sprite;
        img.preserveAspect = true;
        Color c = m.sprite ? Color.white : m.placeholderColor;
        img.color = new Color(c.r, c.g, c.b, GhostAlpha);
        img.raycastTarget = false;
    }

    // Card: a circle icon on the left, and the name in a longer box to its right. Stats and description show in the tooltip on hover.
    /*
     *   ( Icon ) Name
     *   ( ---- ) ---------------
     */
    GameObject MakeCard(Transform parent, ModuleData m, Color bg, Action onClick)
    {
        // The root takes clicks and drops. Its image is invisible, so the visible parts are children.
        var go = new GameObject(m.displayName, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = Color.clear;
        var button = go.GetComponent<Button>();
        button.transition = Selectable.Transition.None;  // no colour tint: the body colour shows the state
        button.onClick.AddListener(() => onClick());
        var size = go.GetComponent<LayoutElement>();
        size.minWidth = size.preferredWidth = CardWidth;
        size.minHeight = size.preferredHeight = CardHeight;

        // Name box: starts at the circle's centre, so the circle overlaps its left end.
        var bodyGo = new GameObject("Body", typeof(RectTransform), typeof(Image));
        bodyGo.transform.SetParent(go.transform, false);
        var body = (RectTransform)bodyGo.transform;
        Fill(body);
        body.offsetMin = new Vector2(CardHeight / 2f, 0f);
        var bodyImage = bodyGo.GetComponent<Image>();
        bodyImage.color = bg;
        bodyImage.raycastTarget = false;

        // Circle icon: the module colour, with the module's icon centred on it when one is assigned.
        var circleGo = new GameObject("Circle", typeof(RectTransform), typeof(Image));
        circleGo.transform.SetParent(go.transform, false);
        var circle = (RectTransform)circleGo.transform;
        circle.anchorMin = circle.anchorMax = new Vector2(0f, 0.5f);
        circle.pivot = new Vector2(0f, 0.5f);
        circle.anchoredPosition = Vector2.zero;
        circle.sizeDelta = new Vector2(CardHeight, CardHeight);
        var circleImage = circleGo.GetComponent<Image>();
        circleImage.sprite = CircleSprite();
        circleImage.color = m.placeholderColor;
        circleImage.raycastTarget = false;

        var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
        iconGo.transform.SetParent(circleGo.transform, false);
        var iconRect = (RectTransform)iconGo.transform;
        iconRect.anchorMin = iconRect.anchorMax = iconRect.pivot = new Vector2(0.5f, 0.5f);
        iconRect.anchoredPosition = Vector2.zero;
        iconRect.sizeDelta = new Vector2(CardHeight * 0.8f, CardHeight * 0.8f);
        var iconImage = iconGo.GetComponent<Image>();
        iconImage.sprite = m.icon;
        iconImage.preserveAspect = true;
        iconImage.raycastTarget = false;
        iconGo.SetActive(m.icon != null);

        var name = Label(go.transform, $"<b>{Colored(m)}</b>", 17f);
        name.alignment = TextAlignmentOptions.MidlineLeft;
        var nameRect = (RectTransform)name.transform;
        Fill(nameRect);
        nameRect.offsetMin = new Vector2(CardHeight + 10f, 6f);
        nameRect.offsetMax = new Vector2(-12f, -6f);

        var hover = go.AddComponent<ModuleHover>();
        hover.owner = this;
        hover.module = m;
        return go;
    }

    // Soft-edged disc, generated once, so no sprite asset is needed for the card's circle.
    static Sprite CircleSprite()
    {
        if (circleSprite) return circleSprite;
        const int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color32[n * n];
        float c = (n - 1) / 2f, radius = n / 2f - 2f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                pixels[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(radius - d + 0.5f) * 255f));
            }
        tex.SetPixels32(pixels);
        tex.Apply();
        circleSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        return circleSprite;
    }

    // Tooltip: module name as the title, the description, then the stats in the same squares as before.
    void BuildTooltip(RectTransform root)
    {
        var go = new GameObject("Tooltip", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(CanvasGroup));
        go.transform.SetParent(root, false);
        tooltipRect = (RectTransform)go.transform;
        tooltipRect.anchorMin = tooltipRect.anchorMax = Vector2.zero;  // bottom-left origin, so the cursor maps straight on
        tooltipRect.pivot = new Vector2(0f, 1f);                       // top-left corner sits at the cursor
        tooltipRect.sizeDelta = new Vector2(TooltipWidth, 0f);
        var image = go.GetComponent<Image>();
        image.color = PanelColor;
        image.raycastTarget = false;
        go.GetComponent<CanvasGroup>().blocksRaycasts = false;  // never steal the pointer from the card under it
        var layout = go.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(12, 12, 10, 10);
        layout.spacing = 6f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        go.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        go.SetActive(false);
    }

    void ShowTooltip(ModuleData m)
    {
        Clear(tooltipRect);
        Label(tooltipRect, $"<b>{Colored(m)}</b>", 20f);
        if (!string.IsNullOrWhiteSpace(m.description)) Label(tooltipRect, m.description, 14f).color = SlotText;
        if (Array.IndexOf(ReliabilityStore.Rolled, m.specialType) >= 0)
        {
            var t = m.specialType;
            Label(tooltipRect, $"Reliability {ReliabilityStore.Get(t):0}%. Last flight: {ReliabilityStore.LastText(t)}.", 14f).color = SlotText;
        }

        var items = StatItems(m);
        for (int i = 0; i < items.Count; i += 3)
        {
            var row = new GameObject("Stats", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(tooltipRect, false);
            var h = row.GetComponent<HorizontalLayoutGroup>();
            h.spacing = 10f;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;
            for (int j = i; j < Math.Min(i + 3, items.Count); j++) StatCell(row.transform, items[j].Item1, items[j].Item2);
        }

        tooltipRect.gameObject.SetActive(true);
        tooltipShown = true;
        PositionTooltip();
    }

    void HideTooltip()
    {
        if (!tooltipRect) return;
        tooltipShown = false;
        tooltipRect.gameObject.SetActive(false);
    }

    // Keeps the tooltip beside the cursor, flipping to the other side when it would leave the canvas.
    void PositionTooltip()
    {
        Vector2 mouse = Mouse.current.position.ReadValue() / canvas.scaleFactor;  // canvas units, bottom-left origin
        var canvasRect = (RectTransform)canvas.transform;
        float w = tooltipRect.rect.width, h = tooltipRect.rect.height;
        float x = mouse.x + TooltipOffset;
        if (x + w > canvasRect.rect.width) x = mouse.x - TooltipOffset - w;
        float top = mouse.y - TooltipOffset;  // pivot is top-left, so anchoredPosition.y is the top edge
        if (top - h < 0f) top = h;
        tooltipRect.anchoredPosition = new Vector2(x, top);
    }

    // Stat with a placeholder icon: a coloured square until real icons exist.
    static void StatCell(Transform parent, Color iconColor, string text)
    {
        var cell = new GameObject("Stat", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        cell.transform.SetParent(parent, false);
        var h = cell.GetComponent<HorizontalLayoutGroup>();
        h.spacing = 4f;
        h.childControlWidth = true;
        h.childControlHeight = true;
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = false;

        var icon = new GameObject("Icon", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        icon.transform.SetParent(cell.transform, false);
        var img = icon.GetComponent<Image>();
        img.color = iconColor;
        img.raycastTarget = false;
        var le = icon.GetComponent<LayoutElement>();
        le.minWidth = le.preferredWidth = 10f;
        le.minHeight = le.preferredHeight = 10f;

        Label(cell.transform, text, 13f).textWrappingMode = TextWrappingModes.NoWrap;
    }

    static List<(Color, string)> StatItems(ModuleData m)
    {
        var list = new List<(Color, string)> { (IconMass, $"{m.mass:0.#} kg") };
        if (m.fuelMass > 0f) list.Add((IconFuel, $"{m.fuelMass:0.#} kg fuel"));
        if (m.thrust > 0f)
        {
            list.Add((IconThrust, $"{m.thrust:0} N"));
            list.Add((IconIsp, $"Isp {m.isp:0} s"));
        }
        if (m.referenceArea > 0f)
        {
            list.Add((IconDrag, $"Cd {m.dragCoefficient:0.00}"));
            list.Add((IconArea, $"{m.referenceArea:0.000} m2"));
        }
        if (m.scienceValue > 0f) list.Add((IconScience, $"Sci {m.scienceValue:0.#}"));
        return list;
    }

    // Module name in its placeholder colour, as TextMesh Pro rich text.
    static string Colored(ModuleData m) =>
        $"<color=#{ColorUtility.ToHtmlStringRGB(m.placeholderColor)}>{m.displayName}</color>";

    // Puts a card's top-centre at pos inside a tree container. Y grows downward from the top edge.
    static void Position(Transform t, Vector2 pos)
    {
        var r = (RectTransform)t;
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 1f);
        r.pivot = new Vector2(0.5f, 1f);
        r.sizeDelta = new Vector2(CardWidth, CardHeight);
        r.anchoredPosition = pos;
    }

    // Axis-aligned connector from a to b.
    static void Link(RectTransform area, Vector2 a, Vector2 b)
    {
        var go = new GameObject("Link", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(area, false);
        var img = go.GetComponent<Image>();
        img.color = LinkColor;
        img.raycastTarget = false;
        var r = (RectTransform)go.transform;
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 1f);
        r.anchoredPosition = (a + b) / 2f;
        r.sizeDelta = new Vector2(Mathf.Max(Mathf.Abs(b.x - a.x), LinkWidth), Mathf.Max(Mathf.Abs(b.y - a.y), LinkWidth));
    }

    // Title strip across the top of a tree frame.
    static TextMeshProUGUI Header(RectTransform frame)
    {
        var t = Label(frame, "", 18f);
        var r = (RectTransform)t.transform;
        r.anchorMin = new Vector2(0f, 1f);
        r.anchorMax = new Vector2(1f, 1f);
        r.pivot = new Vector2(0.5f, 1f);
        r.offsetMin = new Vector2(12f, -HeaderHeight);
        r.offsetMax = new Vector2(-12f, -6f);
        t.alignment = TextAlignmentOptions.TopLeft;
        return t;
    }

    // Empty rect with the given pivot, centred on its anchor, with zero size.
    static RectTransform NewRect(Transform parent, string name, Vector2 pivot)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var r = (RectTransform)go.transform;
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
        r.pivot = pivot;
        return r;
    }

    static void Fill(RectTransform r)
    {
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.offsetMin = r.offsetMax = Vector2.zero;
    }

    static RectTransform MakePanel(Transform parent, Vector2 min, Vector2 max, string name, Color? color)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        if (color.HasValue) go.AddComponent<Image>().color = color.Value;
        return rect;
    }

    static RectTransform Column(RectTransform panel)
    {
        var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(12, 12, 12, 12);
        layout.spacing = 6f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return panel;
    }

    // Returns the button's label, so callers can change the text later.
    static TextMeshProUGUI MakeButton(Transform parent, string text, Color color, Action onClick)
    {
        var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;
        var layout = go.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(10, 10, 6, 6);
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandHeight = false;
        go.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var label = Label(go.transform, text, 14f);
        label.alignment = TextAlignmentOptions.TopLeft;
        go.GetComponent<Button>().onClick.AddListener(() => onClick());
        return label;
    }

    // Horizontal strip of buttons and labels, sized to its contents.
    static RectTransform Row(Transform parent)
    {
        var go = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        go.transform.SetParent(parent, false);
        var h = go.GetComponent<HorizontalLayoutGroup>();
        h.spacing = 6f;
        h.childControlWidth = true;
        h.childControlHeight = true;
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = false;
        return (RectTransform)go.transform;
    }

    static TextMeshProUGUI Label(Transform parent, string text, float size)
    {
        if (!TmpFont)
        {
            TmpFont = FontFactory.GetPixelifyFont();
        }
        
        var go = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = size;
        tmp.font = TmpFont;
        tmp.textWrappingMode = TextWrappingModes.Normal;
        tmp.raycastTarget = false;
        return tmp;
    }

    static void Clear(Transform t)
    {
        for (int i = t.childCount - 1; i >= 0; i--)
        {
            var c = t.GetChild(i);
            c.SetParent(null, false);  // leaves the layout at once; Destroy finishes the job
            Destroy(c.gameObject);
        }
    }

    // Drag source for a module card. From the available panel it attaches on drop.
    // From a tree node (attached set) it detaches on drop on the available panel.
    // Only the left button drags cards. The panels are rebuilt when the drag ends, whatever the outcome.
    public class CardDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public RocketBuilder owner;
        public ModuleData module;
        public PartNode attached;  // null when the card comes from the available panel
        bool dragging;

        public void OnBeginDrag(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left) return;
            dragging = true;
            owner.HideTooltip();
            var group = gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;  // lets the drop target under the pointer receive the drop
            group.alpha = 0.7f;
            ((RectTransform)transform).pivot = new Vector2(0.5f, 0.5f);
            transform.SetParent(owner.dragLayer, true);
            transform.SetAsLastSibling();
        }

        public void OnDrag(PointerEventData e)
        {
            if (dragging) transform.position = e.position;
        }

        public void OnEndDrag(PointerEventData e)
        {
            if (!dragging) return;
            owner.Refresh();
            Destroy(gameObject);
        }
    }

    // Drop target on a node card or free slot. Accepts a module dragged from the available panel.
    public class NodeDrop : MonoBehaviour, IDropHandler
    {
        public RocketBuilder owner;
        public PartNode node;

        public void OnDrop(PointerEventData e)
        {
            var drag = e.pointerDrag ? e.pointerDrag.GetComponent<CardDrag>() : null;
            if (drag && drag.attached == null) owner.Attach(node, drag.module);
        }
    }

    // Shows the module tooltip while the pointer is over a card.
    public class ModuleHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public RocketBuilder owner;
        public ModuleData module;

        public void OnPointerEnter(PointerEventData e) => owner.ShowTooltip(module);
        public void OnPointerExit(PointerEventData e) => owner.HideTooltip();
    }

    // Drop target on the available panel. Accepts an attached node dragged back out of the tree.
    public class PanelDrop : MonoBehaviour, IDropHandler
    {
        public RocketBuilder owner;

        public void OnDrop(PointerEventData e)
        {
            var drag = e.pointerDrag ? e.pointerDrag.GetComponent<CardDrag>() : null;
            if (drag && drag.attached != null) owner.Detach(drag.attached);
        }
    }
}
