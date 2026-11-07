using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace StrayApocalypse;

/// <summary>
/// Draws the follow-up explosions of Kefka's Stray Apocalypse in Dancing Mad (Ultimate).
///
/// Decoded from two ACT network logs (2026-11-06 and 2026-11-07, 5 pulls, 60 starter casts):
/// * Kefka casts <c>BB3B</c> while pairs of helper actors cast <c>BB3C</c> (3.7 s) from the arena edge,
///   a new pair every ~2.5 s, six pairs in all.
/// * Each starter faces diagonally (rotation ±0.785) and, after its own blast at its start point, fires
///   <c>BB3D</c> six more times, each 5 y further along each axis (one diagonal grid cell) in the direction it
///   faces, at 4.60, 5.11, 5.62, 6.13, 6.65 and 7.17 s after its cast starts. That held for every starter whose
///   logged position wasn't stale.
/// * Players were hit by <c>BB3D</c> up to ~6.4 y from the logged centre (positions lag a little), so the
///   default radius is 7 y for a safety margin. Adjustable in the settings.
///
/// The helper's logged position is sometimes stale at cast start (interior point, rotation 0). So the lane is only
/// accepted once the caster faces a diagonal, and re-read every frame while it casts.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    internal const ushort TerritoryDancingMadUltimate = 1363; // ACT zone 0x553
    internal const uint ActionStrayApocalypseStarter = 0xBB3C;

    internal const int Steps = 6;
    internal const float StepPerAxis = 5f;
    internal const float FirstStepDelay = 4.60f;
    internal const float StepInterval = 0.514f;
    private const float ExplodedGrace = 0.15f;

    private const string CommandName = "/stray";
    private const int CircleSegments = 48;
    private const double PreviewLoopSeconds = 23.0; // last set starts at 12.5 s, its final explosion is at ~19.7 s
    private const double PreviewSetGap = 2.5;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly IGameGui gameGui;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog log;
    private readonly Configuration config;

    private readonly WindowSystem windows = new("StrayApocalypse");
    private readonly ConfigWindow configWindow;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    // Framework thread only.
    private readonly Dictionary<ulong, Lane> tracked = new();
    private bool preview;
    private double previewStart;
    private double previewAnchorLoop = double.NaN;
    private Vector3 previewAnchor;
    private (Vector3 A, Vector3 B, Vector3 Step)[] previewOrder = [];
    private int setIndex = -1;
    private double setStart;

    // Written on the framework thread, read on the draw thread.
    private volatile Lane[] lanes = Array.Empty<Lane>();

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IObjectTable objectTable,
        IClientState clientState,
        IGameGui gameGui,
        ICommandManager commandManager,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.gameGui = gameGui;
        this.commandManager = commandManager;
        this.log = log;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        configWindow = new ConfigWindow(config, () => pluginInterface.SavePluginConfig(config), TogglePreview, () => preview);
        windows.AddWindow(configWindow);

        pluginInterface.UiBuilder.Draw += OnDraw;
        pluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;
        pluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        framework.Update += OnUpdate;
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the settings. \"/stray test\" draws two sample lanes in front of you.",
        });
    }

    private double Now => clock.Elapsed.TotalSeconds;

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("test", StringComparison.OrdinalIgnoreCase))
            TogglePreview();
        else
            configWindow.Toggle();
    }

    private void TogglePreview()
    {
        preview = !preview;
        previewStart = Now;
    }

    private void OnUpdate(IFramework _)
    {
        var now = Now;
        var found = new List<Lane>();

        if (config.Enabled && (!config.OnlyInDuty || clientState.TerritoryType == TerritoryDancingMadUltimate))
            ScanCasts(now, found);
        else
            tracked.Clear();

        if (preview)
            AddPreview(now, found);

        lanes = found.ToArray();
    }

    private void ScanCasts(double now, List<Lane> found)
    {
        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc { IsCasting: true } npc || npc.CastActionId != ActionStrayApocalypseStarter)
                continue;

            var castStart = now - npc.CurrentCastTime;
            var id = npc.GameObjectId;

            // Helpers are reused between waves, so a cast that started more than a second away is a new lane.
            if (!tracked.TryGetValue(id, out var lane) || Math.Abs(lane.CastStart - castStart) > 1.0)
                lane = new Lane(castStart, default, default, false, NextSet(castStart));

            var dirX = MathF.Sin(npc.Rotation);
            var dirZ = MathF.Cos(npc.Rotation);
            if (MathF.Abs(MathF.Abs(dirX) - MathF.Abs(dirZ)) < 0.15f)
            {
                var step = new Vector3(MathF.Sign(dirX) * StepPerAxis, 0f, MathF.Sign(dirZ) * StepPerAxis);
                if (!lane.Valid)
                    log.Info($"Stray Apocalypse lane from ({npc.Position.X:F1}, {npc.Position.Z:F1}) "
                           + $"rot {npc.Rotation:F2}, step ({step.X}, {step.Z}), cast {npc.CurrentCastTime:F2}s in");
                lane = lane with { Origin = npc.Position, Step = step, Valid = true };
            }

            tracked[id] = lane;
        }

        // Keep lanes after the starter's cast ends: the follow-ups go off up to ~3.5 s later.
        var expired = new List<ulong>();
        foreach (var (id, lane) in tracked)
        {
            if (now - lane.CastStart > LastStepTime + 1.0)
                expired.Add(id);
            else if (lane.Valid)
                found.Add(lane);
        }

        foreach (var id in expired)
        {
            if (!tracked[id].Valid)
                log.Warning("Stray Apocalypse starter never faced a diagonal; its lane was not drawn.");
            tracked.Remove(id);
        }
    }

    /// <summary>
    /// Starters come in pairs that start casting together, a new pair every ~2.5 s. Number the pairs so their
    /// colours can alternate. Lanes stay tracked ~8 s, so the count only resets between separate Stray Apocalypses.
    /// </summary>
    private int NextSet(double castStart)
    {
        if (tracked.Count == 0)
            setIndex = -1;

        if (setIndex < 0 || castStart - setStart > 1.0)
        {
            setIndex++;
            setStart = castStart;
        }

        return setIndex;
    }

    private void AddPreview(double now, List<Lane> found)
    {
        var me = objectTable.LocalPlayer;
        if (me is null)
            return;

        // Anchor the sample lanes where you stood when each loop began, so they stay put while you move.
        var loopStart = previewStart + Math.Floor((now - previewStart) / PreviewLoopSeconds) * PreviewLoopSeconds;
        if (previewAnchorLoop != loopStart)
        {
            previewAnchorLoop = loopStart;
            previewAnchor = me.Position;
            previewOrder = ShuffledPracticeOrder();
        }

        // The full mechanic, laid out like the real fight with you standing at the arena centre (100, 100).
        for (var set = 0; set < previewOrder.Length; set++)
        {
            var start = loopStart + set * PreviewSetGap;
            if (now < start)
                break;

            var (a, b, step) = previewOrder[set];
            found.Add(new Lane(start, previewAnchor + a, step, true, set));
            found.Add(new Lane(start, previewAnchor + b, step, true, set));
        }
    }

    /// <summary>
    /// In the logs every pull alternated NW pair, NE pair, six pairs in all, using each of the three NW and three NE
    /// starting pairs once in a different order each pull. Offsets are from the arena centre (100, 100).
    /// </summary>
    private (Vector3 A, Vector3 B, Vector3 Step)[] ShuffledPracticeOrder()
    {
        var southEast = new Vector3(5f, 0f, 5f);
        var southWest = new Vector3(-5f, 0f, 5f);
        (Vector3, Vector3, Vector3)[] northWest =
        [
            (new(-20f, 0f, -15f), new(-5f, 0f, -30f), southEast),   // (80,85) (95,70)
            (new(-10f, 0f, -25f), new(-25f, 0f, -10f), southEast),  // (90,75) (75,90)
            (new(-15f, 0f, -20f), new(-30f, 0f, -5f), southEast),   // (85,80) (70,95)
        ];
        (Vector3, Vector3, Vector3)[] northEast =
        [
            (new(15f, 0f, -20f), new(30f, 0f, -5f), southWest),     // (115,80) (130,95)
            (new(20f, 0f, -15f), new(5f, 0f, -30f), southWest),     // (120,85) (105,70)
            (new(25f, 0f, -10f), new(10f, 0f, -25f), southWest),    // (125,90) (110,75)
        ];
        Random.Shared.Shuffle(northWest);
        Random.Shared.Shuffle(northEast);

        var order = new (Vector3, Vector3, Vector3)[6];
        for (var i = 0; i < 3; i++)
        {
            order[2 * i] = northWest[i];
            order[2 * i + 1] = northEast[i];
        }

        return order;
    }

    internal static float StepTime(int k) => FirstStepDelay + StepInterval * (k - 1);

    private static float LastStepTime => StepTime(Steps);

    private void OnDraw()
    {
        windows.Draw();

        var current = lanes;
        if (current.Length == 0)
            return;

        var now = Now;
        var drawList = ImGui.GetBackgroundDrawList();
        var nextOutline = ImGui.ColorConvertFloat4ToU32(config.NextColor);
        var textColor = ImGui.ColorConvertFloat4ToU32(Vector4.One);

        foreach (var lane in current)
        {
            var elapsed = now - lane.CastStart;
            var exploding = elapsed >= StepTime(1);
            var odd = lane.Set % 2 == 0;
            var (setColor, opacity) = (odd, exploding) switch
            {
                (true, false) => (config.Color, config.FillOpacity),
                (true, true) => (config.ActiveColor, config.ActiveFillOpacity),
                (false, false) => (config.SecondColor, config.SecondFillOpacity),
                (false, true) => (config.SecondActiveColor, config.SecondActiveFillOpacity),
            };
            var outline = ImGui.ColorConvertFloat4ToU32(setColor);
            var fill = ImGui.ColorConvertFloat4ToU32(setColor with { W = opacity });
            var nextFill = ImGui.ColorConvertFloat4ToU32(setColor with { W = MathF.Min(1f, opacity * 2f) });
            var nextShown = false;

            for (var k = 1; k <= Steps; k++)
            {
                var fireTime = StepTime(k);
                if (elapsed > fireTime + ExplodedGrace)
                    continue;

                var isNext = !nextShown;
                nextShown = true;

                var center = lane.Origin + lane.Step * k;
                DrawCircle(drawList, center, config.Radius,
                           isNext ? nextOutline : outline,
                           config.Fill ? (isNext ? nextFill : fill) : 0u);

                if (config.ShowCountdown && gameGui.WorldToScreen(center, out var screen, out _))
                {
                    var label = Math.Max(0, fireTime - elapsed).ToString("0.0");
                    var size = ImGui.CalcTextSize(label);
                    drawList.AddText(screen - size / 2, textColor, label);
                }
            }
        }
    }

    private void DrawCircle(ImDrawListPtr drawList, Vector3 center, float radius, uint outline, uint fill)
    {
        Span<Vector2> points = stackalloc Vector2[CircleSegments];
        Span<bool> inFront = stackalloc bool[CircleSegments];
        var allInFront = true;

        for (var i = 0; i < CircleSegments; i++)
        {
            var angle = MathF.Tau * i / CircleSegments;
            var world = center + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);

            // The 3-argument overload returns whether the point is in front of the camera, and still projects
            // points that are off screen. (The 2-argument one also fails for off-screen points, which culled
            // circles near the screen edges.) Only points behind the camera project to nonsense.
            inFront[i] = gameGui.WorldToScreen(world, out points[i], out _);
            allInFront &= inFront[i];
        }

        if (allInFront)
        {
            if (fill != 0)
            {
                for (var i = 0; i < CircleSegments; i++)
                    drawList.PathLineTo(points[i]);
                drawList.PathFillConvex(fill);
            }

            for (var i = 0; i < CircleSegments; i++)
                drawList.PathLineTo(points[i]);
            drawList.PathStroke(outline, ImDrawFlags.Closed, config.Thickness);
            return;
        }

        // Partly behind the camera: skip the fill and draw only the arcs that are in front.
        for (var i = 0; i <= CircleSegments; i++)
        {
            var j = i % CircleSegments;
            if (inFront[j])
            {
                drawList.PathLineTo(points[j]);
            }
            else
            {
                drawList.PathStroke(outline, ImDrawFlags.None, config.Thickness);
            }
        }

        drawList.PathStroke(outline, ImDrawFlags.None, config.Thickness);
    }

    public void Dispose()
    {
        commandManager.RemoveHandler(CommandName);
        framework.Update -= OnUpdate;
        pluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        pluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;
        pluginInterface.UiBuilder.Draw -= OnDraw;
        windows.RemoveAllWindows();
    }
}

