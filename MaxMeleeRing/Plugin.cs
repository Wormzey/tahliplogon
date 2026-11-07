using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace MaxMeleeRing;

/// <summary>
/// Draws a ring on the ground around enemies at maximum melee range.
///
/// The game measures action range between hitbox edges, so an action reaches when
///   centre distance &lt;= enemy hitbox + your hitbox + action range.
/// The ring sits at that distance, which means it's your character's centre (the dot in your own
/// hitbox), not your edge, that has to stay inside it.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/maxmelee";
    private const float SegmentsPerYalm = 8f;
    private const int MinSegments = 64;
    private const int MaxSegments = 360;
    private const float GroundRayStartHeight = 1f;
    private const float GroundRayLength = 50f;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;
    private readonly ICommandManager commandManager;
    private readonly Configuration config;

    private readonly WindowSystem windows = new("MaxMeleeRing");
    private readonly ConfigWindow configWindow;

    // written on the framework thread, read by the draw thread
    private volatile Ring[] rings = Array.Empty<Ring>();

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IGameGui gameGui,
        ICondition condition,
        ICommandManager commandManager)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.gameGui = gameGui;
        this.condition = condition;
        this.commandManager = commandManager;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        configWindow = new ConfigWindow(config, () => pluginInterface.SavePluginConfig(config));
        windows.AddWindow(configWindow);

        pluginInterface.UiBuilder.Draw += OnDraw;
        pluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;
        pluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        framework.Update += OnUpdate;
        commandManager.AddHandler(CommandName, new CommandInfo((_, _) => configWindow.Toggle())
        {
            HelpMessage = "Open the Max Melee Ring settings.",
        });
    }

    private void OnUpdate(IFramework _)
    {
        var me = objectTable.LocalPlayer;
        if (!config.Enabled || me is null || (config.OnlyInCombat && !condition[ConditionFlag.InCombat]))
        {
            rings = Array.Empty<Ring>();
            return;
        }

        var reach = config.MeleeRange + (config.IncludeOwnHitbox ? me.HitboxRadius : 0f);
        var targetId = targetManager.Target?.GameObjectId;
        var found = new List<Ring>();

        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc { BattleNpcKind: BattleNpcSubKind.Combatant, IsTargetable: true, IsDead: false } npc)
                continue;

            var isTarget = npc.GameObjectId == targetId;
            var show = config.Show switch
            {
                RingMode.TargetOnly => isTarget,
                RingMode.InCombat => isTarget || npc.StatusFlags.HasFlag(StatusFlags.InCombat),
                _ => true,
            };

            if (show)
                found.Add(new Ring(GroundBelow(npc.Position), npc.HitboxRadius + reach));
        }

        rings = found.ToArray();
    }

    /// <summary>
    /// An enemy's position isn't always on the floor (striking dummies sit well above it), so ask the
    /// collision scene where the ground is. Starts a little high in case the position is just under the floor.
    /// </summary>
    private static Vector3 GroundBelow(Vector3 position)
    {
        var origin = position + new Vector3(0f, GroundRayStartHeight, 0f);
        return BGCollisionModule.RaycastMaterialFilter(origin, -Vector3.UnitY, out var hit, GroundRayLength)
            ? hit.Point
            : position;
    }

    private void OnDraw()
    {
        windows.Draw();

        var current = rings;
        if (current.Length == 0)
            return;

        var drawList = ImGui.GetBackgroundDrawList();
        var color = ImGui.ColorConvertFloat4ToU32(config.Color);

        foreach (var ring in current)
        {
            var segments = Math.Clamp((int)(ring.Radius * SegmentsPerYalm), MinSegments, MaxSegments);
            for (var i = 0; i <= segments; i++)
            {
                var angle = MathF.Tau * i / segments;
                var point = ring.Center + new Vector3(MathF.Cos(angle) * ring.Radius, 0f, MathF.Sin(angle) * ring.Radius);

                // Points behind the camera project to nonsense, so break the line there. Points that are just
                // off screen are fine, and need keeping or the ring stops short of the screen edge.
                if (gameGui.WorldToScreen(point, out var screen, out _))
                    drawList.PathLineTo(screen);
                else
                    drawList.PathStroke(color, ImDrawFlags.None, config.Thickness);
            }

            drawList.PathStroke(color, ImDrawFlags.None, config.Thickness);
        }
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

internal readonly record struct Ring(Vector3 Center, float Radius);

public enum RingMode
{
    TargetOnly,
    InCombat,
    AllEnemies,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool Enabled { get; set; } = true;
    public bool OnlyInCombat { get; set; } = true;
    public RingMode Show { get; set; } = RingMode.InCombat;
    public float MeleeRange { get; set; } = 3f;
    public bool IncludeOwnHitbox { get; set; } = true;
    public Vector4 Color { get; set; } = new(1f, 0.15f, 0.15f, 0.9f);
    public float Thickness { get; set; } = 3f;
}

internal sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly Action save;

    public ConfigWindow(Configuration config, Action save)
        : base("Max Melee Ring##config", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.config = config;
        this.save = save;
    }

    public override void Draw()
    {
        var changed = false;

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Draw rings", ref enabled))
        {
            config.Enabled = enabled;
            changed = true;
        }

        var onlyInCombat = config.OnlyInCombat;
        if (ImGui.Checkbox("Only while you're in combat", ref onlyInCombat))
        {
            config.OnlyInCombat = onlyInCombat;
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Show rings on");
        changed |= ModeOption("Current target only", RingMode.TargetOnly);
        changed |= ModeOption("Current target and enemies in combat", RingMode.InCombat);
        changed |= ModeOption("All enemies nearby", RingMode.AllEnemies);

        ImGui.Separator();
        var range = config.MeleeRange;
        if (ImGui.SliderFloat("Melee range (yalms)", ref range, 0f, 5f, "%.1f"))
        {
            config.MeleeRange = range;
            changed = true;
        }

        var ownHitbox = config.IncludeOwnHitbox;
        if (ImGui.Checkbox("Add your own hitbox to the range", ref ownHitbox))
        {
            config.IncludeOwnHitbox = ownHitbox;
            changed = true;
        }

        ImGui.Separator();
        var color = config.Color;
        if (ImGui.ColorEdit4("Colour", ref color))
        {
            config.Color = color;
            changed = true;
        }

        var thickness = config.Thickness;
        if (ImGui.SliderFloat("Thickness", ref thickness, 1f, 10f, "%.1f"))
        {
            config.Thickness = thickness;
            changed = true;
        }

        if (changed)
            save();
    }

    private bool ModeOption(string label, RingMode mode)
    {
        if (!ImGui.RadioButton(label, config.Show == mode))
            return false;

        config.Show = mode;
        return true;
    }
}
