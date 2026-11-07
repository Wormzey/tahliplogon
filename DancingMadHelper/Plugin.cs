using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
// Dalamud API 13+ ships its own ImGui bindings. On older API levels use "using ImGuiNET;" instead.
using Dalamud.Bindings.ImGui;

namespace DancingMadHelper;

/// <summary>
/// Dancing Mad (Ultimate) phase 4 helper.
///
/// Neo Exdeath gets a hidden status (0x808) just before every Grand Cross cast. Its param says whether
/// the cast is real or fake. Decoded from a network log (7 pulls, 14 Grand Cross sets):
///   param 0x462 = REAL  -> Forked Lightning spreads, Compressed Water stacks, Acceleration Bomb = stand still
///   param 0x461 = FAKE  -> Forked Lightning stacks,  Compressed Water spreads, Acceleration Bomb = move
/// The stack/spread half is verified against the actual damage events. The bomb half is inferred from
/// player movement/ability use around expiry and the 5 failures in the log (see README), so treat it as
/// likely-but-unconfirmed until you've watched it in game.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    // ---- IDs taken from the log -------------------------------------------------------------
    internal const ushort TerritoryDancingMadUltimate = 0x553;   // ACT zone 553 (hex) = 1363
    internal const uint NeoExdeathNameId = 6055;                 // AddCombatant name ID for "Neo Exdeath"
    internal const uint StatusNeoFlag = 0x808;                   // hidden status on Neo Exdeath
    internal const ushort FlagFake = 0x461;
    internal const ushort FlagReal = 0x462;
    internal const uint ChaosNameId = 7691;
    internal const ushort ChaosFlagFake = 0x45F;
    internal const ushort ChaosFlagReal = 0x460;
    internal const uint ActionInfernoA = 0xBB1E;
    internal const uint ActionInfernoB = 0xBB20;
    internal const uint ActionTsunamiA = 0xBB1F;
    internal const uint ActionTsunamiB = 0xBB21;
    internal const uint StatusForkedLightning = 0x15A8;
    internal const uint StatusCompressedWater = 0x15A9;
    internal const uint StatusAccelerationBomb = 0x15AA;
    internal const uint StatusBlizzardCharged = 0x5CC;           // on Kefka from the charged cast until ~0.7s after Mana Release
    internal const uint StatusThunderCharged = 0x5CD;
    internal const uint ActionKefkaSays = 0xC2DC;                // phase start
    internal const uint ActionUltimaUpsurge = 0xC24A;            // the second one after Kefka Says ends the phase
    internal const uint ActionManaRelease = 0xBAA5;
    internal const uint ActionThunderTrue = 0xBA9F;              // helper actors cast these; true casts deal damage
    internal const uint ActionThunderFalseA = 0xBAA0;
    internal const uint ActionThunderFalseB = 0xBAA1;
    internal const uint ActionBlizzardTrue = 0xBA98;
    internal const uint ActionBlizzardFalseA = 0xBA9B;
    internal const uint ActionBlizzardFalseB = 0xBA9E;
    internal const uint ActorControlHeadMarker = 34;
    internal const uint MarkerBlizzardInverted = 0x2A3;          // question mark around the spell at Mana Release
    internal const uint MarkerBlizzard = 0x2A4;
    internal const uint MarkerThunderInverted = 0x2A5;
    internal const uint MarkerThunder = 0x2A6;
    // -----------------------------------------------------------------------------------------

    private const string CommandName = "/dmuhelper";
    private const long LateFlagWindowMs = 2000;                  // flag lasts ~1.4s after the debuffs land
    private const long ReleaseLingerMs = 6000;                   // released spells land ~4.4s after the charges drop
    private const long ChaosCallDelayMs = 3000;                  // after the Inferno/Tsunami cast starts

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly Configuration config;
    private readonly IDutyState dutyState;
    private readonly IDutyState.DutyWipedDelegate onDutyWiped;
    private readonly IPluginLog log;

    private readonly WindowSystem windows = new("DancingMadHelper");
    private readonly OverlayWindow overlay;

    // status id -> Neo flag that was active when *we* gained that status
    private readonly Dictionary<uint, ushort> flagAtGain = new();
    private readonly Dictionary<uint, long> gainedAtMs = new();
    private readonly HashSet<uint> previous = new();
    private ushort lastLoggedFlag;

    // true from Kefka Says until a wipe or the second Ultima Upsurge
    private bool phaseActive;
    private bool upsurgeCasting;
    private int upsurgeCount;
    private volatile bool wipePending;

    // the Inferno/Tsunami cast waiting for its delayed call
    private bool chaosCasting;
    private string? pendingChaosSpell;
    private long pendingChaosAtMs;

    // Kefka's charged spells, latched while their charge status is up. null = not read yet.
    private bool? thunderTrue;
    private bool? blizzardTrue;
    private bool? thunderInverted;                               // from the Mana Release markers
    private bool? blizzardInverted;
    private bool? thunderFinal;                                  // from the released casts themselves
    private bool? blizzardFinal;
    private bool releaseSeen;
    private long manaClearAtMs;

    private delegate void ActorControlDelegate(
        uint entityId, uint category, uint p1, uint p2, uint p3, uint p4, uint p5, uint p6, uint p7, uint p8,
        GameObjectId targetId, bool replaying);

    private readonly Hook<ActorControlDelegate>? actorControlHook;
    private readonly ConcurrentQueue<uint> markers = new();

    // written on the framework thread, read by the draw thread
    private volatile Callout[] current = Array.Empty<Callout>();

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        ICommandManager commandManager,
        IGameInteropProvider interop,
        IDutyState dutyState,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.dutyState = dutyState;
        this.chatGui = chatGui;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.commandManager = commandManager;
        this.log = log;

        overlay = new OverlayWindow(() => current);
        windows.AddWindow(overlay);

        // Everything else still works without the hook; Mana Release just reads the released casts instead.
        try
        {
            actorControlHook = interop.HookFromAddress<ActorControlDelegate>(
                PacketDispatcher.Addresses.HandleActorControlPacket.Value, OnActorControl);
            actorControlHook.Enable();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Couldn't hook actor control; Mana Release markers won't be read");
        }

        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += TogglePreview;
        pluginInterface.UiBuilder.OpenConfigUi += TogglePreview;
        onDutyWiped = _ => wipePending = true;
        dutyState.DutyWiped += onDutyWiped;
        framework.Update += OnUpdate;
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Show or hide the Dancing Mad helper window so you can position it. "
                + "\"/dmuhelper chat\" switches the Fire/Water calls between party chat and your own chat (/e).",
        });
    }

    private void OnCommand(string command, string args)
    {
        if (!args.Trim().Equals("chat", StringComparison.OrdinalIgnoreCase))
        {
            TogglePreview();
            return;
        }

        config.SendChaosCalls = !config.SendChaosCalls;
        pluginInterface.SavePluginConfig(config);
        chatGui.Print($"Dancing Mad Helper: Fire/Water calls now go to {(config.SendChaosCalls ? "party chat (/p)" : "your own chat only (/e)")}.");
    }

    private void TogglePreview()
    {
        overlay.Preview = !overlay.Preview;
        overlay.IsOpen = overlay.Preview || phaseActive;
    }

    private void OnUpdate(IFramework _)
    {
        if (clientState.TerritoryType != TerritoryDancingMadUltimate)
        {
            Reset();
            return;
        }

        // API 13+: objectTable.LocalPlayer. Older API levels: clientState.LocalPlayer.
        var me = objectTable.LocalPlayer;
        if (me is null)
        {
            Reset();
            return;
        }

        UpdatePhase();
        if (!phaseActive)
        {
            Reset();
            return;
        }

        UpdateChaosCall();

        var neoFlag = ReadBossFlag(NeoExdeathNameId, FlagFake, FlagReal);
        if (neoFlag != lastLoggedFlag)
        {
            if (neoFlag != 0)
                log.Information("Neo Exdeath flag 0x{Flag:X} ({Kind})", neoFlag, neoFlag == FlagReal ? "real" : "fake");
            lastLoggedFlag = neoFlag;
        }

        var present = new HashSet<uint>();
        var callouts = new List<Callout>(3);

        foreach (var status in me.StatusList)
        {
            var id = status.StatusId;
            if (id != StatusForkedLightning && id != StatusCompressedWater && id != StatusAccelerationBomb)
                continue;

            present.Add(id);

            // Latch the flag at the moment we gain the debuff. If we gained it a frame before the flag
            // was readable, fill it in while the flag is still around (it lasts ~1.4s after the debuffs land).
            // Past that window any flag belongs to a later cast, so leave it as "?" rather than guess.
            var now = Environment.TickCount64;
            if (!previous.Contains(id))
            {
                flagAtGain[id] = neoFlag;
                gainedAtMs[id] = now;
                log.Information("Gained status 0x{Id:X} with Neo flag 0x{Flag:X}", id, neoFlag);
            }
            else if (flagAtGain.TryGetValue(id, out var stored) && stored == 0 && neoFlag != 0
                     && now - gainedAtMs[id] <= LateFlagWindowMs)
            {
                flagAtGain[id] = neoFlag;
                log.Information("Late flag 0x{Flag:X} for status 0x{Id:X}", neoFlag, id);
            }

            // the status can outlive its timer briefly while the server resolves it
            if (status.RemainingTime <= 0)
                continue;

            var flag = flagAtGain.TryGetValue(id, out var f) ? f : (ushort)0;
            callouts.Add(Resolve(id, flag, status.RemainingTime));
        }

        // forget statuses that expired
        foreach (var id in new List<uint>(flagAtGain.Keys))
            if (!present.Contains(id))
            {
                flagAtGain.Remove(id);
                gainedAtMs.Remove(id);
            }

        previous.Clear();
        foreach (var id in present)
            previous.Add(id);

        // soonest first; the name tie-break stops equal timers swapping lines between frames
        callouts.Sort((a, b) =>
        {
            var byTime = a.Remaining.CompareTo(b.Remaining);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Debuff, b.Debuff);
        });
        UpdateManaRelease(callouts);
        current = callouts.ToArray();
        overlay.IsOpen = true;
    }

    private void UpdatePhase()
    {
        if (wipePending)
        {
            wipePending = false;
            if (phaseActive)
                log.Information("Wipe; phase over");
            phaseActive = false;
            return;
        }

        bool kefkaSays = false, upsurge = false;
        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc { IsCasting: true } npc)
                continue;

            if (npc.CastActionId == ActionKefkaSays) kefkaSays = true;
            else if (npc.CastActionId == ActionUltimaUpsurge) upsurge = true;
        }

        if (!phaseActive)
        {
            if (kefkaSays)
            {
                phaseActive = true;
                log.Information("Kefka Says; phase started");
            }

            return;
        }

        if (upsurge && !upsurgeCasting && ++upsurgeCount >= 2)
        {
            log.Information("Second Ultima Upsurge; phase over");
            phaseActive = false;
        }

        upsurgeCasting = upsurge;
    }

    /// <summary>Returns the fake or real param if that boss currently carries the hidden flag, else 0.</summary>
    private ushort ReadBossFlag(uint nameId, ushort fake, ushort real)
    {
        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc npc || npc.NameId != nameId)
                continue;

            foreach (var st in npc.StatusList)
            {
                if (st.StatusId != StatusNeoFlag)
                    continue;
                var param = st.Param;
                if (param == real || param == fake)
                    return param;
            }
        }

        return 0;
    }

    /// <summary>
    /// Chaos gets the same hidden status before Inferno and Tsunami, with 0x460 = true and 0x45F = fake.
    /// True Inferno and fake Tsunami spawn a twister; fake Inferno and true Tsunami spawn a donut.
    /// </summary>
    private void UpdateChaosCall()
    {
        string? spell = null;
        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc { IsCasting: true } npc)
                continue;

            if (npc.CastActionId is ActionInfernoA or ActionInfernoB) spell = "Fire";
            else if (npc.CastActionId is ActionTsunamiA or ActionTsunamiB) spell = "Water";
        }

        var now = Environment.TickCount64;
        if (spell is not null && !chaosCasting)
        {
            pendingChaosSpell = spell;
            pendingChaosAtMs = now + ChaosCallDelayMs;
        }

        chaosCasting = spell is not null;

        if (pendingChaosSpell is null || now < pendingChaosAtMs)
            return;

        var flag = ReadBossFlag(ChaosNameId, ChaosFlagFake, ChaosFlagReal);
        var call = ChaosCall(pendingChaosSpell, flag);
        pendingChaosSpell = null;

        if (call is null)
        {
            log.Warning("Chaos flag not readable; no call made");
            return;
        }

        // /e only echoes to our own chat, so the call can be checked live before going to the party
        var line = $"{(config.SendChaosCalls ? "/p" : "/e")} {call}";
        log.Information("Chaos flag 0x{Flag:X}: {Line}", flag, line);
        SendChat(line);
    }

    private static string? ChaosCall(string spell, ushort flag)
    {
        if (flag != ChaosFlagReal && flag != ChaosFlagFake)
            return null; // never saw the flag; don't guess

        var isTwister = (flag == ChaosFlagReal) == (spell == "Fire");
        return $"{spell} is {(isTwister ? "TWISTER (dodge)" : "DONUT (stay)")}";
    }

    private static unsafe void SendChat(string text)
    {
        var message = Utf8String.FromString(text);
        UIModule.Instance()->ProcessChatBoxEntry(message, IntPtr.Zero, false);
        message->Dtor(true);
    }

    /// <summary>
    /// Mana Charge: Kefka charges Thunder (~73s after Kefka Says) and Blizzard (~91s). Only those casts count;
    /// the charge statuses aren't up for earlier ones. Mana Release then shows a marker for each spell, and a
    /// question-mark marker inverts that charged spell. Released spells that end up false are the ones to stand on.
    /// </summary>
    private void UpdateManaRelease(List<Callout> callouts)
    {
        bool thunderCharged = false, blizzardCharged = false, releasing = false;
        bool? thunderCast = null, blizzardCast = null;

        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc npc)
                continue;

            foreach (var st in npc.StatusList)
            {
                if (st.StatusId == StatusThunderCharged) thunderCharged = true;
                else if (st.StatusId == StatusBlizzardCharged) blizzardCharged = true;
            }

            if (!npc.IsCasting)
                continue;

            switch (npc.CastActionId)
            {
                case ActionManaRelease: releasing = true; break;
                case ActionThunderTrue: thunderCast = true; break;
                case ActionThunderFalseA or ActionThunderFalseB: thunderCast = false; break;
                case ActionBlizzardTrue: blizzardCast = true; break;
                case ActionBlizzardFalseA or ActionBlizzardFalseB: blizzardCast = false; break;
            }
        }

        // The charged spells' own markers arrive before they latch, so anything after both are in is Mana Release's.
        while (markers.TryDequeue(out var marker))
        {
            if (thunderTrue is null || blizzardTrue is null)
                continue;

            switch (marker)
            {
                case MarkerThunder: thunderInverted = false; break;
                case MarkerThunderInverted: thunderInverted = true; break;
                case MarkerBlizzard: blizzardInverted = false; break;
                case MarkerBlizzardInverted: blizzardInverted = true; break;
            }

            log.Information("Mana Release marker 0x{Marker:X}", marker);
        }

        if (releasing && !releaseSeen)
        {
            releaseSeen = true;
            log.Information("Mana Release: {Callout}", StandOn(Released(thunderTrue, thunderInverted), Released(blizzardTrue, blizzardInverted)));
        }

        // The released spells start while the charges are still up, so stop latching at Mana Release.
        if (releaseSeen)
        {
            if (!releasing)
            {
                LatchReleased("Thunder", ref thunderFinal, thunderCast, Released(thunderTrue, thunderInverted));
                LatchReleased("Blizzard", ref blizzardFinal, blizzardCast, Released(blizzardTrue, blizzardInverted));
            }
        }
        else
        {
            if (thunderCharged && thunderTrue is null && thunderCast is not null)
            {
                thunderTrue = thunderCast;
                log.Information("Charged Thunder is {Value}", thunderCast.Value ? "true" : "false");
            }

            if (blizzardCharged && blizzardTrue is null && blizzardCast is not null)
            {
                blizzardTrue = blizzardCast;
                log.Information("Charged Blizzard is {Value}", blizzardCast.Value ? "true" : "false");
            }
        }

        var now = Environment.TickCount64;
        if (thunderCharged || blizzardCharged)
        {
            manaClearAtMs = now + ReleaseLingerMs;
        }
        else if (now > manaClearAtMs)
        {
            ResetMana();
            return;
        }

        var thunderNow = thunderFinal ?? Released(thunderTrue, thunderInverted);
        var blizzardNow = blizzardFinal ?? Released(blizzardTrue, blizzardInverted);

        if (thunderTrue is not null || thunderCharged)
            callouts.Add(TrueFalse("Thunder", thunderNow ?? (releaseSeen ? null : thunderTrue), thunderInverted));
        if (blizzardTrue is not null || blizzardCharged)
            callouts.Add(TrueFalse("Blizzard", blizzardNow ?? (releaseSeen ? null : blizzardTrue), blizzardInverted));
        if (releaseSeen)
            callouts.Add(new Callout("Mana Release", StandOn(thunderNow, blizzardNow), Yellow, float.NaN));
    }

    private static bool? Released(bool? charged, bool? inverted) =>
        charged is null || inverted is null ? null : charged.Value != inverted.Value;

    /// <summary>The released casts prove what the markers predicted; if the two disagree, trust the cast.</summary>
    private void LatchReleased(string name, ref bool? final, bool? cast, bool? predicted)
    {
        if (final is not null || cast is null)
            return;

        final = cast;
        if (predicted != cast)
            log.Warning("Released {Name} is {Cast} but markers predicted {Predicted}", name, cast.Value, predicted?.ToString() ?? "unknown");
    }

    private static Callout TrueFalse(string name, bool? value, bool? inverted)
    {
        var suffix = inverted == true ? " (INVERTED)" : string.Empty;
        return value switch
        {
            true => new Callout(name, "TRUE" + suffix, Green, float.NaN),
            false => new Callout(name, "FALSE" + suffix, Red, float.NaN),
            null => new Callout(name, "?", Grey, float.NaN),
        };
    }

    private static string StandOn(bool? thunder, bool? blizzard)
    {
        if (thunder is null || blizzard is null)
            return "?"; // missed a charged cast or a marker; don't guess

        // false spells are the ones you need to stand in
        return (thunder.Value, blizzard.Value) switch
        {
            (false, false) => "STAND ON BOTH",
            (false, true) => "STAND ON THUNDER",
            (true, false) => "STAND ON BLIZZARD",
            (true, true) => "STAND ON NEITHER",
        };
    }

    private void ResetMana()
    {
        thunderTrue = null;
        blizzardTrue = null;
        thunderInverted = null;
        blizzardInverted = null;
        thunderFinal = null;
        blizzardFinal = null;
        releaseSeen = false;
        manaClearAtMs = 0;
        markers.Clear();
    }

    /// <summary>Dalamud has no head marker API, so watch the packets that deliver them.</summary>
    private void OnActorControl(
        uint entityId, uint category, uint p1, uint p2, uint p3, uint p4, uint p5, uint p6, uint p7, uint p8,
        GameObjectId targetId, bool replaying)
    {
        actorControlHook!.Original(entityId, category, p1, p2, p3, p4, p5, p6, p7, p8, targetId, replaying);

        if (category == ActorControlHeadMarker && p1 is MarkerThunder or MarkerThunderInverted or MarkerBlizzard or MarkerBlizzardInverted)
            markers.Enqueue(p1);
    }

    private static readonly Vector4 Red = new(1.0f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 Blue = new(0.45f, 0.70f, 1.0f, 1f);
    private static readonly Vector4 Yellow = new(1.0f, 0.90f, 0.30f, 1f);
    private static readonly Vector4 Green = new(0.45f, 1.0f, 0.55f, 1f);
    private static readonly Vector4 Grey = new(0.75f, 0.75f, 0.75f, 1f);

    private static Callout Resolve(uint status, ushort flag, float remaining)
    {
        var name = status switch
        {
            StatusCompressedWater => "Water",
            StatusForkedLightning => "Lightning",
            StatusAccelerationBomb => "Bomb",
            _ => "?",
        };

        if (flag != FlagReal && flag != FlagFake)
            return new Callout(name, "?", Grey, remaining); // never saw the flag; don't guess

        var real = flag == FlagReal;
        var (label, color) = status switch
        {
            StatusCompressedWater => real ? ("STACK", Blue) : ("SPREAD", Red),
            StatusForkedLightning => real ? ("SPREAD", Red) : ("STACK", Blue),
            StatusAccelerationBomb => real ? ("STAND STILL", Yellow) : ("MOVE", Green),
            _ => ("?", Grey),
        };

        return new Callout(name, label, color, remaining);
    }

    private void Reset()
    {
        if (flagAtGain.Count > 0) flagAtGain.Clear();
        if (gainedAtMs.Count > 0) gainedAtMs.Clear();
        if (previous.Count > 0) previous.Clear();
        lastLoggedFlag = 0;
        ResetMana();
        phaseActive = false;
        upsurgeCasting = false;
        upsurgeCount = 0;
        wipePending = false;
        chaosCasting = false;
        pendingChaosSpell = null;
        current = Array.Empty<Callout>();
        overlay.IsOpen = overlay.Preview;
    }

    public void Dispose()
    {
        commandManager.RemoveHandler(CommandName);
        actorControlHook?.Dispose();
        framework.Update -= OnUpdate;
        dutyState.DutyWiped -= onDutyWiped;
        pluginInterface.UiBuilder.OpenConfigUi -= TogglePreview;
        pluginInterface.UiBuilder.OpenMainUi -= TogglePreview;
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        windows.RemoveAllWindows();
    }
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool SendChaosCalls { get; set; }
}

internal readonly record struct Callout(string Debuff, string Label, Vector4 Color, float Remaining);

internal sealed class OverlayWindow : Window
{
    private readonly Func<Callout[]> source;

    public bool Preview;

    public OverlayWindow(Func<Callout[]> source)
        : base("Dancing Mad helper##overlay",
               ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav)
    {
        this.source = source;
        ShowCloseButton = false;     // visibility is driven by the fight state; collapse it with the title bar
        RespectCloseHotkey = false;
        BgAlpha = 0.7f; // keeps the colored text readable over bright floors
        Position = new Vector2(600, 300);
        PositionCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var items = source();

        ImGui.SetWindowFontScale(2.0f);
        if (items.Length == 0)
            ImGui.TextUnformatted("Waiting for callouts");

        foreach (var c in items)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, c.Color);
            ImGui.TextUnformatted(float.IsNaN(c.Remaining) ? $"{c.Debuff}: {c.Label}" : $"{c.Debuff}: {c.Label}  {c.Remaining:0}s");
            ImGui.PopStyleColor();
        }

        ImGui.SetWindowFontScale(1.0f);
    }
}
