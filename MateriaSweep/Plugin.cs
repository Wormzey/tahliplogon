using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Action = System.Action;

namespace MateriaSweep;

/// <summary>
/// Goes through the inventory and armoury chest one item at a time, extracting materia from fully spiritbonded
/// gear and optionally retrieving melded materia.
///
/// Slots are rescanned before every step rather than queued up front, so items moving mid-run can't send an
/// action at the wrong item.
///
/// Retrieval has a direct game function: <see cref="MaterializeEntryId.Retrieve"/> removes one meld per call
/// (confirmed in game, despite ClientStructs' remark calling that event Materia Extraction). Extraction has none, so
/// it goes through the Materialize window, where each row points at its inventory slot, and then MaterializeDialog.
/// Those windows are driven by callback values that aren't documented anywhere, so a step that doesn't produce its
/// expected result stops the run instead of carrying on.
/// </summary>
public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string CommandName = "/materiasweep";
    private const ushort FullSpiritbond = 10000;
    private const int MaxConsecutiveFailures = 2;
    private const double MinItemDelaySeconds = 1;
    private const double MaxItemDelaySeconds = 3;
    private const int MaxExtractCategories = 8;
    private const int SelectCategoryCallback = 1;
    private const int SelectRowCallback = 2;
    private const string ExtractWindowAddon = "Materialize";
    private const string ExtractDialogAddon = "MaterializeDialog";

    private static readonly TimeSpan StepDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromSeconds(1);

    private static readonly InventoryType[] Containers =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryWaist,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
    };

    private static readonly ConditionFlag[] BlockingConditions =
    {
        ConditionFlag.InCombat,
        ConditionFlag.BoundByDuty,
        ConditionFlag.Crafting,
        ConditionFlag.Gathering,
        ConditionFlag.Mounted,
    };

    private static readonly ConditionFlag[] BusyConditions =
    {
        ConditionFlag.Occupied39,
        ConditionFlag.MeldingMateria,
    };

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private readonly Configuration config;

    private readonly WindowSystem windows = new("MateriaSweep");
    private readonly MainWindow mainWindow;
    private readonly HashSet<(InventoryType Container, int Slot)> skipped = new();

    private Step step = Step.Idle;
    private Work current;
    private byte materiaBefore;
    private int categoriesTried;
    private DateTime stepStarted;
    private DateTime nextAction;
    private DateTime nextPreview;
    private int extracted;
    private int retrieved;
    private int failures;
    private int consecutiveFailures;

    // written on the framework thread, read by the draw thread
    private volatile string status = "Idle.";
    private volatile Preview preview = new(0, 0, 0);

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IObjectTable objectTable,
        IGameGui gameGui,
        ICondition condition,
        ICommandManager commandManager,
        IChatGui chatGui,
        IPluginLog log,
        IDataManager dataManager)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.condition = condition;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.log = log;
        this.dataManager = dataManager;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        mainWindow = new MainWindow(this, config, () => pluginInterface.SavePluginConfig(config));
        windows.AddWindow(mainWindow);

        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
        pluginInterface.UiBuilder.OpenConfigUi += mainWindow.Toggle;
        framework.Update += OnUpdate;
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Materia Sweep window. Add \"start\" or \"stop\" to control a run.",
        });
    }

    internal bool Running => step != Step.Idle;
    internal string Status => status;
    internal Preview CurrentPreview => preview;

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "start":
                RequestStart();
                break;
            case "stop":
                RequestStop();
                break;
            default:
                mainWindow.Toggle();
                break;
        }
    }

    internal void RequestStart() => framework.RunOnFrameworkThread(Start);

    internal void RequestStop() => framework.RunOnFrameworkThread(() => Finish("Stopped."));

    private void Start()
    {
        if (Running)
            return;

        if (objectTable.LocalPlayer is null)
        {
            status = "Not logged in.";
            return;
        }

        if (BlockingCondition() is { } blocked)
        {
            status = $"Can't start right now ({blocked}).";
            return;
        }

        skipped.Clear();
        extracted = 0;
        retrieved = 0;
        failures = 0;
        consecutiveFailures = 0;
        nextAction = DateTime.UtcNow;
        step = Step.Choosing;
        status = "Starting.";
    }

    private void OnUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;

        if (step == Step.Idle)
        {
            if (mainWindow.IsOpen && now >= nextPreview)
            {
                nextPreview = now + PreviewInterval;
                preview = BuildPreview();
            }

            return;
        }

        if (BlockingCondition() is { } blocked)
        {
            Finish($"Stopped ({blocked}).");
            return;
        }

        if (now < nextAction)
            return;

        switch (step)
        {
            case Step.Choosing:
                ChooseNext();
                break;
            case Step.SelectingRow:
                SelectExtractRow(now);
                break;
            case Step.OpeningDialog:
                ConfirmExtractDialog(now);
                break;
            case Step.Extracting:
                CheckExtraction(now);
                break;
            case Step.Retrieving:
                CheckRetrieval(now);
                break;
        }
    }

    private void ChooseNext()
    {
        if (IsBusy())
            return;

        var work = FindWork(BuildGearsetItemIds());
        if (work is null)
        {
            Finish("Finished.");
            return;
        }

        if (InventoryManager.Instance()->GetEmptySlotsInBag() == 0)
        {
            Finish("Stopped (inventory full).");
            return;
        }

        current = work.Value;
        var item = GetItem(current);
        status = $"{(current.Action == WorkAction.Extract ? "Extracting from" : "Retrieving from")} {ItemName(item)}.";
        stepStarted = DateTime.UtcNow;

        if (current.Action == WorkAction.Retrieve)
        {
            // extraction always comes first, so its window may still be up from the last item
            if (GetReadyAddon(ExtractWindowAddon) is not null)
            {
                CloseAddon(ExtractWindowAddon);
                SetStep(Step.Choosing);
                return;
            }

            materiaBefore = item->GetMateriaCount();
            log.Information("Retrieving from {Item}: {Details}", ItemName(item), DescribeMelds(item));
            EventFramework.Instance()->MaterializeItem(item, MaterializeEntryId.Retrieve);
            SetStep(Step.Retrieving);
            return;
        }

        if (GetReadyAddon(ExtractWindowAddon) is null)
            AgentMaterialize.Instance()->Show();

        categoriesTried = 0;
        SetStep(Step.SelectingRow);
    }

    private void CheckExtraction(DateTime now)
    {
        if (ExtractionDone() && !IsBusy())
        {
            extracted++;
            Succeed();
        }
        else if (now - stepStarted > ActionTimeout)
        {
            CloseAddon(ExtractDialogAddon);
            Fail("extraction didn't complete");
        }
    }

    private bool ExtractionDone()
    {
        var item = GetItem(current);
        return item is null || item->ItemId != current.ItemId || item->GetSpiritbondOrCollectability() < FullSpiritbond;
    }

    private void SelectExtractRow(DateTime now)
    {
        var window = GetReadyAddon(ExtractWindowAddon);
        if (window is null)
        {
            if (now - stepStarted > WindowTimeout)
                Fail("the extraction window didn't open");
            return;
        }

        var agent = AgentMaterialize.Instance();
        var rows = agent->ItemArraySorted;
        for (var i = 0; rows is not null && i < agent->ItemCount; i++)
        {
            var row = rows[i];
            if (row is null || row->Item is null || row->Item->Container != current.Container || row->Item->Slot != current.Slot)
                continue;

            FireExtractWindow(window, SelectRowCallback, i);
            stepStarted = DateTime.UtcNow;
            SetStep(Step.OpeningDialog);
            return;
        }

        // the list only shows one category at a time
        if (categoriesTried >= MaxExtractCategories)
        {
            log.Warning("Not listed in any category. Now on category {Category} with {Count} rows", agent->Category, agent->ItemCount);
            Fail("it isn't listed in the extraction window");
            return;
        }

        FireExtractWindow(window, SelectCategoryCallback, categoriesTried++);
        SetStep(Step.SelectingRow);
    }

    private static void FireExtractWindow(AtkUnitBase* window, int callback, int value)
    {
        var values = stackalloc AtkValue[2];
        values[0].SetInt(callback);
        values[1].SetInt(value);
        window->FireCallback(2, values, false);
    }

    private void ConfirmExtractDialog(DateTime now)
    {
        var dialog = (AddonMaterializeDialog*)GetReadyAddon(ExtractDialogAddon);
        if (dialog is null)
        {
            // the dialog doesn't always appear: selecting the row can start the extraction straight away
            if (ExtractionDone() || IsBusy())
                SetStep(Step.Extracting);
            else if (now - stepStarted > WindowTimeout)
                Fail("the extract window didn't open");
            return;
        }

        var selected = AgentMaterialize.Instance()->SelectedItem;
        if (selected is null || selected->Container != current.Container || selected->Slot != current.Slot)
        {
            log.Warning("Expected {Container} slot {Slot}, dialog has {Selected}", current.Container, current.Slot,
                selected is null ? "nothing" : $"{selected->Container} slot {selected->Slot}");
            CloseAddon(ExtractDialogAddon);
            Fail("the extract window showed a different item");
            return;
        }

        dialog->AtkUnitBase.FireCallbackInt(0);
        stepStarted = DateTime.UtcNow;
        SetStep(Step.Extracting);
    }

    private void CheckRetrieval(DateTime now)
    {
        var item = GetItem(current);
        var done = item is null || item->ItemId != current.ItemId || item->GetMateriaCount() < materiaBefore;

        if (done && !IsBusy())
        {
            retrieved++;
            Succeed();
        }
        else if (now - stepStarted > ActionTimeout)
        {
            log.Warning("Retrieval timed out. Busy: {Busy}, count before: {Before}, now: {Details}",
                IsBusy(), materiaBefore, item is null ? "no item" : DescribeMelds(item));
            Fail("retrieval didn't complete");
        }
    }

    private void Succeed()
    {
        consecutiveFailures = 0;
        step = Step.Choosing;
        nextAction = DateTime.UtcNow + TimeSpan.FromSeconds(MinItemDelaySeconds + (Random.Shared.NextDouble() * (MaxItemDelaySeconds - MinItemDelaySeconds)));
    }

    private void Fail(string reason)
    {
        var item = GetItem(current);
        var message = $"{(item is null ? "Slot" : ItemName(item))}: {reason}.";
        log.Warning("{Message}", message);
        chatGui.Print($"[Materia Sweep] {message}");

        skipped.Add((current.Container, current.Slot));
        failures++;
        consecutiveFailures++;

        if (consecutiveFailures >= MaxConsecutiveFailures)
            Finish("Stopped after repeated failures. See /xllog.");
        else
            SetStep(Step.Choosing);
    }

    private void Finish(string outcome)
    {
        if (!Running)
            return;

        // leave no window of ours open however the run ends
        if (step is Step.OpeningDialog)
            CloseAddon(ExtractDialogAddon);
        CloseAddon(ExtractWindowAddon);

        step = Step.Idle;
        nextPreview = default;
        status = $"{outcome} Extracted {extracted}, retrieved {retrieved}, failed {failures}.";
        chatGui.Print($"[Materia Sweep] {status}");
    }

    private void SetStep(Step next)
    {
        step = next;
        nextAction = DateTime.UtcNow + StepDelay;
    }

    private Work? FindWork(HashSet<uint> gearsetItems)
    {
        Work? retrieve = null;

        foreach (var (container, slot) in GearSlots())
        {
            var item = InventoryManager.Instance()->GetInventorySlot(container, slot);
            if (skipped.Contains((container, slot)) || gearsetItems.Contains(item->GetBaseItemId()))
                continue;

            if (config.Extract && CanExtract(item))
                return new Work(container, slot, item->ItemId, WorkAction.Extract);

            if (config.Retrieve && retrieve is null && HasMelds(item))
                retrieve = new Work(container, slot, item->ItemId, WorkAction.Retrieve);
        }

        return retrieve;
    }

    private Preview BuildPreview()
    {
        if (objectTable.LocalPlayer is null)
            return new Preview(0, 0, 0);

        var extractable = 0;
        var melded = 0;
        var protectedItems = 0;
        var gearsetItems = BuildGearsetItemIds();

        foreach (var (container, slot) in GearSlots())
        {
            var item = InventoryManager.Instance()->GetInventorySlot(container, slot);
            var canExtract = CanExtract(item);
            var hasMelds = HasMelds(item);

            if (!canExtract && !hasMelds)
                continue;

            if (gearsetItems.Contains(item->GetBaseItemId()))
            {
                protectedItems++;
                continue;
            }

            extractable += canExtract ? 1 : 0;
            melded += hasMelds ? 1 : 0;
        }

        return new Preview(extractable, melded, protectedItems);
    }

    /// <summary>
    /// Slots holding equippable items. Loose materia also reports a materia count, so the item's own fields can't
    /// tell gear from anything else in the bags.
    /// </summary>
    private List<(InventoryType Container, int Slot)> GearSlots()
    {
        var manager = InventoryManager.Instance();
        var slots = new List<(InventoryType, int)>();

        foreach (var container in Containers)
        {
            var size = manager->GetInventoryContainer(container)->GetSize();
            for (var slot = 0; slot < size; slot++)
            {
                var item = manager->GetInventorySlot(container, slot);
                if (item is not null && !item->IsEmpty() && IsGear(item))
                    slots.Add((container, slot));
            }
        }

        return slots;
    }

    private bool IsGear(InventoryItem* item) =>
        dataManager.GetExcelSheet<Item>().GetRowOrDefault(item->GetBaseItemId())?.EquipSlotCategory.RowId is > 0;

    /// <summary>
    /// The materia fields hold other data on some items (an Eternity Ring keeps its anniversary date there), so they
    /// only count as melds on an item with materia slots.
    /// </summary>
    private bool HasMelds(InventoryItem* item) =>
        item->GetMateriaCount() > 0 && dataManager.GetExcelSheet<Item>().GetRowOrDefault(item->GetBaseItemId())?.MateriaSlotCount is > 0;

    private string DescribeMelds(InventoryItem* item)
    {
        var row = dataManager.GetExcelSheet<Item>().GetRowOrDefault(item->GetBaseItemId());
        return $"{item->Container} slot {item->Slot}, item {item->ItemId}, flags {item->Flags}, sheet slots {row?.MateriaSlotCount}, "
            + $"count {item->GetMateriaCount()}, ids [{string.Join(", ", item->Materia.ToArray())}], "
            + $"grades [{string.Join(", ", item->MateriaGrades.ToArray())}]";
    }

    private static bool CanExtract(InventoryItem* item) =>
        item->GetSpiritbondOrCollectability() >= FullSpiritbond && !item->Flags.HasFlag(InventoryItem.ItemFlags.Collectable);

    /// <summary>
    /// Base item IDs in any gear set. Deliberately ignores quality and melds, so every copy of a registered item
    /// counts as registered: skipping a spare is much cheaper than stripping the real one.
    /// </summary>
    private HashSet<uint> BuildGearsetItemIds()
    {
        var ids = new HashSet<uint>();
        if (!config.SkipGearsetItems)
            return ids;

        var module = RaptureGearsetModule.Instance();
        for (var id = 0; id < 100; id++)
        {
            if (!module->IsValidGearset(id))
                continue;

            var gearset = module->GetGearset(id);
            foreach (var index in Enum.GetValues<RaptureGearsetModule.GearsetItemIndex>())
            {
                var itemId = gearset->GetItem(index).ItemId % 1_000_000;
                if (itemId != 0)
                    ids.Add(itemId);
            }
        }

        return ids;
    }

    private static InventoryItem* GetItem(Work work)
    {
        var item = InventoryManager.Instance()->GetInventorySlot(work.Container, work.Slot);
        return item is null || item->IsEmpty() ? null : item;
    }

    private string ItemName(InventoryItem* item) =>
        dataManager.GetExcelSheet<Item>().GetRowOrDefault(item->GetBaseItemId())?.Name.ExtractText() ?? $"item {item->GetBaseItemId()}";

    private AtkUnitBase* GetReadyAddon(string name)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(name).Address;
        return addon is not null && addon->IsReady && addon->IsVisible ? addon : null;
    }

    private void CloseAddon(string name)
    {
        var addon = GetReadyAddon(name);
        if (addon is not null)
            addon->Close(true);
    }

    private bool IsBusy() => BusyConditions.Any(flag => condition[flag]);

    private ConditionFlag? BlockingCondition()
    {
        foreach (var flag in BlockingConditions)
        {
            if (condition[flag])
                return flag;
        }

        return null;
    }

    public void Dispose()
    {
        commandManager.RemoveHandler(CommandName);
        framework.Update -= OnUpdate;
        pluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
        pluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        windows.RemoveAllWindows();
    }

    private enum Step
    {
        Idle,
        Choosing,
        Extracting,
        SelectingRow,
        OpeningDialog,
        Retrieving,
    }

    private enum WorkAction
    {
        Extract,
        Retrieve,
    }

    private readonly record struct Work(InventoryType Container, int Slot, uint ItemId, WorkAction Action);
}

