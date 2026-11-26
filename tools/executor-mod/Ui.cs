// Generic screen automation: GET /ui lists what can be clicked on the current screen, POST /click?i=N clicks it.
// The node types and the click methods (ForceClick, or the Pressed signal for card holders) are the ones the game's own
// AutoSlay handlers use (MegaCrit.Sts2.Core.AutoSlay.Handlers); choosing what to click stays in the out-of-process driver.
using Godot;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Entities.Merchant;
using CardSelection = MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

namespace Sts2Executor;

public static partial class ExecutorMod
{
    static Node? Scope()
    {
        // The map is not an overlay, but a finished rewards overlay can stay on the stack beneath it.
        if (NMapScreen.Instance is { IsOpen: true } map) return map;
        if (NOverlayStack.Instance?.Peek() is Control top && top.IsVisibleInTree()) return top;
        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        // Outside a run (main menu, character select) fall back to the whole game tree.
        return root.GetNodeOrNull("/root/Game/RootSceneContainer/Run/RoomContainer") ?? root.GetNodeOrNull("/root/Game");
    }

    static bool Interesting(Node n) =>
        (n is NRewardButton or NCardHolder or NMapPoint or NRestSiteButton or NEventOptionButton or NProceedButton
            or NConfirmButton or NBackButton or NMerchantSlot or CardSelection.NChoiceSelectionSkipButton
            or CardSelection.NCardRewardAlternativeButton or NTreasureRoomRelicHolder
            || (n is NClickableControl && n.Name == "Chest" && n.GetParent() is NTreasureRoom))
        && (n is not Control c || c.IsVisibleInTree());

    static List<Node> Clickables()
    {
        Node? scope = Scope();
        return scope == null ? [] : UiHelper.FindAll<Node>(scope).Where(Interesting).ToList();
    }

    static string Merchant(MerchantEntry e) => e switch
    {
        MerchantCardEntry c => "card:" + c.CreationResult?.Card.Id.Entry,
        MerchantRelicEntry r => "relic:" + r.Model?.Id.Entry,
        MerchantPotionEntry p => "potion:" + p.Model?.Id.Entry,
        _ => e.GetType().Name,
    };

    static object Describe(Node n, int i) => n switch
    {
        NRewardButton b => new { i, type = "reward", kind = b.Reward?.GetType().Name, text = b.Reward?.Description.GetFormattedText(), enabled = b.IsEnabled },
        NCardHolder h => new { i, type = "card", id = h.CardModel?.Id.Entry, up = h.CardModel?.CurrentUpgradeLevel ?? 0 },
        NMapPoint m => new { i, type = "map", row = m.Point.coord.row, col = m.Point.coord.col, state = m.State.ToString(), enabled = m.IsEnabled },
        NRestSiteButton r => new { i, type = "rest", id = r.Option.OptionId, enabled = r.Option.IsEnabled },
        NEventOptionButton e => new { i, type = "event", key = e.Option.TextKey, text = e.Option.Title.GetFormattedText(), locked = e.Option.IsLocked, proceed = e.Option.IsProceed },
        NTreasureRoomRelicHolder h => new { i, type = "relic", enabled = h.IsEnabled },
        NClickableControl when n.Name == "Chest" => new { i, type = "chest" },
        NMerchantCardRemoval => new { i, type = "removal" },
        NMerchantSlot s => new { i, type = "shop", entry = Merchant(s.Entry), cost = s.Entry.Cost, stocked = s.Entry.IsStocked, enabled = s.Entry.EnoughGold },
        _ => new { i, type = n.GetType().Name },
    };

    static object Ui()
    {
        List<Node> nodes = Clickables();
        return new
        {
            ok = true,
            scope = Scope()?.GetType().Name,
            room = RunManager.Instance.DebugOnlyGetState()?.BaseRoom?.RoomType.ToString(),
            items = nodes.Select((n, i) => Describe(n, i)).ToArray(),
        };
    }

    static object Click(int index)
    {
        List<Node> nodes = Clickables();
        if (index < 0 || index >= nodes.Count) return new { ok = false, error = $"no clickable #{index} (have {nodes.Count})" };
        switch (nodes[index])
        {
            case NCardHolder holder: holder.EmitSignal(NCardHolder.SignalName.Pressed, holder); break;
            // Merchant slots are plain Controls: the game buys on mouse release, which calls PurchaseLocalEntry.
            case NMerchantSlot slot: _ = RunManager.Instance.MerchantSynchronizer.PurchaseLocalEntry(slot.Entry); break;
            case NClickableControl control: control.ForceClick(); break;
            default: return new { ok = false, error = "not clickable" };
        }
        return new { ok = true };
    }
}

public static partial class ExecutorMod
{
    static object OpenShop()
    {
        NMerchantRoom? room = NMerchantRoom.Instance;
        if (room == null || !room.Visible) return new { ok = false, error = "no merchant room" };
        room.OpenInventory();
        return new { ok = true };
    }
}
