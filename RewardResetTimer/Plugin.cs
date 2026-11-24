using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Action = System.Action;

namespace RewardResetTimer;

/// <summary>
/// Draws a countdown next to the "Reward already received" text that the Duty Finder shows for a duty whose
/// bonus has been claimed.
///
/// Roulette bonuses reset daily at 15:00 UTC; everything else that shows the text (raid loot and the like) resets
/// weekly on Tuesday at 08:00 UTC. Which one applies is taken from the selected entry's content type in
/// AgentContentsFinder.
///
/// The text lives in the duty detail pane beside the Duty Finder, which is its own addon. Rather than hard-code
/// that addon's name, while the Duty Finder is open every visible addon is searched about twice a second, and the
/// ones holding the text are then re-measured every frame. Text matches exactly (ignoring case and trailing
/// punctuation) against the game's own Addon sheet rows, read in the client's language, so unrelated text that
/// merely contains the phrase can't trigger it.
///
/// The game's nodes are never edited: the countdown is an ImGui overlay drawn at the node's on-screen position.
/// </summary>
public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string CommandName = "/rewardreset";
    private const string DutyFinderAddon = "ContentsFinder";
    private const int MaxNodeDepth = 8;

    private static readonly string[] EnglishTexts = { "Reward already received", "Reward already claimed" };
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMilliseconds(500);

    internal static readonly TimeSpan DailyResetUtc = TimeSpan.FromHours(15);
    internal static readonly TimeSpan WeeklyResetUtc = TimeSpan.FromHours(8);
    internal const DayOfWeek WeeklyResetDay = DayOfWeek.Tuesday;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IGameGui gameGui;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly Configuration config;

    private readonly WindowSystem windows = new("RewardResetTimer");
    private readonly ConfigWindow configWindow;
    private readonly HashSet<string> needles;
    private readonly List<Match> matches = new();
    private readonly HashSet<string> hostAddons = new();
    private readonly HashSet<string> loggedHosts = new();

    private DateTime nextDiscovery;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IGameGui gameGui,
        ICommandManager commandManager,
        IChatGui chatGui,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.gameGui = gameGui;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.log = log;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        needles = BuildNeedles(dataManager);
        log.Information($"Matching reward text: {string.Join(" | ", needles.Select(n => $"\"{n}\""))}");

        configWindow = new ConfigWindow(config, () => pluginInterface.SavePluginConfig(config));
        windows.AddWindow(configWindow);

        pluginInterface.UiBuilder.Draw += OnDraw;
        pluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        pluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Reward Reset Timer settings. \"/rewardreset time\" prints both countdowns to chat.",
        });
    }

    public void Dispose()
    {
        commandManager.RemoveHandler(CommandName);
        pluginInterface.UiBuilder.Draw -= OnDraw;
        pluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        pluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;
        windows.RemoveAllWindows();
    }

    private void OnCommand(string command, string arguments)
    {
        if (arguments.Trim().Equals("time", StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTime.UtcNow;
            chatGui.Print($"Daily reset in {Format(NextDailyReset(now) - now, true)}, " +
                          $"weekly reset in {Format(NextWeeklyReset(now) - now, true)}.");
            return;
        }

        configWindow.Toggle();
    }

    // ---- reset maths ----------------------------------------------------------------------------------------

    internal static DateTime NextDailyReset(DateTime utcNow)
    {
        var reset = utcNow.Date + DailyResetUtc;
        return reset > utcNow ? reset : reset.AddDays(1);
    }

    internal static DateTime NextWeeklyReset(DateTime utcNow)
    {
        var daysAhead = ((int)WeeklyResetDay - (int)utcNow.DayOfWeek + 7) % 7;
        var reset = utcNow.Date.AddDays(daysAhead) + WeeklyResetUtc;
        return reset > utcNow ? reset : reset.AddDays(7);
    }

    internal static string Format(TimeSpan left, bool seconds)
    {
        if (left < TimeSpan.Zero)
            left = TimeSpan.Zero;

        if (left.TotalDays >= 1)
            return seconds ? $"{left.Days}d {left.Hours}h {left.Minutes:D2}m {left.Seconds:D2}s" : $"{left.Days}d {left.Hours}h {left.Minutes:D2}m";

        if (left.TotalHours >= 1)
            return seconds ? $"{left.Hours}h {left.Minutes:D2}m {left.Seconds:D2}s" : $"{left.Hours}h {left.Minutes:D2}m";

        return $"{left.Minutes}m {left.Seconds:D2}s";
    }

    // ---- text lookup ----------------------------------------------------------------------------------------

    private static string Normalise(string text) => text.Trim().TrimEnd('.', '。', '!', '！', ' ');

    private HashSet<string> BuildNeedles(IDataManager dataManager)
    {
        var result = new HashSet<string>(EnglishTexts, StringComparer.OrdinalIgnoreCase);
        try
        {
            var english = dataManager.GetExcelSheet<Addon>(ClientLanguage.English);
            var local = dataManager.GetExcelSheet<Addon>();
            foreach (var row in english)
            {
                var text = Normalise(row.Text.ExtractText());
                if (!EnglishTexts.Any(t => t.Equals(text, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (local.GetRowOrDefault(row.RowId) is { } localRow && Normalise(localRow.Text.ExtractText()) is { Length: > 0 } localText)
                    result.Add(localText);
            }
        }
        catch (Exception e)
        {
            log.Warning(e, "Couldn't read the Addon sheet; falling back to English text only.");
        }

        return result;
    }

    // ---- drawing --------------------------------------------------------------------------------------------

    private void OnDraw()
    {
        windows.Draw();

        if (!config.Enabled)
            return;

        try
        {
            DrawOverlay();
        }
        catch (Exception e)
        {
            log.Error(e, "Overlay draw failed.");
            config.Enabled = false;
            chatGui.PrintError("[Reward Reset Timer] Overlay hit an error and was turned off. See /xllog.");
        }
    }

    private void DrawOverlay()
    {
        var finder = (AtkUnitBase*)gameGui.GetAddonByName(DutyFinderAddon).Address;
        if (finder == null || !finder->IsVisible)
        {
            hostAddons.Clear();
            return;
        }

        matches.Clear();
        var now = DateTime.UtcNow;
        if (now >= nextDiscovery)
        {
            nextDiscovery = now + DiscoveryInterval;
            Discover();
        }
        else
        {
            foreach (var name in hostAddons)
            {
                var addon = (AtkUnitBase*)gameGui.GetAddonByName(name).Address;
                if (addon != null && addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded)
                    CollectMatches(&addon->UldManager, 0);
            }
        }

        if (matches.Count == 0)
            return;

        var weekly = SelectedIsWeekly();
        var left = (weekly ? NextWeeklyReset(now) : NextDailyReset(now)) - now;
        var label = $"{(config.ShowKind ? weekly ? "Weekly reset: " : "Daily reset: " : "")}{Format(left, config.ShowSeconds)}";

        var drawList = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var colour = config.TextColour;
        var shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, ImGui.ColorConvertU32ToFloat4(colour).W * 0.85f));

        foreach (var m in matches)
        {
            var fontSize = ImGui.GetFontSize() * config.FontScale * m.Scale;
            var size = ImGui.CalcTextSize(label) * (fontSize / ImGui.GetFontSize());
            var gap = config.Gap * m.Scale;
            var pos = config.Placement switch
            {
                Placement.Left => new Vector2(m.Left - gap - size.X, m.CenterY - size.Y / 2),
                Placement.Right => new Vector2(m.Right + gap, m.CenterY - size.Y / 2),
                Placement.Above => new Vector2(m.Right - size.X, m.Top - gap - size.Y),
                _ => new Vector2(m.Right - size.X, m.Bottom + gap),
            };

            drawList.AddText(font, fontSize, pos + Vector2.One, shadow, label);
            drawList.AddText(font, fontSize, pos, colour, label);
        }
    }

    /// <summary>Searches every visible addon and remembers which ones hold the reward text.</summary>
    private void Discover()
    {
        hostAddons.Clear();
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
            return;

        var list = &manager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < list->Count; i++)
        {
            var addon = list->Entries[i].Value;
            if (addon == null || !addon->IsVisible || addon->UldManager.LoadedState != AtkLoadState.Loaded)
                continue;

            var before = matches.Count;
            CollectMatches(&addon->UldManager, 0);
            if (matches.Count == before)
                continue;

            var name = addon->NameString;
            hostAddons.Add(name);
            if (loggedHosts.Add(name))
                log.Information($"Found reward text in addon \"{name}\".");
        }
    }

    /// <summary>
    /// True when the Duty Finder's selected entry is a regular duty (weekly-restricted loot), false for a roulette
    /// (daily bonus). Defaults to daily if the agent can't be read.
    /// </summary>
    private static bool SelectedIsWeekly()
    {
        var agent = AgentContentsFinder.Instance();
        if (agent == null)
            return false;

        return agent->SelectedDuty.ContentType == ContentsType.Regular;
    }

    private void CollectMatches(AtkUldManager* uld, int depth)
    {
        if (depth > MaxNodeDepth || uld == null || uld->NodeList == null)
            return;

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || !node->IsVisible())
                continue;

            if (node->Type == NodeType.Text)
            {
                var text = (AtkTextNode*)node;
                if (needles.Contains(Normalise(text->NodeText.ToString())) && IsShownOnScreen(node))
                    matches.Add(Measure(text));
            }
            else if ((int)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null)
                    CollectMatches(&component->UldManager, depth + 1);
            }
        }
    }

    private static bool IsShownOnScreen(AtkResNode* node)
    {
        for (var n = node; n != null; n = n->ParentNode)
        {
            if (!n->IsVisible() || n->Alpha_2 == 0)
                return false;
        }

        return true;
    }

    private static Match Measure(AtkTextNode* text)
    {
        var node = &text->AtkResNode;
        var scale = 1f;
        for (var n = node; n != null; n = n->ParentNode)
            scale *= n->ScaleX;

        ushort drawWidth = 0, drawHeight = 0;
        text->GetTextDrawSize(&drawWidth, &drawHeight);

        var nodeWidth = node->Width * scale;
        var width = drawWidth > 0 ? Math.Min(drawWidth * scale, nodeWidth) : nodeWidth;
        var left = node->ScreenX;

        // Right- or centre-aligned text doesn't start at the node's left edge.
        var align = (int)text->AlignmentType & 0x3;
        if (align == 2)
            left += nodeWidth - width;
        else if (align == 1)
            left += (nodeWidth - width) / 2;

        var height = node->Height * scale;
        return new Match(left, left + width, node->ScreenY, node->ScreenY + height, scale);
    }

    private readonly record struct Match(float Left, float Right, float Top, float Bottom, float Scale)
    {
        public float CenterY => (Top + Bottom) / 2;
    }
}

