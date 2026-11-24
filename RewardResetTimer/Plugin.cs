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
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Action = System.Action;

namespace RewardResetTimer;

/// <summary>
/// Draws a countdown next to every visible "Reward already claimed" text node in the Duty Finder.
///
/// Roulette bonuses reset daily at 15:00 UTC; everything else in the Duty Finder that shows the text (raid loot and
/// the like) resets weekly on Tuesday at 08:00 UTC. Which one applies is taken from the selected entry's content
/// type in AgentContentsFinder.
///
/// The text to look for comes from the game's own Addon sheet (any English row containing "already claimed",
/// read back in the client's language), so it works on non-English clients. The game's nodes are never edited:
/// the countdown is an ImGui overlay drawn at the node's on-screen position.
/// </summary>
public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string CommandName = "/rewardreset";
    private const string AddonName = "ContentsFinder";
    private const string EnglishNeedle = "already claimed";
    private const int MaxNodeDepth = 8;

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
    private readonly string[] needles;
    private readonly List<Match> matches = new();

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
        log.Information($"Matching Duty Finder text: {string.Join(" | ", needles.Select(n => $"\"{n}\""))}");

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

    private string[] BuildNeedles(IDataManager dataManager)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { EnglishNeedle };
        try
        {
            var english = dataManager.GetExcelSheet<Addon>(ClientLanguage.English);
            var local = dataManager.GetExcelSheet<Addon>();
            foreach (var row in english)
            {
                if (!row.Text.ExtractText().Contains(EnglishNeedle, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (local.GetRowOrDefault(row.RowId) is { } localRow)
                {
                    var text = localRow.Text.ExtractText().Trim().TrimEnd('.', '。', '!', '！');
                    if (text.Length > 0)
                        result.Add(text);
                }
            }
        }
        catch (Exception e)
        {
            log.Warning(e, "Couldn't read the Addon sheet; falling back to English text only.");
        }

        return result.ToArray();
    }

    private bool IsRewardClaimedText(string text)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(AddonName).Address;
        if (addon == null || !addon->IsVisible || addon->UldManager.LoadedState != AtkLoadState.Loaded)
            return;

        matches.Clear();
        CollectMatches(&addon->UldManager, 0);
        if (matches.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var weekly = SelectedIsWeekly();
        var left = (weekly ? NextWeeklyReset(now) : NextDailyReset(now)) - now;
        var label = $"{(config.ShowKind ? weekly ? "Weekly reset: " : "Daily reset: " : "")}{Format(left, config.ShowSeconds)}";

        var drawList = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * config.FontScale * addon->Scale;
        var textSize = ImGui.CalcTextSize(label) * (fontSize / ImGui.GetFontSize());
        var color = ImGui.ColorConvertFloat4ToU32(config.Color);
        var shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, config.Color.W * 0.85f));

        foreach (var m in matches)
        {
            var pos = new Vector2(m.Right + config.Gap * addon->Scale, m.CenterY - textSize.Y / 2);
            drawList.AddText(font, fontSize, pos + Vector2.One, shadow, label);
            drawList.AddText(font, fontSize, pos, color, label);
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
                if (IsShownOnScreen(node) && IsRewardClaimedText(text->NodeText.ToString()))
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

        return new Match(left + width, node->ScreenY + node->Height * scale / 2);
    }

    private readonly record struct Match(float Right, float CenterY);
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool Enabled { get; set; } = true;
    public bool ShowSeconds { get; set; } = false;
    public bool ShowKind { get; set; } = true;
    public float FontScale { get; set; } = 1f;
    public float Gap { get; set; } = 8f;
    public Vector4 Color { get; set; } = new(1f, 0.85f, 0.45f, 1f);
}

internal sealed class ConfigWindow : Window
{
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

        var color = config.Color;
        if (ImGui.ColorEdit4("Colour", ref color))
        {
            config.Color = color;
            changed = true;
        }

        if (changed)
            save();
    }
}