/// <summary>One starter's line: its start point, the per-explosion step, and when its cast began (plugin clock).</summary>
internal readonly record struct Lane(double CastStart, Vector3 Origin, Vector3 Step, bool Valid, int Set);

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool Enabled { get; set; } = true;
    public bool OnlyInDuty { get; set; } = true;
    public float Radius { get; set; } = 7f;
    public bool Fill { get; set; } = true;
    public float FillOpacity { get; set; } = 0.2f;
    public bool ShowCountdown { get; set; } = true;
    public Vector4 Color { get; set; } = new(1f, 0.55f, 0.1f, 0.9f);
    public Vector4 SecondColor { get; set; } = new(0.2f, 0.65f, 1f, 0.9f);
    public float SecondFillOpacity { get; set; } = 0.2f;

    // Looks once the set's first drawn circle has gone off.
    public Vector4 ActiveColor { get; set; } = new(1f, 0.55f, 0.1f, 1f);
    public float ActiveFillOpacity { get; set; } = 0.35f;
    public Vector4 SecondActiveColor { get; set; } = new(0.2f, 0.65f, 1f, 1f);
    public float SecondActiveFillOpacity { get; set; } = 0.35f;
    public Vector4 NextColor { get; set; } = new(1f, 0.1f, 0.1f, 1f);
    public float Thickness { get; set; } = 2.5f;
}