public enum Placement
{
    Left,
    Right,
    Above,
    Below,
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;
    public bool ShowSeconds { get; set; } = false;
    public bool ShowKind { get; set; } = true;
    public Placement Placement { get; set; } = Placement.Left;
    public float FontScale { get; set; } = 1f;
    public float Gap { get; set; } = 8f;

    /// <summary>ImGui packed colour (ABGR). A plain number, because Vector4 didn't round-trip through the config.</summary>
    public uint TextColour { get; set; } = 0xFF73D9FF;
}

internal sealed class ConfigWindow : Window
{
    private static readonly string[] PlacementNames = Enum.GetNames<Placement>();

    private readonly Configuration config;
    private readonly Action save;

    public ConfigWindow(Configuration config, Action save)
        : base("Reward Reset Timer###RewardResetTimerConfig", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.config = config;
        this.save = save;
    }

    public override void Draw()
    {
        var now = DateTime.UtcNow;
        ImGui.TextUnformatted($"Daily reset (15:00 UTC):  {Plugin.Format(Plugin.NextDailyReset(now) - now, true)}");
        ImGui.TextUnformatted($"Weekly reset (Tue 08:00 UTC):  {Plugin.Format(Plugin.NextWeeklyReset(now) - now, true)}");
        ImGui.Separator();

        var changed = false;

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Show countdown in the Duty Finder", ref enabled))
        {
            config.Enabled = enabled;
            changed = true;
        }

        var kind = config.ShowKind;
        if (ImGui.Checkbox("Prefix with \"Daily reset:\" / \"Weekly reset:\"", ref kind))
        {
            config.ShowKind = kind;
            changed = true;
        }

        var seconds = config.ShowSeconds;
        if (ImGui.Checkbox("Show seconds", ref seconds))
        {
            config.ShowSeconds = seconds;
            changed = true;
        }

        var placement = (int)config.Placement;
        if (ImGui.Combo("Position", ref placement, PlacementNames, PlacementNames.Length))
        {
            config.Placement = (Placement)placement;
            changed = true;
        }

        var scale = config.FontScale;
        if (ImGui.SliderFloat("Text size", ref scale, 0.5f, 2f, "%.2f"))
        {
            config.FontScale = scale;
            changed = true;
        }

        var gap = config.Gap;
        if (ImGui.SliderFloat("Gap from text", ref gap, 0f, 40f, "%.0f px"))
        {
            config.Gap = gap;
            changed = true;
        }

        var colour = ImGui.ColorConvertU32ToFloat4(config.TextColour);
        if (ImGui.ColorEdit4("Colour", ref colour))
        {
            config.TextColour = ImGui.ColorConvertFloat4ToU32(colour);
            changed = true;
        }

        if (changed)
            save();
    }
}
