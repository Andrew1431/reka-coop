using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Popups everyone should see.
// Loot: the game's "you received" box (trial and quest rewards, loot bags) shows on the copy that ran the reward; the
// items reach everyone through the shared pack, so the others only show the same box.
// Story choices (POI_Decision, two options after a line of dialogue): they come up on the host, where conversations run.
// The player who led that conversation chooses: the host sends it to them and shows everyone else the same choice to
// watch; the leader's pick runs the option on the host, and the story's effects reach everyone as usual.
// The host's other story popups (a quest chapter started or completed, a tool unlocked, "something has been added to
// your map") are shown to guests too, to read: their buttons only close them there (a popup offering more than one
// button shows a single Continue), since what the buttons do already happened on the host. The same message coming up
// on a guest's own copy within a few seconds isn't shown twice.
public static class PopupSync
{
    private enum Kind : byte { Loot = 1, Decision = 2, Watch = 3, Choice = 4, Chapter = 5, Unlock = 6, Message = 7, Chosen = 8 }

    private const float SameWindow = 15f;
    private static readonly Dictionary<string, float> Shown = new();  // guest: message -> when shown here

    private static bool ShownRecently(string key) => Shown.TryGetValue(key, out var at) && Time.unscaledTime - at < SameWindow;

    // Guest: false when the host's copy of this popup was just shown here.
    internal static bool GuestLocal(string key)
    {
        if (Replaying || _session == null || !_session.IsConnected || _session.Role != Role.Client) return true;
        if (ShownRecently(key)) { Plugin.Logger.LogInfo($"Popups: already showed the host's \"{key}\""); return false; }
        Shown[key] = Time.unscaledTime;
        return true;
    }

    private static NetDataWriter HostBegin(Kind kind)
    {
        if (Replaying || _session == null || !_session.IsConnected || _session.Role != Role.Host) return null;
        var w = _session.BeginMessage(MsgType.Popup);
        w.Put((byte)kind);
        return w;
    }