internal sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly Action save;
    private readonly Action togglePreview;
    private readonly Func<bool> previewOn;

    public ConfigWindow(Configuration config, Action save, Action togglePreview, Func<bool> previewOn)
        : base("Stray Apocalypse Paths##config", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.config = config;
        this.save = save;
        this.togglePreview = togglePreview;
        this.previewOn = previewOn;
    }

    public override void Draw()
    {
        var changed = false;

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Draw explosion paths", ref enabled))
        {
            config.Enabled = enabled;
            changed = true;
        }

        var onlyInDuty = config.OnlyInDuty;
        if (ImGui.Checkbox("Only in Dancing Mad (Ultimate)", ref onlyInDuty))
        {
            config.OnlyInDuty = onlyInDuty;
            changed = true;
        }

        ImGui.Separator();
        var radius = config.Radius;
        if (ImGui.SliderFloat("Explosion radius (yalms)", ref radius, 3f, 10f, "%.1f"))
        {
            config.Radius = radius;
            changed = true;
        }

        var countdown = config.ShowCountdown;
        if (ImGui.Checkbox("Show seconds until each explosion", ref countdown))
        {
            config.ShowCountdown = countdown;
            changed = true;
        }

        var fill = config.Fill;
        if (ImGui.Checkbox("Fill circles", ref fill))
        {
            config.Fill = fill;
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Each set has two looks: waiting (before its first drawn circle goes off)");
        ImGui.TextUnformatted("and exploding (after). The colour's alpha sets the outline opacity.");

        ImGui.TextUnformatted("Odd sets (1st, 3rd, 5th)");
        changed |= Look("Waiting##odd", () => config.Color, v => config.Color = v,
                        () => config.FillOpacity, v => config.FillOpacity = v);
        changed |= Look("Exploding##odd", () => config.ActiveColor, v => config.ActiveColor = v,
                        () => config.ActiveFillOpacity, v => config.ActiveFillOpacity = v);

        ImGui.TextUnformatted("Even sets (2nd, 4th, 6th)");
        changed |= Look("Waiting##even", () => config.SecondColor, v => config.SecondColor = v,
                        () => config.SecondFillOpacity, v => config.SecondFillOpacity = v);
        changed |= Look("Exploding##even", () => config.SecondActiveColor, v => config.SecondActiveColor = v,
                        () => config.SecondActiveFillOpacity, v => config.SecondActiveFillOpacity = v);

        ImGui.Separator();

        var nextColor = config.NextColor;
        if (ImGui.ColorEdit4("Next-to-explode outline", ref nextColor))
        {
            config.NextColor = nextColor;
            changed = true;
        }

        var thickness = config.Thickness;
        if (ImGui.SliderFloat("Outline thickness", ref thickness, 1f, 8f, "%.1f"))
        {
            config.Thickness = thickness;
            changed = true;
        }

        ImGui.Separator();
        if (ImGui.Button(previewOn() ? "Stop preview" : "Preview in front of me"))
            togglePreview();

        if (changed)
            save();
    }

    private static bool Look(string label, Func<Vector4> getColor, Action<Vector4> setColor,
                             Func<float> getOpacity, Action<float> setOpacity)
    {
        var changed = false;
        ImGui.PushID(label);

        var color = getColor();
        if (ImGui.ColorEdit4(label, ref color, ImGuiColorEditFlags.NoInputs))
        {
            setColor(color);
            changed = true;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        var opacity = getOpacity();
        if (ImGui.SliderFloat("fill opacity", ref opacity, 0f, 1f, "%.2f"))
        {
            setOpacity(opacity);
            changed = true;
        }

        ImGui.PopID();
        return changed;
    }
}