internal sealed record Preview(int Extractable, int Melded, int Protected);

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool Extract { get; set; } = true;
    public bool Retrieve { get; set; } = false;
    public bool SkipGearsetItems { get; set; } = true;
}

internal sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly Configuration config;
    private readonly Action save;

    public MainWindow(Plugin plugin, Configuration config, Action save)
        : base("Materia Sweep##main", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
        this.config = config;
        this.save = save;
    }

    public override void Draw()
    {
        var changed = false;

        ImGui.BeginDisabled(plugin.Running);

        var extract = config.Extract;
        if (ImGui.Checkbox("Extract materia from fully spiritbonded gear", ref extract))
        {
            config.Extract = extract;
            changed = true;
        }

        var retrieve = config.Retrieve;
        if (ImGui.Checkbox("Retrieve melded materia", ref retrieve))
        {
            config.Retrieve = retrieve;
            changed = true;
        }

        var skipGearsets = config.SkipGearsetItems;
        if (ImGui.Checkbox("Skip items in a gear set", ref skipGearsets))
        {
            config.SkipGearsetItems = skipGearsets;
            changed = true;
        }

        ImGui.EndDisabled();

        var preview = plugin.CurrentPreview;
        ImGui.Separator();
        ImGui.TextUnformatted($"Ready to extract: {preview.Extractable}");
        ImGui.TextUnformatted($"Items with melds: {preview.Melded}");
        if (config.SkipGearsetItems)
            ImGui.TextUnformatted($"Protected by gear sets: {preview.Protected}");

        ImGui.Separator();
        if (plugin.Running)
        {
            if (ImGui.Button("Stop"))
                plugin.RequestStop();
        }
        else
        {
            ImGui.BeginDisabled(!config.Extract && !config.Retrieve);
            if (ImGui.Button("Start"))
                plugin.RequestStart();
            ImGui.EndDisabled();
        }

        ImGui.TextUnformatted(plugin.Status);

        if (changed)
            save();
    }
}