    internal static void LocalChapter(TasksTracker.ChapterTag chapter, string name, bool completed)
    {
        if (HostBegin(Kind.Chapter) is not { } w) return;
        w.Put((int)chapter); w.Put(name ?? ""); w.Put(completed);
        _session.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"Popups: sent the chapter popup {name} (completed={completed})");
    }

    internal static string UseItemIdOf(UltimateRadialButtonInfo info)
    {
        var mgr = UseItemManager.Instance;
        if (info != null && mgr != null)
            foreach (var item in mgr.GetAvailableItems())
                if (item != null && item.buttonInfo != null && item.buttonInfo.Pointer == info.Pointer) return item.ID;
        return info?.key;
    }

    internal static void LocalUnlock(UltimateRadialButtonInfo info, bool epic)
    {
        if (info == null || HostBegin(Kind.Unlock) is not { } w) return;
        var id = UseItemIdOf(info);
        w.Put(id ?? ""); w.Put(epic);
        _session.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"Popups: sent the unlock popup for {id}");
    }

    // Host: a question that is the host's alone to answer is being put up (whether to travel on, asked of whoever
    // drives the hut). A guest shown it could only press "Continue", and as a passenger not even that.
    internal static bool HostsQuestion;

    internal static void LocalMessage(string message, Sprite sprite, Il2CppSystem.Collections.Generic.List<string> labels, int type)
    {
        if (HostsQuestion) return;
        if (string.IsNullOrEmpty(message) || HostBegin(Kind.Message) is not { } w) return;
        w.Put(message); w.Put(sprite != null ? sprite.name : ""); w.Put(type);
        w.Put(labels != null && labels.Count == 1 ? labels[0] ?? "" : "");
        _session.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"Popups: sent \"{message}\"");
    }

    private static void ShowHosts(Kind kind, NetDataReader r)
    {
        Replaying = true;
        try
        {
            if (kind == Kind.Chapter)
            {
                var chapter = (TasksTracker.ChapterTag)r.GetInt(); var name = r.GetString(); bool done = r.GetBool();
                var key = "chapter:" + name + done;
                if (ShownRecently(key)) return;
                Shown[key] = Time.unscaledTime;
                Live<PopUp_Chapter>()?.QueuePopUp(chapter, name, done, null);
                Plugin.Logger.LogInfo($"Popups: showed the host's chapter popup {name}");
            }
            else if (kind == Kind.Unlock)
            {
                var id = r.GetString(); bool epic = r.GetBool();
                if (ShownRecently("unlock:" + id)) return;
                Shown["unlock:" + id] = Time.unscaledTime;
                var info = UseItemManager.Instance?.GetItemByID(id)?.buttonInfo;
                if (info == null) { Plugin.Logger.LogInfo($"Popups: no use item {id} here for its unlock popup"); return; }
                Live<PopUp_UseItemUnlock>()?.QueuePopUp(info, new UnityEngine.Events.UnityEvent(), epic);
                Plugin.Logger.LogInfo($"Popups: showed the host's unlock popup for {id}");
            }
            else
            {
                var message = r.GetString(); var spriteName = r.GetString(); int type = r.GetInt(); var label = r.GetString();
                if (ShownRecently(message)) return;
                Shown[message] = Time.unscaledTime;
                var labels = new Il2CppSystem.Collections.Generic.List<string>();
                labels.Add(label != "" ? label : "Continue");
                var calls = new Il2CppSystem.Collections.Generic.List<Il2CppSystem.Action>();
                calls.Add((Il2CppSystem.Action)(System.Action)Nothing);
                var sprite = spriteName == "" ? null : Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(x => x.name == spriteName);
                if (sprite != null) Live<PopUp_GenericWithSprite>()?.QueuePopUp(message, sprite, labels, calls);
                else Live<PopUp_Generic>()?.QueuePopUp(message, labels, calls, (PopUp_Generic.PopUpType)type);
                Plugin.Logger.LogInfo($"Popups: showed the host's \"{message}\"");
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Popups: can't show the host's {kind} popup: {e.Message}"); }
        finally { Replaying = false; }
    }

    private static void Nothing() { }

    // A choice box shown here only to watch; it closes once the choice is made (on the host).
    private static bool _watching;

    // Host: the host chose in its box, or a guest leader's pick ran here: watchers' boxes close.
    internal static void LocalDecisionClosed()
    {
        if (Replaying || HostBegin(Kind.Chosen) is not { } w) return;
        _session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    internal static bool Replaying;
    private static CoopSession _session;
    private static Dictionary<string, LootablePossession> _possessions;
    private static Dictionary<string, BuildingBlock> _blocks;

    public static void Attach(CoopSession session)
    {
        _session = session;
        session.Message += (from, type, r) => { if (type == MsgType.Popup) OnMessage(session, from, r); };
    }

    // Any copy: the game queued its loot box here.
    internal static void LocalLoot(string title, Il2CppSystem.Collections.Generic.List<PossessionStack> stacks, Il2CppSystem.Collections.Generic.List<BuildingBlock> blocks)
    {
        if (Replaying || _session == null || !_session.IsConnected) return;
        var w = _session.BeginMessage(MsgType.Popup);
        w.Put((byte)Kind.Loot);
        w.Put(title ?? "");
        var items = new List<(string, int)>();
        if (stacks != null) foreach (var st in stacks) if (st?._type != null) items.Add((st._type.name, st._amount));
        w.Put(items.Count);
        foreach (var (name, amount) in items) { w.Put(name); w.Put(amount); }
        var bbs = new List<string>();
        if (blocks != null) foreach (var bb in blocks) if (bb != null) bbs.Add(bb.name);
        w.Put(bbs.Count);
        foreach (var name in bbs) w.Put(name);
        _session.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"Popups: sent the loot box \"{title}\" ({items.Count} items, {bbs.Count} blocks)");
    }

    private static void ShowLoot(string title, List<(string name, int amount)> items, List<string> bbs)
    {
        var popup = Live<PopUp_Loot>();
        if (popup == null) { Plugin.Logger.LogWarning("Popups: no loot box here"); return; }
        _possessions ??= Index<LootablePossession>();
        _blocks ??= Index<BuildingBlock>();
        var stacks = new Il2CppSystem.Collections.Generic.List<PossessionStack>();
        foreach (var (name, amount) in items)
            if (_possessions.TryGetValue(name, out var p)) stacks.Add(new PossessionStack(p, amount));
        var blocks = new Il2CppSystem.Collections.Generic.List<BuildingBlock>();
        foreach (var name in bbs) if (_blocks.TryGetValue(name, out var bb)) blocks.Add(bb);
        Replaying = true;
        try { popup.QueuePopUp(title, stacks, blocks); }
        catch (Exception e) { Plugin.Logger.LogWarning($"Popups: can't show the loot box: {e.Message}"); }
        finally { Replaying = false; }
        Plugin.Logger.LogInfo($"Popups: showed the loot box \"{title}\" ({stacks.Count} items, {blocks.Count} blocks)");
    }

    // Host: a story choice is coming up. False when it belongs to a guest (who gets it) instead of the host.
    internal static bool LocalDecision(POI_Decision d)
    {
        if (Replaying || _session == null || !_session.IsConnected) return true;
        // A guest's copy reaches the choice too: the condition that raises it (the raven man's GetQuest) is copied to every
        // copy by ConditionSync. Its own box let the guest choose by itself (and run that option there alone: the
        // crows went out of sync); the host's box comes instead.
        if (_session.Role != Role.Host)
        {
            Plugin.Logger.LogInfo($"Popups: the choice at {ScenePath.Of(d.transform)} comes from the host");
            return false;
        }
        int leader = DialogueSync.RecentLeader;
        var path = ScenePath.Of(d.transform);
        foreach (var p in _session.Players.Values)
        {
            if (!p.Ready) continue;
            var w = _session.BeginMessage(MsgType.Popup);
            w.Put((byte)(p.PlayerId == leader ? Kind.Decision : Kind.Watch));
            w.Put(path);
            w.Put(leader);
            _session.SendTo(p.PlayerId, w, DeliveryMethod.ReliableOrdered);
        }
        if (leader <= 0) return true;  // the host chooses, as the game would
        Plugin.Logger.LogInfo($"Popups: {NameTags.NameOf(leader)} chooses at {path}");
        ShowDecision(d, -1, leader);
        return false;
    }

    // option -1: shown to watch; the buttons only close it.
    private static void ShowDecision(POI_Decision d, int forHost, int chooser)
    {
        var popup = Live<PopUp_Decision>();
        if (popup == null) { Plugin.Logger.LogWarning("Popups: no choice box here"); return; }
        var path = ScenePath.Of(d.transform);
        Action pick1 = () => Pick(path, 1, forHost), pick2 = () => Pick(path, 2, forHost);
        Replaying = true;
        try { popup.QueueDecisionPopUp(d.Option1, d.Option2, pick1, pick2); }
        catch (Exception e) { Plugin.Logger.LogWarning($"Popups: can't show the choice: {e.Message}"); }
        finally { Replaying = false; }
        if (forHost < 0) CoopDriver.Notice($"{NameTags.NameOf(chooser)} is choosing.");
        _watching = forHost < 0;
    }

    // forHost: 1 = we choose for the host, -1 = only watching.
    private static void Pick(string path, int option, int forHost)
    {
        if (forHost < 0 || _session == null) return;
        var w = _session.BeginMessage(MsgType.Popup);
        w.Put((byte)Kind.Choice);
        w.Put(path);
        w.Put(option);
        _session.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"Popups: chose option {option} at {path}");
    }

    private static void Choose(POI_Decision d, int option)
    {
        Replaying = true;
        try
        {
            (option == 1 ? d.OnOption1 : d.OnOption2)?.Invoke();
            d.OnAnyOptionChosen?.Invoke();
            if (_watching) { _watching = false; Live<PopUp_Decision>()?.ForceClosePopup(); }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Popups: running option {option} failed: {e.Message}"); }
        finally { Replaying = false; }
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (kind == Kind.Loot)
        {
            var title = r.GetString();
            var items = new List<(string, int)>();
            int n = r.GetInt();
            for (int i = 0; i < n; i++) items.Add((r.GetString(), r.GetInt()));
            var bbs = new List<string>();
            int m = r.GetInt();
            for (int i = 0; i < m; i++) bbs.Add(r.GetString());
            ShowLoot(title, items, bbs);
            if (session.Role == Role.Host)
            {
                var w = session.BeginMessage(MsgType.Popup);
                w.Put((byte)Kind.Loot);
                w.Put(title);
                w.Put(items.Count);
                foreach (var (name, amount) in items) { w.Put(name); w.Put(amount); }
                w.Put(bbs.Count);
                foreach (var name in bbs) w.Put(name);
                session.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId: from.PlayerId);
            }
            return;
        }
        if (kind == Kind.Chapter || kind == Kind.Unlock || kind == Kind.Message) { ShowHosts(kind, r); return; }
        if (kind == Kind.Chosen)
        {
            if (!_watching) return;
            _watching = false;
            Replaying = true;
            try { Live<PopUp_Decision>()?.ForceClosePopup(); Plugin.Logger.LogInfo("Popups: the choice was made; closed it here"); }
            catch (Exception e) { Plugin.Logger.LogWarning($"Popups: can't close the choice: {e.Message}"); }
            finally { Replaying = false; }
            return;
        }
        var path = r.GetString();
        var d = ScenePath.Resolve(path)?.GetComponent<POI_Decision>();
        if (d == null) { Plugin.Logger.LogWarning($"Popups: no choice at {path} here"); return; }
        if (kind == Kind.Decision || kind == Kind.Watch)
        {
            int leader = r.GetInt();
            if (kind == Kind.Watch && leader < 0) leader = 0;
            ShowDecision(d, kind == Kind.Decision ? 1 : -1, leader);
        }
        else if (kind == Kind.Choice && session.Role == Role.Host)
        {
            int option = r.GetInt();
            Plugin.Logger.LogInfo($"Popups: {NameTags.NameOf(from.PlayerId)} chose option {option} at {path}");
            Choose(d, option);
            LocalDecisionClosed();
        }
    }

    private static T Live<T>() where T : UnityEngine.Object =>
        Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(x => x is Component c && c.gameObject.scene.IsValid());

    private static Dictionary<string, T> Index<T>() where T : UnityEngine.Object
    {
        var all = new Dictionary<string, T>();
        foreach (var o in Resources.FindObjectsOfTypeAll<T>()) all.TryAdd(o.name, o);
        return all;
    }
}

[HarmonyPatch]
internal static class PopupHooks
{
    [HarmonyPrefix, HarmonyPatch(typeof(MapBorderFogManager), nameof(MapBorderFogManager.TriggerTravel))]
    private static void TravelAsked() => PopupSync.HostsQuestion = true;

    [HarmonyFinalizer, HarmonyPatch(typeof(MapBorderFogManager), nameof(MapBorderFogManager.TriggerTravel))]
    private static void TravelAskedDone() => PopupSync.HostsQuestion = false;

    [HarmonyPostfix, HarmonyPatch(typeof(PopUp_Loot), nameof(PopUp_Loot.QueuePopUp))]
    private static void Loot(string __0, Il2CppSystem.Collections.Generic.List<PossessionStack> __1, Il2CppSystem.Collections.Generic.List<BuildingBlock> __2) =>
        PopupSync.LocalLoot(__0, __1, __2);

    [HarmonyPrefix, HarmonyPatch(typeof(PopUp_Chapter), nameof(PopUp_Chapter.QueuePopUp))]
    private static bool Chapter(PopUp_Chapter __instance, TasksTracker.ChapterTag __0, string __1, bool __2, Il2CppSystem.Action __3)
    {
        if (!PopupSync.GuestLocal("chapter:" + __1 + __2)) return false;
        // Just arrived somewhere with guests still on their way: the card waits for them (this runs again then).
        if (!__2 && WorldTransfer.HoldForGuests($"the chapter card {__1}", () => __instance.QueuePopUp(__0, __1, __2, __3))) return false;
        PopupSync.LocalChapter(__0, __1, __2);
        return true;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PopUp_UseItemUnlock), nameof(PopUp_UseItemUnlock.QueuePopUp))]
    private static bool Unlock(UltimateRadialButtonInfo __0, bool __2)
    {
        if (!PopupSync.GuestLocal("unlock:" + PopupSync.UseItemIdOf(__0))) return false;
        PopupSync.LocalUnlock(__0, __2);
        return true;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PopUp_Generic), nameof(PopUp_Generic.QueuePopUp))]
    private static bool Generic(string __0, Il2CppSystem.Collections.Generic.List<string> __1, PopUp_Generic.PopUpType __3)
    {
        if (!PopupSync.GuestLocal(__0 ?? "")) return false;
        PopupSync.LocalMessage(__0, null, __1, (int)__3);
        return true;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PopUp_GenericWithSprite), nameof(PopUp_GenericWithSprite.QueuePopUp))]
    private static bool WithSprite(string __0, Sprite __1, Il2CppSystem.Collections.Generic.List<string> __2)
    {
        if (!PopupSync.GuestLocal(__0 ?? "")) return false;
        PopupSync.LocalMessage(__0, __1, __2, 0);
        return true;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PopUp_Decision), nameof(PopUp_Decision.ButtonClicked))]
    private static void DecisionClicked() => PopupSync.LocalDecisionClosed();

    [HarmonyPrefix, HarmonyPatch(typeof(POI_Decision), nameof(POI_Decision.TriggerDecision))]
    private static bool Decision(POI_Decision __instance) => PopupSync.LocalDecision(__instance);
}
