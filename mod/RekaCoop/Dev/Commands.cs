using System.Collections.Generic;
using System.Linq;
#if DEVBUILD
using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace RekaCoop.Dev;

// Write one command per line to `cmd.request` in the game folder (mod/cmd.ps1 does this). Results go to the log.
//   skiptut         leave the tutorial for the first generated map (ritual done, starting items)
//   status          log map, chapter, hut, clock and unlocked items
//   unblock [step]  report save blockers; step: confirm the chapter card, skip dialogue, unblock movement
//   click <label>   press every visible UI button with that label (e.g. OK)
//   coll            write every collectible and destructible (key, state) to collectibles.txt
//   collect [type]  collect the nearest collectible (item name contains type)
//   hit [dmg] [tree]   damage the nearest destructible (or tree)
//   woodcut         allow tree damage (normally unlocked by the story)
//   inv             list the local inventory
//   give <id> <n>   add items to the local inventory (e.g. Item_Collected_Basic_Wood)
//   hut             describe the hut (paths, mode, mirroring)
//   hutcall         call the hut to the local player (a guest asks the host)
//   hutstand / hutsit
//   hutgo <dx> <dz> call the hut to a point relative to where it stands
//   board           put the local player on the hut floor
//   hutfix          switch the hut back on next to the player
//   unlockall       unlock every use item
//   save            manual save into the current slot
//   gear            describe the puppets' gear copies
//   carry           what everyone is carrying
//   animals         wild animal streaming (sent/received/mirrored) and AI warps
//   equip [n]       list the tools, or hold tool n of the list (-1: put away)
//   rig             dump the local player's rig, gear and animator layers
//   time <hour>     set the time of day
//   weather rain    heavy rain, low cloud
//   weather clear   clear sky
//   walk <x> <y> <seconds>   hold a camera-relative move input (y forward)
//   pshot [front|back|left|right]   photograph the nearest puppet from that side into puppet.png
internal static class Commands
{
    private static (long heap, float at) _allocAt;
    private static Dictionary<string, string> _hutCols;
    private static Collider _lastGround;
    private static float _nextCheck, _walkUntil = -1;
    private static int _restoreCameraFrame = -1;
    private static GameObject _photoCam;

    private static float _circleFrom, _circleUntil = -1;
    private static Vector3? _walkTo;
    private static float _walkToUntil;

    public static void Tick()
    {
        if (_restoreCameraFrame >= 0 && Time.frameCount >= _restoreCameraFrame)
        {
            _restoreCameraFrame = -1;
            if (_photoCam != null) UnityEngine.Object.Destroy(_photoCam);
        }
        if (_circleUntil >= 0)
        {
            float t = Time.unscaledTime - _circleFrom;
            bool on = Time.unscaledTime < _circleUntil;
            PlayerMovement_2021.DebugMoveInputOverride = on ? new Il2CppSystem.Nullable<Vector2>(new Vector2(Mathf.Sin(t * 0.6f), Mathf.Cos(t * 0.6f))) : new Il2CppSystem.Nullable<Vector2>();
            if (!on) _circleUntil = -1;
        }
        Trace.Tick();
        NoInput.Tick();
        DialogueProbe.Tick();
        if (_walkTo is Vector3 goal)
        {
            // walkto: real movement input toward the goal, relative to the camera (the override is (forward, right))
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            var cam = Camera.main;
            var to = goal - (me != null ? me.transform.position : goal);
            to.y = 0;
            if (me == null || cam == null || to.magnitude < 1.2f || Time.unscaledTime > _walkToUntil)
            {
                _walkTo = null;
                PlayerMovement_2021.DebugMoveInputOverride = new Il2CppSystem.Nullable<Vector2>();
                Plugin.Logger.LogInfo($"walkto: stopped {to.magnitude:0.0} m from the goal");
            }
            else
            {
                var fwd = Quaternion.Euler(0, cam.transform.eulerAngles.y, 0) * Vector3.forward;  // a top-down carry camera has no flat forward
                var right = Vector3.Cross(Vector3.up, fwd);
                var dir = to.normalized;
                PlayerMovement_2021.DebugMoveInputOverride = new Il2CppSystem.Nullable<Vector2>(new Vector2(Vector3.Dot(dir, fwd), Vector3.Dot(dir, right)));
            }
        }
        if (_walkUntil >= 0 && Time.unscaledTime >= _walkUntil)
        {
            _walkUntil = -1;
            PlayerMovement_2021.DebugMoveInputOverride = new Il2CppSystem.Nullable<Vector2>();
        }
        FuzzTick();
        TalkThroughTick();
        var request = Path.Combine(BepInEx.Paths.GameRootPath, "cmd.request");
        if (!File.Exists(request)) return;
        string[] lines;
        try { lines = File.ReadAllLines(request); File.Delete(request); }
        catch (IOException) { return; }   // the sender still has it open; next time
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { Plugin.Logger.LogInfo($"[cmd] {line} -> {Run(line.Trim().Split(' '))}"); }
            catch (Exception e) { Plugin.Logger.LogError($"[cmd] {line} failed: {e}"); }
        }
    }

    // Dev: talkthrough [seconds]: clicks a talk on as fast as the game takes it, the way a player hammering the key
    // would, until nobody is talking, a choice comes up, or the time is over. The regression suite's talks took a
    // command round trip and a second's wait per line.
    private static float _talkUntil = -1, _talkNext, _talkQuietSince = -1;
    private static int _talkClicks;

    private static void TalkThroughTick()
    {
        if (_talkUntil < 0 || Time.unscaledTime < _talkNext) return;
        _talkNext = Time.unscaledTime + 0.15f;
        var ui = DialogueUILogic.Instance;
        bool talking = ui != null && ui.IsTalking && !ui.currentDialogueIsPassive;
        string end = null;
        if (Time.unscaledTime > _talkUntil) end = "time over";
        else if (DialogueOptionsPanel.IsShowing) end = "a choice";
        else if (!talking)
        {
            if (_talkQuietSince < 0) _talkQuietSince = Time.unscaledTime;
            if (Time.unscaledTime - _talkQuietSince > 1.2f) end = "talk over";
        }
        else
        {
            _talkQuietSince = -1;
            try { Run(new[] { "next" }); _talkClicks++; }
            catch (Exception e) { end = "failed: " + e.Message; }
        }
        if (end == null) return;
        _talkUntil = -1;
        Plugin.Logger.LogInfo($"[talkthrough] {end} after {_talkClicks} clicks");
    }

    // Dev: random building and unbuilding near the house, run on several machines at once (the build exit test).
    // Only blocks above the starting index are removed, so the house the run started from stays.
    private static int _fuzzLeft, _fuzzBase;
    private static System.Random _fuzzRng;
    private static float _fuzzNext;
    private static readonly Dictionary<string, int> FuzzResults = new();

    private static void FuzzTick()
    {
        if (_fuzzLeft <= 0 || Time.unscaledTime < _fuzzNext || !Sync.HouseBlocks.Ready) return;
        _fuzzNext = Time.unscaledTime + 0.1f + (float)_fuzzRng.NextDouble() * 0.1f;
        _fuzzLeft--;
        var keys = Sync.HouseBlocks.Capture().Keys.ToList();
        var mine = keys.Where(k => k > _fuzzBase).ToList();
        string result;
        if (mine.Count > 0 && _fuzzRng.NextDouble() < 0.35)
            result = Sync.HouseBlocks.Unbuild(mine[_fuzzRng.Next(mine.Count)]).StartsWith("removed") ? "removed" : "remove failed";
        else
        {
            var r = Sync.HouseBlocks.BuildNextTo(keys[_fuzzRng.Next(keys.Count)]);
            result = r.Contains("placed") ? "placed" : "refused:" + r.Split(';').Last(x => x.Length > 1).Split(':').Last().Trim();
        }
        FuzzResults[result] = FuzzResults.GetValueOrDefault(result) + 1;
        if (_fuzzLeft == 0) Plugin.Logger.LogInfo($"[fuzz] done: {string.Join(", ", FuzzResults.Select(kv => $"{kv.Key} {kv.Value}"))}; {Sync.HouseBlocks.Describe()}");
    }

    private static string Run(string[] a)
    {
        if (DialogueProbe.Run(a) is { } probed) return probed;
        if (a[0] == "rig") return RigDump.Run();
        if (a[0] == "skiptut") return SkipTutorial();
        if (a[0] == "status") return Status();
        if (a[0] == "unblock") return Unblock(a.Length > 1);
        if (a[0] == "hutfix") return HutFix();
        if (a[0] == "inv") return Sync.InventorySync.Describe() + $" (loading={SaveLoadGame.isLoading} worldLoading={SaveLoadGame.worldIsLoading})";
        if (a[0] == "give")
        {
            var m = UnityEngine.Object.FindFirstObjectByType<InventoryManager>();
            var item = m.pRef.IDToType(a[1])?.TryCast<Item>();
            if (item == null) return $"no item {a[1]}";
            m.AddItemsToPlayerInventory(item, int.Parse(a[2]));
            return Sync.InventorySync.Describe();
        }
        if (a[0] == "take")
        {
            var m = UnityEngine.Object.FindFirstObjectByType<InventoryManager>();
            var item = m.pRef.IDToType(a[1])?.TryCast<Item>();
            if (item == null) return $"no item {a[1]}";
            return $"removed={m.RemoveItemsFromPlayerInventory(item, int.Parse(a[2]))}; {Sync.InventorySync.Describe()}";
        }
        if (a[0] == "money")
        {
            var mm = UnityEngine.Object.FindFirstObjectByType<MoneyManager>();
            int n = int.Parse(a[1]);
            if (n >= 0) mm.AddMoney(n); else mm.RemoveMoney(-n);
            return $"money {mm.currentMoney}";
        }
        if (a[0] == "hut") return Sync.HutSync.Describe();
        if (a[0] == "hutcall")
        {
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().transform.position;
            return $"call to {me}: {Sync.HutSync.Hut?.TryCallTo(me)}";
        }
        if (a[0] == "hutgo")
        {
            var hut = Sync.HutSync.Hut;
            var target = hut.transform.position + new Vector3(F(a[1]), 0, F(a[2]));
            return $"hut to {target}: {hut.TryCallTo(target)}";
        }
        if (a[0] == "rider")
        {
            var floor = Sync.HutSync.Floor;
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().transform;
            var other = Sync.PlayerSync.NearestPuppetTransform(me.position);
            return $"floor {floor.position} me-on-floor {floor.InverseTransformPoint(me.position)} aboard={PlayerHutBoardDetector.PlayerAboard} " +
                   $"puppet-on-floor {(other ? floor.InverseTransformPoint(other.position).ToString() : "none")}";
        }
        if (a[0] == "board")
        {
            var floor = Sync.HutSync.Floor;
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            var pos = floor.position + Vector3.up * 0.6f;
            var rb = me.GetComponent<Rigidbody>();
            if (rb != null) rb.position = pos;
            me.transform.position = pos;
            return $"boarded at {pos}";
        }
        if (a[0] == "hutstand" || a[0] == "hutsit")
        {
            bool stand = a[0] == "hutstand";
            if (Sync.HutSync.Mirroring) Sync.HutSync.SendRequest(stand ? Sync.HutSync.Request.Stand : Sync.HutSync.Request.Sit);
            else if (stand) Sync.HutSync.Hut?.StandUp();
            else Sync.HutSync.Hut?.SitDown();
            return Sync.HutSync.Describe();
        }
        if (a[0] == "click") return Click(string.Join(" ", a, 1, a.Length - 1));
        if (a[0] == "buttons")
            return string.Join(" | ", UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsSortMode.None)
                .Where(b => b.interactable).Select(b => $"{b.name}:'{b.GetComponentInChildren<TMPro.TMP_Text>()?.text?.Trim()}'"));
        if (a[0] == "ccdone")
        {
            var cc = UnityEngine.Object.FindFirstObjectByType<CharacterCustomization>();
            if (cc == null) return "no character customization";
            cc.StopCustomization();
            return "stopped customizing";
        }
        if (a[0] == "intro")
        {
            // intro start|end: the new-game intro's pause-screen calls, which the join gate listens to.
            var cm = UnityEngine.Object.FindFirstObjectByType<CanvasManager>();
            if (cm == null) return "no CanvasManager";
            if (a[1] == "start") cm.ForbidPauseScreen_NewGameIntro(); else cm.AllowPauseScreen_NewGameIntro();
            return $"intro {a[1]}; starting journey={Sync.WorldTransfer.StartingJourney}";
        }
        if (a[0] == "unlockall")
        {
            foreach (var item in UseItemManager.Instance.allItems) UseItemManager.Instance.UnlockItem(item.ID);
            return "unlocked all use items";
        }
        if (a[0] == "saves")
        {
            var f = FileIO.InstanceIfExists;
            if (f == null) return "no FileIO";
            var sb = new System.Text.StringBuilder($"status={f.status} root={FileIO.SaveFilesRootPath} folders={f.LoadedFolderCount} singles={f.LoadedSingleSaveGameCount} beyondBuild={f._savesBeyondThisBuild} toLoad={f.SaveGamesToLoad} loaded={f.LoadedSaveGames};");
            for (int i = 0; i < f.LoadedFolderCount; i++) sb.Append($" [{i}] {f.GetLoadedFolderName(i)} x{f.GetSaveGameCountInLoadedFolder(i)}");
            foreach (var d in Directory.GetDirectories(FileIO.SaveFilesRootPath))
                foreach (var slot in Directory.GetDirectories(d)) sb.Append($" {Path.GetFileName(d)}/{Path.GetFileName(slot)} complete={FileIO.HasEverySaveFileItNeeds(slot)}");
            sb.Append($" mostRecent={f.TryGetMostRecentSave(out var ms, out int mf, out int mslot)} {mf}/{mslot}");
            return sb.ToString();
        }
        if (a[0] == "saveables") return Sync.SaveableSync.Describe();
        if (a[0] == "digest") return Sync.DigestSync.Describe();
        if (a[0] == "saveablexml")
        {
            File.WriteAllText(Path.Combine(BepInEx.Paths.GameRootPath, "saveable.xml"), Sync.SaveableSync.Dump(a[1]));
            return "wrote saveable.xml";
        }
        if (a[0] == "fields")
        {
            // fields <Type>[@name part] [invoke <method>]: public properties of the first live object of that game type, or call a no-arg method on it
            var at = a[1].IndexOf('@');
            var type = typeof(MainMenu).Assembly.GetType(at < 0 ? a[1] : a[1][..at]);
            if (type == null) return $"no type {a[1]}";
            var il = Il2CppInterop.Runtime.Il2CppType.From(type);
            UnityEngine.Object[] found = Resources.FindObjectsOfTypeAll(il).ToArray();
            if (at >= 0) found = found.Where(o => o.name.Contains(a[1][(at + 1)..])).ToArray();
            if (found.Length == 0) return "none";
            var obj = System.Activator.CreateInstance(type, found[0].Pointer);
            if (a.Length > 3 && a[2] == "invoke")
            {
                // invoke Method [args]: int, float and bool arguments
                var args = a.Skip(4).ToArray();
                var m = type.GetMethods().FirstOrDefault(x => x.Name == a[3] && x.GetParameters().Length == args.Length);
                if (m == null) return $"no {a[3]} taking {args.Length}";
                var ps = m.GetParameters();
                var vals = new object[args.Length];
                for (int i = 0; i < args.Length; i++) vals[i] = System.Convert.ChangeType(args[i], ps[i].ParameterType, System.Globalization.CultureInfo.InvariantCulture);
                var ret = m.Invoke(obj, vals);
                return $"invoked {a[3]} on {found[0].name} -> {ret}";
            }
            var sb = new System.Text.StringBuilder($"{found.Length} found, {found[0].name}:");
            foreach (var pr in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            {
                try { var v = pr.GetValue(obj); sb.Append($" {pr.Name}={(v is UnityEngine.Object uo && uo != null ? uo.GetIl2CppType().Name + "(" + uo.name + ")" : v)}"); } catch { }
            }
            return sb.ToString();
        }
        if (a[0] == "active") return Sync.ActiveSync.Describe();
        if (a[0] == "dlgstuck") return Sync.DialogueSync.TestStuck();
        if (a[0] == "unblock_move")
        {
            var mv = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            bool was = mv.blockMovement;
            mv.UnblockMovement();
            if (a.Length > 1 && a[1] == "flag") DialogueUILogic.isTalkingOrInQueue = false;
            if (a.Length > 1 && a[1] == "passive") DialogueUILogic.isPassiveDialogueOnScreen = true;
            if (a.Length > 1 && a[1] == "mode")
            {
                var gm = Resources.FindObjectsOfTypeAll<CurrentGameMode>().FirstOrDefault();
                var senders = new List<string>();
                foreach (var r in gm._inGameUIRequests.ToArray()) { senders.Add($"{r?.GetIl2CppType().Name}/{r?.name}"); }
                foreach (var r in gm._inGameUIRequests.ToArray()) if (r != null && r.GetIl2CppType().Name.Contains("Dialogue")) gm.DeRequestInGameUI(r);
                return $"requests were {string.Join(", ", senders)}; mode now {gm.mode}";
            }
            if (a.Length > 1 && a[1] == "camera")UnityEngine.Object.FindFirstObjectByType<DialogueCamera>(FindObjectsInactive.Include).Deactivate();
            return $"blockMovement was {was}, now {mv.blockMovement}; wasInDialogue={mv._wasInDialogue}";
        }
        if (a[0] == "setactive")
        {
            // setactive <exact name> <0|1>: switch the first object with that name (inactive ones included)
            var t = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(x => x.name == a[1]);
            if (t == null) return "none";
            t.gameObject.SetActive(a[2] != "0");
            return $"{TPath(t)} active={t.gameObject.activeSelf}";
        }
        if (a[0] == "roots")
        {
            // Scene roots with their transform counts (inactive included), biggest first.
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                foreach (var go in UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).GetRootGameObjects()
                             .Select(g => (g, n: g.GetComponentsInChildren<Transform>(true).Length)).OrderByDescending(x => x.n).Take(40))
                    sb.Append($"\n{go.n} {(go.g.activeSelf ? "+" : "-")} {go.g.name}");
            return sb.ToString();
        }
        if (a[0] == "where")
        {
            // where <name part>: every object whose name contains it, with position, active and visible renderers.
            var sb = new System.Text.StringBuilder();
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!t.name.Contains(a[1], StringComparison.OrdinalIgnoreCase)) continue;
                var rs = t.GetComponentsInChildren<Renderer>(true);
                sb.Append($"\n{(t.gameObject.activeInHierarchy ? "+" : "-")} {TPath(t)} @{t.position:F1} renderers {rs.Count(r => r.enabled && r.gameObject.activeInHierarchy)}/{rs.Length}");
            }
            File.WriteAllText(Path.Combine(BepInEx.Paths.GameRootPath, "where.txt"), sb.ToString());
            return "wrote where.txt";
        }
        if (a[0] == "statedump")
        {
            // Every ISaveable's state, as the game would save it, one file per section in statedump/ (for diffing machines).
            var walk = new SaveableSceneWalk();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++) walk.PushRoots(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i));
            walk.PushRoots(SaveableSceneWalk.DontDestroyOnLoadScene);
            int steps = 0;
            while (!walk.Step(1000) && steps++ < 100) { }
            walk.FillLists();
            var sg = new SaveGame
            {
                saveableInventory = new SaveGame.SaveableInventory(), saveableProgress = new SaveGame.SaveableProgress(), saveableStats = new SaveGame.SaveableStats(),
                saveablePoiHistory = new SaveGame.SaveablePoiHistory(), saveablePlayerGuidance = new SaveGame.SaveablePlayerGuidance(),
                saveableCollection = new SaveGame.SaveableCollection(), savedBBs = new SaveLoadBBs.SavedBBs(),
            };
            var dir = Path.Combine(BepInEx.Paths.GameRootPath, "statedump");
            Directory.CreateDirectory(dir);
            var types = new List<string>();
            foreach (var s in walk.saveables)
            {
                var mb = s.TryCast<MonoBehaviour>();
                types.Add(mb != null ? $"{mb.GetIl2CppType().Name} {TPath(mb.transform)}" : "?");
                try { s.GetStateForSaving(sg); }
                catch (Exception e) { types[^1] += $" FAILED {e.InnerException?.Message ?? e.Message}"; }
            }
            File.WriteAllLines(Path.Combine(dir, "saveables.txt"), types.OrderBy(t => t));
            void Dump<T>(string name, Il2CppSystem.Object o)
            {
                try { File.WriteAllText(Path.Combine(dir, name + ".xml"), o == null ? "null" : ClassicElementStateBridge.Serialize(Il2CppInterop.Runtime.Il2CppType.Of<T>(), o)); }
                catch (Exception e) { File.WriteAllText(Path.Combine(dir, name + ".xml"), "FAILED " + e); }
            }
            Dump<SaveGame.SaveableProgress>("Progress", sg.saveableProgress);
            Dump<SaveGame.SaveableInventory>("Inventory", sg.saveableInventory);
            Dump<SaveGame.SaveableStats>("Stats", sg.saveableStats);
            Dump<SaveGame.SaveablePoiHistory>("PoiHistory", sg.saveablePoiHistory);
            Dump<SaveGame.SaveablePlayerGuidance>("Guidance", sg.saveablePlayerGuidance);
            Dump<SaveGame.SaveableCollection>("Collection", sg.saveableCollection);
            return $"statedump: {walk.saveables.Count} saveables, {walk.mapElements.Count} map elements";
        }
        if (a[0] == "debugsave")
        {
            if (Cheats.instance == null) return "no Cheats instance";
            Cheats.instance.DebugSave();
            return "DebugSave called";
        }
        if (a[0] == "save")
        {
            var slg = UnityEngine.Object.FindFirstObjectByType<SaveLoadGame>();
            slg.SaveCurrentSaveGame(slg.currentSaveGameSlot, SaveGameType.ManualSave);
            return $"saving to slot {slg.currentSaveGameSlot} folder {slg.currentSaveGameFolder} refused={slg.SavingIsRefusedNow} forbidden={slg.SavingIsForbidden} collecting={SaveLoadGame.IsCollecting}";
        }
        if (a[0] == "near")
        {
            // near x y z r: every object within r of a point, with its components and whether it's active.
            var at = new Vector3(F(a[1]), F(a[2]), F(a[3]));
            float r = F(a[4]);
            var sb = new System.Text.StringBuilder();
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if ((t.position - at).sqrMagnitude > r * r) continue;
                var comps = t.GetComponents<Component>().Select(c => c == null ? "?" : c.GetIl2CppType().Name).Where(n => n != "Transform");
                sb.AppendLine().Append($"{(t.gameObject.activeInHierarchy ? "+" : "-")} {TPath(t)} @{t.position} [{string.Join(",", comps)}]");
            }
            File.WriteAllText(Path.Combine(BepInEx.Paths.GameRootPath, "near.txt"), sb.ToString());
            return "wrote near.txt";
        }
        if (a[0] == "coll")
        {
            File.WriteAllText(Path.Combine(BepInEx.Paths.GameRootPath, "collectibles.txt"), Sync.WorldObjects.Dump());
            return "wrote collectibles.txt";
        }
        if (a[0] == "blueprints") return Sync.BlueprintSync.Describe();
        if (a[0] == "carry") return Sync.CarrySync.Describe();
        if (a[0] == "animals") return Sync.MirrorSync.Describe() + "; " + Sync.AiSync.Describe();
        if (a[0] == "bpmake") return Sync.BlueprintSync.DevMakeSite();
        if (a[0] == "house")
        {
            File.WriteAllText(Path.Combine(BepInEx.Paths.GameRootPath, "house.txt"), Sync.HouseBlocks.Dump());
            return Sync.HouseBlocks.Describe();
        }
        if (a[0] == "buildfuzz")
        {
            _fuzzLeft = int.Parse(a[1]);
            _fuzzRng = new System.Random(int.Parse(a[2]));
            _fuzzBase = a.Length > 3 ? int.Parse(a[3]) : Sync.HouseBlocks.Capture().Keys.Max();
            FuzzResults.Clear();
            return $"fuzzing {_fuzzLeft} edits above block {_fuzzBase}";
        }
        if (a[0] == "unbuildabove")
        {
            int n = 0;
            foreach (var k in Sync.HouseBlocks.Capture().Keys.Where(k => k > int.Parse(a[1])).OrderByDescending(k => k).ToList())
                if (Sync.HouseBlocks.Unbuild(k).StartsWith("removed")) n++;
            return $"removed {n}; {Sync.HouseBlocks.Describe()}";
        }
        if (a[0] == "buildnext") return Sync.HouseBlocks.BuildNextTo(int.Parse(a[1]));
        if (a[0] == "furnish") return Sync.HouseFurniture.PlaceTest(a[1]);
        if (a[0] == "unfurnish")
        {
            foreach (var kv in Sync.HouseFurniture.Capture())
                if (kv.Key.StartsWith(a[1])) { UnityEngine.Object.FindFirstObjectByType<BB_Building>().RemoveBBFromWorld(kv.Value.go); return $"removed {kv.Key}"; }
            return "none";
        }
        if (a[0] == "unbuild") return Sync.HouseBlocks.Unbuild(int.Parse(a[1]));
        if (a[0] == "houseroundtrip") return Sync.HouseBlocks.RoundTrip();
        if (a[0] == "talktest") return Sync.DialogueSync.Test();
        if (a[0] == "trialreward") return Sync.RewardSync.TestTrialReward(int.Parse(a[1]));
        if (a[0] == "bbs")
        {
            var ids = UnityEngine.Object.FindFirstObjectByType<InventoryManager>().pRef;
            var stacks = UnityEngine.Object.FindFirstObjectByType<BB_Building>().playerBBs.AquiredBBs;
            var sb = new System.Text.StringBuilder();
            foreach (var s in stacks) if (s.amountOfInstances > 0 || (a.Length > 1 && ids.TypeToID(s.bb).Contains(a[1]))) sb.Append($"{ids.TypeToID(s.bb)} x{s.amountOfInstances}{(s.hasPlan ? " plan" : "")}; ");
            return sb.ToString();
        }
        if (a[0] == "lootreward") return Sync.RewardSync.TestLootReward();
        if (a[0] == "lootopen")
        {
            // lootopen <path part>: open that loot spot as if the player had (POI_Loot.GiveToPlayer).
            var l = UnityEngine.Object.FindObjectsByType<POI_Loot>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(x => TPath(x.transform).Contains(a[1]));
            if (l == null) return "no such loot";
            return $"{TPath(l.transform)} gave={l.GiveToPlayer()}; {Sync.InventorySync.Describe()}";
        }
        if (a[0] == "loots")
            return string.Join(" | ", UnityEngine.Object.FindObjectsByType<POI_Loot>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(l => $"{TPath(l.transform)} treasure={Sync.RewardSync.IsTreasure(l)}"));
        if (a[0] == "detectors")
        {
            // detectors [radius]: ObjectDetectors near the player, how they detect and what entering calls
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().transform.position;
            float r = a.Length > 1 ? F(a[1]) : 30f;
            var sb = new System.Text.StringBuilder();
            foreach (var d in UnityEngine.Object.FindObjectsByType<ObjectDetector>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                float dist = (d.transform.position - me).magnitude;
                if (dist > r) continue;
                var ev = d.OnEnter;
                var calls = new System.Collections.Generic.List<string>();
                for (int i = 0; ev != null && i < ev.GetPersistentEventCount(); i++) calls.Add($"{ev.GetPersistentTarget(i)?.GetIl2CppType().Name}({ev.GetPersistentTarget(i)?.name}).{ev.GetPersistentMethodName(i)}");
                sb.Append($"\n{dist:0}m {TPath(d.transform)} on={d.isActiveAndEnabled} tag={d.tagString} type={d.detectionType} path={d._path} tracks={d.TracksThePlayer()} once={d.triggerOnlyOnce} entered={d.HasTriggeredEnter} inside={d._inside} los={d.doLOSCheck} moves={d.detectorMoves} cooldown={d.eventCooldown} registered={d._registeredWith != null} puppetIn={Sync.DetectorSync.PuppetInside(d)} enter: {string.Join(", ", calls)}");
            }
            return sb.ToString();
        }
        if (a[0] == "buyables")
            return string.Join(" | ", UnityEngine.Object.FindObjectsByType<BuyableLoot>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(l => $"{TPath(l.transform)} bb={(l.bb != null ? l.bb.name : "-")} cost={l.goldCost} n={l.amountAvailable} on={l.isActiveAndEnabled} kids={l.transform.childCount}"))
                + " || " + string.Join(" | ", UnityEngine.Object.FindObjectsByType<BuyableItem>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(l => $"{TPath(l.transform)} on={l.isActiveAndEnabled}"));
        if (a[0] == "requesters")
            return string.Join(" | ", UnityEngine.Object.FindObjectsByType<ItemRequester>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(r => $"{TPath(r.transform)} item={(r.requestItem != null ? r.requestItem.name : "-")} on={r.isActiveAndEnabled} pos={r.transform.position}"));
        if (a[0] == "birds") return Sync.BirdSync.Describe();
        if (a[0] == "birdtest") return Sync.BirdSync.Test();
        if (a[0] == "shoot")
        {
            var b = Sync.Find.One<UseItem_Birds>();
            if (b == null) return "no birds";
            b.PlaceBirdInHand();
            b.Shoot();
            return $"equipped={b.IsEquipped} available={b.availableBirds?.Count} spawned={b.spawnedBirds?.Count}";
        }
        if (a[0] == "flags") return Sync.StorySync.Describe();
        if (a[0] == "setflag")
        {
            StaticGameCondition.SetCondition(a[1], a.Length < 3 || a[2] != "0");
            return $"{a[1]} = {StaticGameCondition.GetCondition(a[1])}";
        }
        if (a[0] == "popups")
        {
            var sb = new System.Text.StringBuilder();
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                var n = mb.GetIl2CppType().Name;
                if (mb.isActiveAndEnabled && (n.Contains("PopUp") || n.Contains("Popup") || n.Contains("TitleCard") || n.Contains("Title"))) sb.Append($"{n}@{mb.name}; ");
            }
            return sb.ToString();
        }
        if (a[0] == "titlecard")
        {
            UnityEngine.Object.FindFirstObjectByType<TasksTracker>().OnTrialTitleCartDismissed();
            return Blockers();
        }
        if (a[0] == "chestput") return Sync.ContainerSync.PutNearest(a[1], int.Parse(a[2]));
        if (a[0] == "plantstage")
        {
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().transform.position;
            var pg = UnityEngine.Object.FindObjectsByType<PlantGrowth>(FindObjectsSortMode.None).OrderBy(x => (x.transform.position - me).sqrMagnitude).First();
            pg.SetStage(int.Parse(a[1]));
            return $"{pg.name} at {pg.transform.position} now stage {pg.stageIndex}";
        }
        if (a[0] == "feather")
        {
            var fc = UnityEngine.Object.FindFirstObjectByType<FeatherColorController>();
            if (a.Length > 1) fc.SetFeatherColor(int.Parse(a[1]));
            return $"feather {fc.featherIndex}";
        }
        if (a[0] == "scanslow") { var r = $"{Sync.Find.Slowest} {Sync.Find.SlowestMs:0.0} ms"; Sync.Find.SlowestMs = 0; return r; }
        if (a[0] == "hutctl")
        {
            var hut = Sync.HutSync.Hut;
            if (hut == null) return "no hut";
            if (a.Length > 1 && a[1] == "off") hut.StopControlling(); else hut.StartControlling();
            return HutProbe(hut);
        }
        if (a[0] == "hutallow") { Sync.GuestHutMirror.DevAllow.Clear(); for (int i = 1; i < a.Length; i++) Sync.GuestHutMirror.DevAllow.Add(a[i]); return "allowed " + string.Join(",", Sync.GuestHutMirror.DevAllow); }
        if (a[0] == "actor") { if (a.Length > 1) { Sync.ActingPlayer.Trace = a[1] == "off" ? null : a[1]; return "tracing " + a[1]; } return Sync.ActingPlayer.Probe(); }
        if (a[0] == "dlgprobe")
        {
            var ui = DialogueUILogic.Instance;
            if (ui == null) return "no DialogueUILogic";
            return $"talking={ui.IsTalking} passive={ui.currentDialogueIsPassive} lines={ui.currentDialogue?.Length} index={ui.indexInDialogue} gen={ui.DialogueGeneration} options={DialogueOptionsPanel.IsShowing} queue={ui.queue?.Count} delay={ui.delayForPassiveDialogue} {Sync.DialogueSync.Describe()} text={(ui.currentDialogue != null && ui.indexInDialogue >= 0 && ui.indexInDialogue < ui.currentDialogue.Length ? ui.currentDialogue[ui.indexInDialogue] : "")}";
        }
        if (a[0] == "grabobj")
        {
            // grabobj [go]: walk up to the nearest carriable that isn't an animal (wheel, jar, ...) and pick it up.
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            CarriableObject best = null; float bestD = float.MaxValue;
            foreach (var co in UnityEngine.Object.FindObjectsByType<CarriableObject>(FindObjectsSortMode.None))
            {
                if (co.isBeingCarried || co.ai != null || co.GetComponentInParent<GeneralAIController>() != null) continue;
                float d = (co.transform.position - me.transform.position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = co; }
            }
            if (best == null) return "no carriable object";
            var pos = best.transform.position + Vector3.up * 0.5f - best.transform.forward * 1.2f;
            var rb = me.GetComponent<Rigidbody>();
            if (rb != null) rb.position = pos;
            me.transform.position = pos;
            if (a.Length > 1 && a[1] == "go") return $"next to {best.name} at {Mathf.Sqrt(bestD):0} m";
            best.StartCarrying();
            return $"picked {best.name} from {Mathf.Sqrt(bestD):0} m away; carried={best.isBeingCarried} follower={best.objectFollower?.name}";
        }
        if (a[0] == "grabhare")
        {
            // Walk up to the nearest carriable animal and pick it up, as the player would.
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            CarriableObject best = null; float bestD = float.MaxValue;
            foreach (var co in UnityEngine.Object.FindObjectsByType<CarriableObject>(FindObjectsSortMode.None))
            {
                if (co.isBeingCarried || !(co.ai != null || co.name.Contains("Hare") || co.GetComponentInParent<GeneralAIController>() != null)) continue;
                float d = (co.transform.position - me.transform.position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = co; }
            }
            if (best == null) return "no carriable animal";
            var at = best.ai != null ? best.ai.transform : best.transform;
            var pos = at.position + Vector3.up * 0.5f - at.forward * 1.2f;
            var rb = me.GetComponent<Rigidbody>();
            if (rb != null) rb.position = pos;
            me.transform.position = pos;
            if (a.Length > 1 && a[1] == "go") return $"next to {best.name} at {Mathf.Sqrt(bestD):0} m";
            best.StartCarrying();
            return $"picked {best.name} from {Mathf.Sqrt(bestD):0} m away; carried={best.isBeingCarried}";
        }
        if (a[0] == "rigs")
        {
            var root = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().transform.root;
            var sb = new System.Text.StringBuilder();
            foreach (var rig in root.GetComponentsInChildren<UnityEngine.Animations.Rigging.Rig>(true)) sb.Append($"{rig.name}={rig.weight:0.00} ");
            return sb.ToString();
        }
        if (a[0] == "playeranim")
        {
            var anim = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().playerAnimator;
            var sb = new System.Text.StringBuilder();
            for (int l = 0; l < anim.layerCount; l++)
                sb.Append($"L{l} {anim.GetLayerName(l)} w={anim.GetLayerWeight(l):0.00} state={anim.GetCurrentAnimatorStateInfo(l).shortNameHash}; ");
            foreach (var p in anim.parameters)
                if (p.type == AnimatorControllerParameterType.Bool && anim.GetBool(p.nameHash)) sb.Append(p.name + " ");
            return sb.ToString();
        }
        if (a[0] == "carrystate")
        {
            var sb = new System.Text.StringBuilder();
            foreach (var co in UnityEngine.Object.FindObjectsByType<CarriableObject>(FindObjectsSortMode.None))
            {
                if (co.ai == null && !co.isBeingCarried) co.ResolveCarriedAI();
                if (co.ai == null) continue;
                bool remote = Sync.CarrySync.IsCarried(co.ai);
                if (!co.isBeingCarried && !remote) continue;
                var anim = co.ai.GetComponentInChildren<Animator>();
                sb.Append($"{co.name} mine={co.isBeingCarried} remote={remote} ai.enabled={co.ai.enabled} aiCarried={co.ai.IsCarried} ");
                if (anim != null)
                {
                    sb.Append($"anim on={anim.isActiveAndEnabled} state={anim.GetCurrentAnimatorStateInfo(0).shortNameHash} t={anim.GetCurrentAnimatorStateInfo(0).normalizedTime:0.00} params:");
                    foreach (var p in anim.parameters)
                        sb.Append(p.type == AnimatorControllerParameterType.Float ? $" {p.name}={anim.GetFloat(p.nameHash):0.##}" :
                                  p.type == AnimatorControllerParameterType.Bool ? $" {p.name}={anim.GetBool(p.nameHash)}" : "");
                }
                sb.Append("; ");
            }
            return sb.Length == 0 ? "nothing carried" : sb.ToString();
        }
        if (a[0] == "facenpc")
        {
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            NPC_DialogueManager best = null; float bestD = float.MaxValue;
            foreach (var npc in UnityEngine.Object.FindObjectsByType<NPC_DialogueManager>(FindObjectsSortMode.None))
            {
                if (a.Length > 1 && !Sync.GearObjects.PathOf(npc.transform).Contains(a[1])) continue;
                float d = (npc.transform.position - me.transform.position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = npc; }
            }
            if (best == null) return "no villager";
            var camFwd = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up).normalized;
            var pos = best.transform.position - camFwd * 1.8f + Vector3.up * 0.3f;
            var rb = me.GetComponent<Rigidbody>();
            if (rb != null) rb.position = pos;
            me.transform.position = pos;
            me.transform.rotation = Quaternion.LookRotation(camFwd);
            return $"in front of {best.name} ({Sync.GearObjects.PathOf(best.transform)})";
        }
        if (a[0] == "tphut")
        {
            var hut = Sync.HutSync.Hut;
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            // In front of the camera, so the hut is in view.
            var camFwd = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up).normalized;
            var pos = hut.transform.position - camFwd * F(a.Length > 1 ? a[1] : "9") + Vector3.up * 1f;
            var rb = me.GetComponent<Rigidbody>();
            if (rb != null) rb.position = pos;
            me.transform.position = pos;
            me.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(hut.transform.position - pos, Vector3.up));
            return $"moved to {pos}; " + HutProbe(hut) + $" lookAt={hut.lookAtEngaged} prompt={hut._sitPromptVisible}";
        }
        if (a[0] == "gc")
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long before = Il2CppSystem.GC.GetTotalMemory(false);
            if (a.Length > 1) Il2CppSystem.GC.Collect();
            return $"incremental={UnityEngine.Scripting.GarbageCollector.isIncremental} slice={UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds / 1e6:0.0} ms mode={UnityEngine.Scripting.GarbageCollector.GCMode} heap {before >> 20} -> {Il2CppSystem.GC.GetTotalMemory(false) >> 20} MB, took {sw.Elapsed.TotalMilliseconds:0.0} ms";
        }
        if (a[0] == "alloc")
        {
            // alloc start [pause]: from now (with the mod's ticks off on pause); alloc end: the game heap's growth since.
            if (a[1] == "start")
            {
                Sync.PlayerSync.PausedTicks = a.Length > 2 && a[2] == "pause";
                Sync.PlayerSync.SkippedTick = a.Length > 2 && a[2] != "pause" ? a[2] : null;
                _allocAt = (Sync.PlayerSync.HeapGrowth, Time.unscaledTime);
                return $"ticks paused={Sync.PlayerSync.PausedTicks}";
            }
            Sync.PlayerSync.PausedTicks = false;
            Sync.PlayerSync.SkippedTick = null;
            float secs = Time.unscaledTime - _allocAt.at;
            return $"{(Sync.PlayerSync.HeapGrowth - _allocAt.heap) / 1024.0 / secs:0} KB/s over {secs:0.0} s";
        }
        if (a[0] == "flee" && a.Length > 1 && a[1] == "count") return $"startled by guests: {Sync.FleeSync.Startled}, ticks {Sync.FleeSync.Ticks}, guest closer {Sync.FleeSync.Closer}";
        if (a[0] == "flee" && a.Length > 1 && a[1] == "all")
        {
            var groups = new System.Collections.Generic.Dictionary<string, (int n, int live)>();
            foreach (var ai in UnityEngine.Object.FindObjectsByType<GeneralAIController>(FindObjectsSortMode.None))
            {
                var trig = PetEligibility.FleeTriggers(ai);
                if (trig == null || trig.Length == 0) continue;
                var key = $"{ai.name}/{ai.GetComponentInParent<POI_SaveLoad>()?.name}";
                var g = groups.GetValueOrDefault(key);
                groups[key] = (g.n + 1, g.live + (PetEligibility.HasLivePlayerFleeTrigger(ai) ? 1 : 0));
            }
            return string.Join(", ", groups.Select(kv => $"{kv.Key} {kv.Value.live}/{kv.Value.n} live"));
        }
        if (a[0] == "flee")
        {
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().transform.position;
            GeneralAIController best = null; float bestD = float.MaxValue;
            foreach (var ai in UnityEngine.Object.FindObjectsByType<GeneralAIController>(FindObjectsSortMode.None))
            {
                var trig = PetEligibility.FleeTriggers(ai);
                if (trig == null || trig.Length == 0) continue;
                if (a.Length > 4 && Vector3.Distance(ai.transform.position, Sync.PlayerSync.NearestPuppet(ai.transform.position) ?? new Vector3(1e6f, 0, 0)) < F(a[4])) continue;  // away from the other player
                if (a.Length > 3 && !ai.name.Contains(a[3]) && !(ai.transform.parent?.name ?? "").Contains(a[3])) continue;
                if (ai.GetComponentInParent<POI_SaveLoad>() is { } poi && poi.name.Contains("Trapped")) continue;
                float d = Vector3.Distance(ai.transform.position, me);
                if (d < bestD) { bestD = d; best = ai; }
            }
            if (best == null) return "no animal with flee triggers";
            var pdm = UnityEngine.Object.FindFirstObjectByType<ProximityDetectorManager>();
            var sb = new System.Text.StringBuilder($"{best.name} under {best.transform.parent?.name}/{best.GetComponentInParent<POI_SaveLoad>()?.name} at {best.transform.position}, {bestD:0} m, fleeing={PetEligibility.IsFleeing(best)}, ai on={best.enabled}, manager player={pdm?._playerT?.name}@{pdm?._playerT?.position} stride={pdm?.frameStride} n={pdm?._tickables?.Count}:");
            foreach (var d in PetEligibility.FleeTriggers(best))
                sb.Append($" [{d.name} type={d.detectionType} tag={d.tagString} path={d._path} probe={d.probeRadius} col={d.col?.GetType().Name}/{(d.col?.TryCast<SphereCollider>() is { } sc ? sc.radius * d.transform.lossyScale.x : -1):0.0} inside={d._inside} active={d.isActiveAndEnabled} objects={d.objects?.Count} tracks={d.TracksThePlayer()} enter={d.OnEnter?.GetPersistentEventCount()}:{(d.OnEnter != null && d.OnEnter.GetPersistentEventCount() > 0 ? d.OnEnter.GetPersistentMethodName(0) + "@" + d.OnEnter.GetPersistentTarget(0)?.name : "")}]");
            if (a.Length > 1 && a[1] == "tp") { var mv = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>(); var p = best.transform.position + (me - best.transform.position).normalized * F(a.Length > 2 ? a[2] : "30") + Vector3.up; var rb = mv.GetComponent<Rigidbody>(); if (rb != null) rb.position = p; mv.transform.position = p; sb.Append($" -> moved to {p}"); }
            return sb.ToString();
        }
        if (a[0] == "backport")
        {
            var bp = UnityEngine.Object.FindFirstObjectByType<UseItem_Backport>(FindObjectsInactive.Include);
            if (bp == null) return "no backport item";
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            var target = bp.GetTeleportPosition();
            string Probe() => $"me={me.transform.position} target={(target == null ? "null" : target.position.ToString())} hut={Sync.HutSync.Hut?.transform.position} porting={bp.porting} active={bp.isActiveAndEnabled} override={bp.teleportToOverwrite} hold={bp.timeToHold} wait={bp.timeToWaitBeforePorting} cd={bp.betweenPortTimer}/{bp.cdBetweenPorts} buildup={bp.teleportBuildUp} H={Rewired.ReInput.players.GetPlayer(0).GetButton("Backport")}";
            if (a.Length > 1 && a[1] == "go") { var before = Probe(); bp.ActualPort(); return before + " -> " + Probe(); }
            if (a.Length > 1 && a[1] == "port") { bp.Port(); return "porting the way the held key does"; }
            return Probe();
        }
        if (a[0] == "hutmode") { var hut = Sync.HutSync.Hut; hut.SetControlMode(System.Enum.Parse<ChickenLegsBehaviour.ControlMode>(a[1])); return HutProbe(hut); }
        if (a[0] == "hutengage") { var hut = Sync.HutSync.Hut; hut.EngageLookAt(); return HutProbe(hut) + $" engaged={hut.lookAtEngaged} before={hut.modeBeforeLookAt}"; }
        if (a[0] == "actions")
        {
            var names = new System.Collections.Generic.List<string>();
            var pl = Rewired.ReInput.players.GetPlayer(0);
            for (int i = 0; i < 300; i++)
                if (Rewired.ReInput.mapping.GetAction(i) is { } act)
                {
                    var map = pl.controllers.maps.GetFirstElementMapWithAction(act.name, true);
                    names.Add($"{act.id}:{act.name}={map?.elementIdentifierName}{(pl.GetButton(act.id) ? "*" : "")}");
                }
            return string.Join(" ", names);
        }
        if (a[0] == "hutlook")
        {
            var hut = Sync.HutSync.Hut;
            Sync.GuestHutMirror.DevAllow.Add("TickLookAtTakeover");
            hut.TickLookAtTakeover();
            return HutProbe(hut) + $" prompt={hut._sitPromptVisible} interaction={hut.lookAtInteractionPrompt?.name} active={hut.lookAtInteractionPrompt?.gameObject.activeInHierarchy}";
        }
        if (a[0] == "seat")
        {
            var seat = UnityEngine.Object.FindFirstObjectByType<HutControlPlayerSittingPlace>(FindObjectsInactive.Include);
            if (seat == null) return "no control seat";
            if (a.Length > 1 && a[1] == "events")
            {
                string Ev(UnityEngine.Events.UnityEventBase e)
                {
                    if (e == null) return "null";
                    var l = new System.Collections.Generic.List<string>();
                    for (int i = 0; i < e.GetPersistentEventCount(); i++) l.Add($"{e.GetPersistentTarget(i)?.name}.{e.GetPersistentMethodName(i)}");
                    return string.Join(", ", l);
                }
                var hut = Sync.HutSync.Hut;
                return $"OnSit [{Ev(seat.OnSit)}] OnStopSit [{Ev(seat.OnStopSit)}] interact={seat.interact?.name} hut.OnStartControlling [{Ev(hut.OnStartControlling)}] hut.OnStopControlling [{Ev(hut.OnStopControlling)}]";
            }
            if (a.Length > 1 && a[1] == "off") seat.StopSitting(); else seat.StartSitting();
            return $"seat {Sync.GearObjects.PathOf(seat.transform)} active={seat.isActiveAndEnabled} occupied={seat.isOccupied}; " + HutProbe(Sync.HutSync.Hut);
        }
        if (a[0] == "rider")
        {
            var hut = Sync.HutSync.Hut;
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().transform.position;
            return $"hut {hut.transform.position} local {hut.transform.InverseTransformPoint(me)} aboard={PlayerHutBoardDetector.PlayerAboard} moving={hut.HutIsMoving} speed={hut.currentSpeed:0.00} mode={hut.CurrentControlMode}";
        }
        if (a[0] == "mestate")
        {
            var mv = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            var rb = mv.GetComponent<Rigidbody>();
            var carry = mv.GetComponent<PlayerPlatformCarry>() ?? mv.GetComponentInChildren<PlayerPlatformCarry>();
            var hut = Sync.HutSync.Hut;
            return $"pos={mv.transform.position} groundMask={mv.ground.value} aboardHut={mv._aboardHut} insideHouse={mv._insideHouse} block={mv.blockMovement} grounded={mv.isGrounded} move on={mv.enabled} rb kin={rb?.isKinematic} grav={rb?.useGravity} vel={rb?.velocity} carry={carry != null}/{carry?.enabled} ground={carry?.Ground?.name} rides={hut?.PlayerRidesPlatform(mv.transform)} aboard={PlayerHutBoardDetector.PlayerAboard} hutmode={hut?.CurrentControlMode} controlled={hut?.isBeingControlled} parent={mv.transform.parent?.name} layer={LayerMask.LayerToName(mv.gameObject.layer)} cols={string.Join(",", mv.GetComponentsInChildren<Collider>().Select(c => $"{c.name}:{LayerMask.LayerToName(c.gameObject.layer)}:{c.enabled}"))} ignores={string.Join(",", Enumerable.Range(0, 32).Where(l => LayerMask.LayerToName(l) != "" && Physics.GetIgnoreLayerCollision(mv.gameObject.layer, l)).Select(LayerMask.LayerToName))}";
        }
        if (a[0] == "hutflag") { var hut = Sync.HutSync.Hut; hut.isBeingControlled = a[1] == "1"; return HutProbe(hut); }
        if (a[0] == "hutcols")
        {
            // First call records the hut's colliders; the next lists what changed since.
            var now = new Dictionary<string, string>();
            foreach (var c in BB_Building.Home.GetComponentsInChildren<Collider>(true))
                now[Sync.GearObjects.PathOf(c.transform) + ":" + c.GetIl2CppType().Name] = $"on={c.enabled}/{c.gameObject.activeInHierarchy} b={c.bounds.center.y:0.0}/{c.bounds.size.y:0.0} layer={LayerMask.LayerToName(c.gameObject.layer)} trig={c.isTrigger}";
            var phys = $"player layer collides with Default? {!Physics.GetIgnoreLayerCollision(UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().gameObject.layer, 0)}";
            if (_hutCols == null || a.Length > 1) { _hutCols = now; return $"recorded {now.Count}; {phys}"; }
            var diff = now.Where(kv => !_hutCols.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => $"{kv.Key} {(_hutCols.TryGetValue(kv.Key, out var v) ? v : "new")} -> {kv.Value}").Take(30).ToList();
            return $"{diff.Count} changed; {phys}: " + string.Join(" | ", diff);
        }
        if (a[0] == "steermode") { Sync.HutSync.SteerMode = System.Enum.Parse<ChickenLegsBehaviour.ControlMode>(a[1]); return $"{Sync.HutSync.SteerMode}"; }
        if (a[0] == "rideable")
        {
            var mv = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            var carry = mv.GetComponent<PlayerPlatformCarry>() ?? mv.GetComponentInChildren<PlayerPlatformCarry>();
            if (carry.Ground != null) _lastGround = carry.Ground;
            var board = UnityEngine.Object.FindFirstObjectByType<PlayerHutBoardDetector>();
            return $"ground={(_lastGround == null ? "" : Sync.GearObjects.PathOf(_lastGround.transform))} on={_lastGround?.enabled}/{_lastGround?.gameObject.activeInHierarchy} layer={(_lastGround == null ? "" : LayerMask.LayerToName(_lastGround.gameObject.layer))} b={_lastGround?.bounds.center} rideable={(_lastGround != null && carry.IsRideable(_lastGround))} hutGround={(_lastGround != null && board.IsHutGround(_lastGround))} carriable={(_lastGround != null && PlayerHutBoardDetector.IsCarriable(_lastGround))} walkableBelow={mv.WalkableGroundBelow(0.5f)} current={carry.CurrentGround()?.name}";
        }
        if (a[0] == "hutprobe") return Sync.HutSync.Hut is { } h ? HutProbe(h) : "no hut";
        if (a[0] == "npcs") return Sync.AiSync.DescribeNear(a.Length > 1 ? int.Parse(a[1]) : 12, a.Length > 3 ? new Vector3(F(a[2]), 0, F(a[3])) : null);
        if (a[0] == "poiparts") return Sync.PoiSync.PartsTest(a.Length > 1 ? a[1] : null);
        if (a[0] == "poiroundtrip") return Sync.PoiSync.RoundTrip();
        if (a[0] == "pois")
        {
            var sb = new System.Text.StringBuilder();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = 0; long chars = 0;
            foreach (var poi in UnityEngine.Object.FindObjectsByType<POI_SaveLoad>(FindObjectsSortMode.None))
            {
                var map = new SaveableMap();
                var one = System.Diagnostics.Stopwatch.StartNew();
                string xml;
                try { poi.ISaveableMapElement_GetStateForSaving(map); xml = ClassicElementStateBridge.Serialize(Il2CppInterop.Runtime.Il2CppType.Of<SaveableMap>(), map); }
                catch (System.Exception e) { xml = "ERR " + (e.InnerException?.Message ?? e.Message); }
                n++; chars += xml.Length;
                sb.AppendLine($"== {poi.uniqueID} {poi.name} inited={poi.isInited} {xml.Length} {one.Elapsed.TotalMilliseconds:0.0}ms");
                if (a.Length > 1 && (poi.uniqueID + poi.name).Contains(a[1])) sb.AppendLine(xml);
            }
            File.WriteAllText(Path.Combine(BepInEx.Paths.GameRootPath, "pois.txt"), sb.ToString());
            return $"{n} POIs, {chars} chars, {sw.ElapsedMilliseconds} ms";
        }
        if (a[0] == "plants")
        {
            var sb = new System.Text.StringBuilder();
            foreach (var pg in UnityEngine.Object.FindObjectsByType<PlantGrowth>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var t = pg.transform; var path = t.name;
                while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
                sb.AppendLine($"{pg.isPlayerOwnedPlant} {pg.IsBB} {path}");
            }
            File.WriteAllText(Path.Combine(BepInEx.Paths.GameRootPath, "plants.txt"), sb.ToString());
            return "wrote plants.txt; home " + BB_Building.Home?.name;
        }
        if (a[0] == "stations")
        {
            File.WriteAllText(Path.Combine(BepInEx.Paths.GameRootPath, "stations.txt"), a.Length > 1 ? Sync.StationSync.Dump(a[1]) : Sync.StationSync.DescribeAll());
            return "wrote stations.txt";
        }
        if (a[0] == "trials") return Sync.TrialSync.Describe();
        if (a[0] == "vfxstate")
        {
            // vfxstate <name part>: the effects with that name, and whether they're running
            var sb = new System.Text.StringBuilder();
            foreach (var v in Resources.FindObjectsOfTypeAll<UnityEngine.VFX.VisualEffect>())
            {
                if (v == null || !v.gameObject.scene.IsValid() || !v.name.Contains(a[1])) continue;
                // vfxstate <name part> reinit|play|stop|event <e>: on puppets' copies only
                if (a.Length > 2 && TPath(v.transform).StartsWith("RekaCoop_Puppet"))
                {
                    if (a[2] == "reinit") v.Reinit();
                    else if (a[2] == "play") v.Play();
                    else if (a[2] == "stop") v.Stop();
                    else if (a[2] == "reasset") { var asset = v.visualEffectAsset; v.visualEffectAsset = null; v.visualEffectAsset = asset; v.Play(); }
                    else if (a[2] == "toggle") { v.gameObject.SetActive(false); v.gameObject.SetActive(true); v.Play(); }
                    else if (a[2] == "event" && a.Length > 3) v.SendEvent(a[3]);
                    v.Simulate(0.02f, 1);
                }
                sb.Append($"\n{TPath(v.transform)} on={v.isActiveAndEnabled} alive={v.aliveParticleCount} awake={v.HasAnySystemAwake()} rate={v.playRate} pause={v.pause} culled={v.culled} asset={(v.visualEffectAsset == null ? "none" : v.visualEffectAsset.name)} renderer={v.GetComponent<UnityEngine.VFX.VFXRenderer>()?.enabled} layer={v.gameObject.layer} scale={v.transform.lossyScale}");
                if (v.visualEffectAsset == null) continue;
                var props = new Il2CppSystem.Collections.Generic.List<UnityEngine.VFX.VFXExposedProperty>();
                v.visualEffectAsset.GetExposedProperties(props);
                foreach (var pr in props)
                {
                    var n = pr.name;
                    if (pr.type == Il2CppInterop.Runtime.Il2CppType.Of<float>()) sb.Append($" {n}={v.GetFloat(n):0.###}");
                    else if (pr.type == Il2CppInterop.Runtime.Il2CppType.Of<bool>()) sb.Append($" {n}={v.GetBool(n)}");
                    else if (pr.type == Il2CppInterop.Runtime.Il2CppType.Of<int>()) sb.Append($" {n}={v.GetInt(n)}");
                    else sb.Append($" {n}:{pr.type?.Name}");
                }
            }
            return sb.ToString();
        }
        if (a[0] == "popuptest")
        {
            // popuptest chapter|unlock <use item id>|message: the game's own popups, to check they reach guests
            if (a[1] == "chapter") { Resources.FindObjectsOfTypeAll<PopUp_Chapter>().First(x => x.gameObject.scene.IsValid()).QueuePopUp(default, "Test Chapter", true, null); return "chapter queued"; }
            if (a[1] == "unlock") { Resources.FindObjectsOfTypeAll<PopUp_UseItemUnlock>().First(x => x.gameObject.scene.IsValid()).QueuePopUp((UseItemManager.Instance.GetItemByID(a[2]) ?? UseItemManager.Instance.GetAvailableItems()[UseItemManager.Instance.GetAvailableItems().Count - 1]).buttonInfo, new UnityEngine.Events.UnityEvent(), false); return "unlock queued"; }
            var labels = new Il2CppSystem.Collections.Generic.List<string>(); labels.Add("Continue"); labels.Add("Highlight");
            var calls = new Il2CppSystem.Collections.Generic.List<Il2CppSystem.Action>(); calls.Add(null); calls.Add(null);
            Resources.FindObjectsOfTypeAll<PopUp_Generic>().First(x => x.gameObject.scene.IsValid()).QueuePopUp_Generic("Co-op test: something has been added to your map.", labels, calls);
            return "message queued";
        }
        // map [id 0|1]: compass elements highlighted here, or highlight one the way the game does
        if (a[0] == "patchaddrs")
        {
            // patchaddrs: every game method the mod patches with its address in GameAssembly.dll, to patchaddrs.txt.
            // Methods that share an address (the compiler folds identical bodies) are marked: a patch on one is a patch on all.
            long gameBase = 0;
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
                if (m.ModuleName == "GameAssembly.dll") gameBase = m.BaseAddress.ToInt64();
            var rows = new List<(long rva, string name)>();
            foreach (var m in HarmonyLib.Harmony.GetAllPatchedMethods())
            {
                try
                {
                    var f = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m);
                    if (f == null) continue;
                    var info = (IntPtr)f.GetValue(null);
                    if (info == IntPtr.Zero) continue;
                    long ptr = System.Runtime.InteropServices.Marshal.ReadIntPtr(info).ToInt64();
                    rows.Add((ptr - gameBase, $"{m.DeclaringType?.FullName}.{m.Name}"));
                }
                catch (Exception e) { rows.Add((-1, $"{m.DeclaringType?.FullName}.{m.Name}: {e.Message}")); }
            }
            rows.Sort();
            var shared = rows.GroupBy(r => r.rva).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            File.WriteAllLines(Path.Combine(BepInEx.Paths.GameRootPath, "patchaddrs.txt"),
                rows.Select(r => $"{r.rva:x8} {(shared.Contains(r.rva) ? "SHARED " : "")}{r.name}"));
            return $"{rows.Count} patched methods, {shared.Count} shared addresses; wrote patchaddrs.txt";
        }
        if (a[0] == "gates")
            return string.Join(" | ", UnityEngine.Object.FindObjectsByType<Tutorial_FenceGateLock>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(g => $"{Sync.ScenePath.Of(g.transform)} active={g.isActiveAndEnabled} completed={g.isCompleted} pending={g.openPending} " +
                             (g.gate == null ? "no hinge" : $"open={g.gate._isOpen} locked={g.gate.isLocked} progress={g.gate.currentProgressInSeconds:0.00}")));
        if (a[0] == "map") { if (a.Length > 2) CompassLogic.SetHighlight(string.Join(" ", a.Skip(1).Take(a.Length - 2)), a[^1] == "1"); return Sync.MapSync.Describe(); }
        if (a[0] == "tutorial") return Sync.TutorialSync.Describe();
        if (a[0] == "pieces") return TutorialPieceLog.Describe();
        if (a[0] == "journal")
        {
            // what the journal key, a tab and the close key do
            var jm = UnityEngine.Object.FindObjectsByType<JournalManager>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            string what = a.Length > 1 ? a[1] : "";
            if (what == "open") GameEvents.UI.Interactables.PeekInventory.TryToOpen?.Invoke();
            else if (what == "openui") GameEvents.UI.Journal.OpenJournalUi?.Invoke();
            else if (what == "closeui" && jm != null) jm.CloseJournal();
            else if (what == "close") GameEvents.UI.Journal.CloseJournalUi?.Invoke();
            else if (what == "page" && jm != null) jm.JumpToPage(System.Enum.Parse<JournalPage>(a[2]));
            else if (what == "next" && jm != null) jm.OnRequest_NextTab();
            return $"journal {what}: manager {(jm == null ? "none" : jm.isActiveAndEnabled ? "on" : "off")}";
        }
        if (a[0] == "areas") { float rr = a.Length > 1 ? F(a[1]) : 15f; var me = Sync.PlayerSync.LocalFeet ?? Vector3.zero; return string.Join("\n", UnityEngine.Object.FindObjectsByType<PlayerTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None).Where(t => (t.transform.position - me).magnitude <= rr).Select(t => $"{(t.transform.position - me).magnitude:0}m {(t.isActiveAndEnabled ? "+" : "-")} {Sync.ScenePath.Of(t.transform)} {Sync.AreaSync.Describe(t)}")); }
        if (a[0] == "skiptask") { UnityEngine.Object.FindFirstObjectByType<TasksTracker>().SkipNextTask(); return Sync.TrialSync.Describe(); }
        if (a[0] == "skiptrial") { UnityEngine.Object.FindFirstObjectByType<TasksTracker>().SkipCurrentTrial(); return Sync.TrialSync.Describe(); }
        if (a[0] == "chests")
        {
            File.WriteAllText(Path.Combine(BepInEx.Paths.GameRootPath, "chests.txt"), Sync.ContainerSync.DescribeAll());
            return "wrote chests.txt";
        }
        if (a[0] == "leshy" && a.Length > 1)   // leshy all: every place that can earn a favour, with its kind
            return string.Join("; ", Resources.FindObjectsOfTypeAll<LeshyApprovalHelper>().Where(h => h.gameObject.scene.IsValid()).Select(h => $"{h.taskType} {(h.gameObject.activeInHierarchy ? "+" : "-")} {Sync.ScenePath.Of(h.transform)} @{h.transform.position}"));
        if (a[0] == "leshy")
            return $"favours={LeshyApproval.GetPoisCompletedInLeshysFavour()} hares={LeshyApproval.freedHares} deer={LeshyApproval.charmedDeer} lutki={LeshyApproval.helpedLutki} stuckLutki={LeshyApproval.helpedStuckLutki} offering={LeshyApproval.madeOffering} highStand={LeshyApproval.destroyedHighStand} mara={LeshyApproval.helpedMara} felled={LeshyApproval.treesFelled} reforested={LeshyApproval.treesReforested} forbidden={LeshyApproval.treeFellForbidden}";
        if (a[0] == "counts")   // counts [part of id]: the game's synced counters (quests that want N of something), and who counts them
        {
            var sb = new System.Text.StringBuilder("synced:");
            var d = CountUpAndDoSth_StaticallySynced.syncedCounts;
            if (d != null) foreach (var kv in d) if (a.Length < 2 || kv.Key.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) >= 0) sb.Append($" {kv.Key}={kv.Value}");
            foreach (var c in Resources.FindObjectsOfTypeAll<CountUpAndDoSth_StaticallySynced>())
            {
                if (!c.gameObject.scene.IsValid() || (a.Length > 1 && (c.synchronizationId ?? "").IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0)) continue;
                sb.Append($"; {c.synchronizationId} count={c.count}/{c.maxCount} now={c.GetCurrentValue()} {(c.gameObject.activeInHierarchy ? "+" : "-")}{c.transform.root.name}/{c.name}");
            }
            return sb.ToString();
        }
        if (a[0] == "items")
        {
            // items [part of id]: item types the game knows, with what kind of food each is
            var m = UnityEngine.Object.FindFirstObjectByType<InventoryManager>();
            var sb = new System.Text.StringBuilder();
            foreach (var p in m.pRef.possessionIDs)
            {
                if (p == null || p.ID == null || (a.Length > 1 && p.ID.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0)) continue;
                var item = p.possession?.TryCast<Item>();
                if (item == null) continue;
                sb.Append($"{p.ID} food={item.foodTag}/{item.foodValue:0.#} cat={item.category}; ");
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }
        if (a[0] == "travelui")
        {
            // travelui | food <n> | pick <slot> | confirm | dest <n> | go | close : the hut's travel screen, step by step
            var logic = Resources.FindObjectsOfTypeAll<BB_Logic_TravelStation>().FirstOrDefault(x => x.gameObject.scene.IsValid());
            var ui = Resources.FindObjectsOfTypeAll<UI_TravelStation>().FirstOrDefault(x => x.gameObject.scene.IsValid());
            var food = Resources.FindObjectsOfTypeAll<FoodSelectionUI>().FirstOrDefault(x => x.gameObject.scene.IsValid());
            if (logic == null || ui == null || food == null) return "no travel screen";
            string did = "";
            if (a.Length > 1)
            {
                if (a[1] == "food") { food.OnSlotClicked(food.foodToDisplay[int.Parse(a[2])]._type); did = "food clicked; "; }
                else if (a[1] == "pick") { food.OnSelectionClicked(int.Parse(a[2])); did = "selection clicked; "; }
                else if (a[1] == "confirm") { food.TryConfirmSelection(); did = "confirm tried; "; }
                else if (a[1] == "dest") { logic.SetDestination(logic.availableDestinations[int.Parse(a[2])]); did = "destination set; "; }
                else if (a[1] == "go") { ui.TravelOnwards(); did = "travel onwards; "; }
                else if (a[1] == "exec") { logic.ExecuteTravel(); did = "executed; "; }
                else if (a[1] == "open") { ui.OnOpen(); did = "opened; "; }
                else if (a[1] == "close") { ui.CloseTravelScreen(); did = "closed; "; }
            }
            var sb = new System.Text.StringBuilder(did);
            sb.Append($"open={ui.gameObject.activeInHierarchy} foodScreen={food.display != null && food.display.gameObject.activeInHierarchy} selected={ui.indexOfSelected} need={food.foodsToSelect}; destinations:");
            var dests = logic.availableDestinations;
            for (int i = 0; dests != null && i < dests.Count; i++) sb.Append($" [{i}] {DescribeDest(dests[i])}");
            sb.Append("; food:");
            var foods = food.foodToDisplay;
            for (int i = 0; foods != null && i < foods.Count; i++) sb.Append($" [{i}] {foods[i]?._type?.name} x{foods[i]?._amount}");
            sb.Append("; chosen:");
            var sel = food.selections;
            for (int i = 0; sel != null && i < sel.Length; i++) sb.Append($" {(sel[i] == null ? "-" : sel[i].name)}");
            return sb.ToString();
        }
        if (a[0] == "travel")
        {
            UnityEngine.Object.FindFirstObjectByType<MapChanger>().JumpToNewMap();
            return "travelling to a new map";
        }
        if (a[0] == "listeners")
        {
            // listeners <method part>: scene UnityEvents (direct fields of components) whose saved calls name that method
            var asm = typeof(MainMenu).Assembly;
            var props = new Dictionary<string, System.Reflection.PropertyInfo[]>();
            var sb = new System.Text.StringBuilder();
            int seen = 0;
            foreach (var c in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (c == null || !c.gameObject.scene.IsValid()) continue;
                var tn = c.GetIl2CppType().FullName;
                if (!props.TryGetValue(tn, out var ps))
                {
                    var mt = asm.GetType(tn.Replace('/', '+'));
                    ps = mt == null ? System.Array.Empty<System.Reflection.PropertyInfo>() : mt.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .Where(pr => typeof(UnityEngine.Events.UnityEventBase).IsAssignableFrom(pr.PropertyType) && pr.GetIndexParameters().Length == 0).ToArray();
                    props[tn] = ps;
                }
                if (ps.Length == 0) continue;
                seen++;
                var obj = System.Activator.CreateInstance(ps[0].DeclaringType == null ? null : asm.GetType(tn.Replace('/', '+')), c.Pointer);
                foreach (var pr in ps)
                {
                    UnityEngine.Events.UnityEventBase ev;
                    try { ev = pr.GetValue(obj) as UnityEngine.Events.UnityEventBase; } catch { continue; }
                    if (ev == null) continue;
                    for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                        if (a[1] == "*" || (ev.GetPersistentMethodName(i) ?? "").Contains(a[1]))
                        {
                            var t = ev.GetPersistentTarget(i);
                            // optional third word: only calls whose target's or owner's name contains it
                            if (a.Length > 2 && !(t != null && t.name.Contains(a[2])) && !c.name.Contains(a[2])) continue;
                            sb.Append($"\n{Sync.ScenePath.Of(c.transform)} {c.GetIl2CppType().Name}.{pr.Name} -> {(t != null ? t.GetIl2CppType().Name + "(" + t.name + ")" : "null")}.{ev.GetPersistentMethodName(i)}");
                        }
                }
            }
            return $"{seen} components with events" + sb;
        }
        if (a[0] == "comps")
        {
            // comps <name part>: components on the nearest object with that name, and its children
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().transform.position;
            Transform best = null; float bestD = float.MaxValue;
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
                if (t.name.Contains(a[1]) && (t.position - me).sqrMagnitude < bestD) { bestD = (t.position - me).sqrMagnitude; best = t; }
            if (best == null) return "none";
            var sb = new System.Text.StringBuilder();
            void Walk(Transform t, string pad, int depth)
            {
                var cs = t.GetComponents<Component>().Select(c => c.GetIl2CppType().Name).Where(n => n != "Transform");
                sb.Append($"\n{pad}{t.name}{(t.gameObject.activeSelf ? "" : " (off)")}: {string.Join(",", cs)}");
                if (depth < 4) for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), pad + " ", depth + 1);
            }
            Walk(best, "", 0);
            return sb.ToString();
        }
        if (a[0] == "detections")
        {
            // detections [radius]: interaction triggers nearby and what interacting with them calls
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().transform.position;
            float r = a.Length > 1 ? F(a[1]) : 30f;
            var sb = new System.Text.StringBuilder();
            foreach (var pd in UnityEngine.Object.FindObjectsByType<PlayerDetection>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if ((pd.transform.position - me).magnitude > r) continue;
                sb.Append($"\n{(pd.transform.position - me).magnitude:0}m {pd.transform.parent?.name}/{pd.name} on={pd.isActiveAndEnabled}:");
                var ev = pd.OnTryToInteract;
                if (ev != null)
                    for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                    {
                        var t = ev.GetPersistentTarget(i);
                        sb.Append($" {(t != null ? t.GetIl2CppType().Name + "(" + t.name + ")" : "null")}.{ev.GetPersistentMethodName(i)}");
                    }
            }
            return sb.ToString();
        }
        if (a[0] == "interact")
        {
            // interact [name part] [all]: use the nearest interaction trigger, as pressing the interact key would
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().transform.position;
            PlayerDetection best = null; float bestD = float.MaxValue;
            foreach (var pd in UnityEngine.Object.FindObjectsByType<PlayerDetection>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (a.Length > 1 && !(pd.transform.parent?.name ?? "").Contains(a[1]) && !pd.name.Contains(a[1])) continue;
                if (!pd.isActiveAndEnabled && !(a.Length > 2 && a[2] == "all")) continue;
                float d = (pd.transform.position - me).sqrMagnitude;
                if (d < bestD) { bestD = d; best = pd; }
            }
            if (best == null) return "nothing to interact with";
            best.SimulateInteraction();
            return $"interacted with {best.transform.parent?.name}/{best.name} at {Mathf.Sqrt(bestD):0} m";
        }
        if (a[0] == "talkers")
        {
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().transform.position;
            var sb = new System.Text.StringBuilder();
            foreach (var npc in UnityEngine.Object.FindObjectsByType<NPC_DialogueManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.Append($"\n{(npc.transform.position - me).magnitude:0}m on={npc.isActiveAndEnabled} options={npc.HasAnyDialogueOptions} silent={npc.IsSilentToPlayer} cant={npc.cantTalk} {Sync.GearObjects.PathOf(npc.transform)}");
            foreach (var sd in UnityEngine.Object.FindObjectsByType<StandaloneDialogue>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.Append($"\n{(sd.transform.position - me).magnitude:0}m standalone on={sd.isActiveAndEnabled} passive={sd.isPassiveDialogue} done={sd.hasBeenActivated} once={sd.onlyActivateOnce} {Sync.GearObjects.PathOf(sd.transform)}");
            return sb.ToString();
        }
        if (a[0] == "say")
        {
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().transform.position;
            NPC_DialogueManager best = null; float bestD = float.MaxValue;
            foreach (var npc in UnityEngine.Object.FindObjectsByType<NPC_DialogueManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (a.Length > 1 && !Sync.GearObjects.PathOf(npc.transform).Contains(a[1])) continue;
                float d = (npc.transform.position - me).sqrMagnitude;
                if (d < bestD) { bestD = d; best = npc; }
            }
            if (best == null) return "nobody";
            // As the key would: the host's own press makes the talk the host's, whoever stands in the villager's detector.
            if (CoopDriver.Session is { Role: Net.Role.Host, IsConnected: true }) Sync.DialogueSync.NoteRequest(Sync.DialogueSync.Me, 10f, act: true);
            best.SaySomething();
            return $"{best.name} says something ({Mathf.Sqrt(bestD):0} m)";
        }
        if (a[0] == "closeoptions") { UnityEngine.Object.FindFirstObjectByType<DialogueOptionsPanel>(FindObjectsInactive.Include).Close(); return "options closed"; }
        if (a[0] == "takeitem")
        {
            // takeitem <path part>: hand in to that place, as its trigger would (berries to the wood creatures, ...)
            foreach (var f in UnityEngine.Object.FindObjectsByType<GenericPOIFunctions>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (Sync.GearObjects.PathOf(f.transform).Contains(a[1])) { f.TakeItemFromPlayer(); return $"handed in at {Sync.GearObjects.PathOf(f.transform)}"; }
            return "none";
        }
        if (a[0] == "standalone")
        {
            // standalone <path part>: start that story dialogue, as its trigger would
            foreach (var sd in UnityEngine.Object.FindObjectsByType<StandaloneDialogue>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (Sync.GearObjects.PathOf(sd.transform).Contains(a[1])) { sd.StartDialogue(); return $"started {Sync.GearObjects.PathOf(sd.transform)}"; }
            return "none";
        }
        if (a[0] == "talkthrough")
        {
            if (a.Length > 1 && a[1] == "off") { _talkUntil = -1; return "off"; }
            _talkUntil = Time.unscaledTime + (a.Length > 1 ? F(a[1]) : 90f);
            _talkClicks = 0; _talkQuietSince = -1; _talkNext = 0;
            return "clicking on";
        }
        if (a[0] == "next")
        {
            // next: continue as the player would; on the host, the dialogue box's own pending step
            if (!Sync.WorldSync.IsGuest) return Sync.DialogueSync.Continue() ? "continued" : "nothing to continue";
            if (Sync.DialogueSync.InOneSession) return DialogueProbe.Run(new[] { "dlgpress" });   // one session: the real continue button
            DialogueUILogic.Instance.QueueNextLine();
            return "next";
        }
        if (a[0] == "camstate")
        {
            var cam = UnityEngine.Object.FindFirstObjectByType<DialogueCamera>(FindObjectsInactive.Include);
            return $"live={cam._live} onPlayer={cam._onPlayerCamera} shot={cam._shotName} reason={cam._shotReason} transition={cam._lastTransitionDescription} speaker={cam._sessionSpeakerName} head={cam._sessionHead?.name} vcam={cam._vcam?.name} prio={cam._vcam?.Priority} enabled={cam.isActiveAndEnabled} gen={cam._sessionGeneration} retry={cam._retryPending}";
        }
        if (a[0] == "kick")
        {
            // kick <player id>: host drops that guest's connection; the guest joins again by itself (a join mid-talk, mid-anything)
            if (CoopDriver.Session == null) return "no session";
            CoopDriver.Session.Kick(int.Parse(a[1]));
            return $"dropped player {a[1]}";
        }
        if (a[0] == "talknpc") return Sync.DialogueSync.TalkToNearest(a.Length > 1 ? a[1] : null);
        if (a[0] == "talkoptions") return Sync.DialogueSync.TestOptions();
        if (a[0] == "pick")
        {
            UnityEngine.Object.FindFirstObjectByType<DialogueOptionsPanel>(FindObjectsInactive.Include).OnOptionPicked(int.Parse(a[1]));
            return $"picked {a[1]}";
        }
        if (a[0] == "advance")
        {
            var ui = DialogueUILogic.Instance;
            ui.QueueNextLine();
            return $"line {ui.indexInDialogue} talking={ui.IsTalking} options={DialogueOptionsPanel.IsShowing}";
        }
        if (a[0] == "woodcut")
        {
            UseItem_Birds.canDamageTrees = true;
            return "trees can be damaged";
        }
        if (a[0] == "hit") return Sync.WorldObjects.HitNearest(a.Length > 1 ? F(a[1]) : 1000f, a.Length > 2 ? a[2] : null);
        if (a[0] == "collect") return Sync.WorldObjects.CollectNearest(a.Length > 1 ? a[1] : null);
        if (a[0] == "cams")
        {
            var sb = new System.Text.StringBuilder($"main={Camera.main?.name}");
            foreach (var c in Camera.allCameras)
            {
                var brain = c.GetComponent<Cinemachine.CinemachineBrain>();
                sb.Append($"\n  {Sync.GearObjects.PathOf(c.transform)} depth={c.depth} tag={c.tag} target={(c.targetTexture != null)} pos={c.transform.position} rot={c.transform.eulerAngles} fov={c.fieldOfView:0} brain={(brain == null ? "-" : brain.enabled + " live=" + brain.ActiveVirtualCamera?.Name)}");
            }
            return sb.ToString();
        }
        if (a[0] == "lookcap")
        {
            var cc = Sync.Find.One<SaveLoadCharacterCustomisation>();
            var sg = new SaveGame();
            cc?.GetCharacterState(sg);
            var look = Appearance.AppearanceBridge.CaptureLocal();
            return $"customisation={cc != null} assembler={cc?.characterAssembler != null} character={sg.saveableCharacter != null} meshes={(look == null ? "-" : string.Join(",", look.Meshes))}";
        }
        if (a[0] == "gear") return "\n" + Sync.PlayerSync.DescribeGear();
        if (a[0] == "equip") return Equip(a.Length > 1 ? int.Parse(a[1]) : -2);
        if (a[0] == "gearlocal")
        {
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            var sb = new System.Text.StringBuilder();
            foreach (var (key, go) in Sync.GearObjects.Find(me))
            {
                var e = Sync.GearObjects.Capture(key, go);
                sb.Append($"\n  {key}: active={e.Active} anchor={e.Anchor} follower={e.Follower}");
                foreach (var f in go.GetComponentsInChildren<ObjectFollower>(false))
                {
                    var t = f.followTargetFromTransformReference ? f.followTargetTransformReference?.transform : f.followTarget?.transform;
                    sb.Append($"\n     {f.name} -> {(t != null ? t.name : "null")} enabled={f.enabled} d={(t != null ? Vector3.Distance(f.transform.position, t.position) : -1):0.00}");
                }
            }
            return sb.ToString();
        }
        if (a[0] == "bench")
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 10; i++) UnityEngine.Object.FindFirstObjectByType<TasksTracker>();
            double first = sw.Elapsed.TotalMilliseconds / 10; sw.Restart();
            for (int i = 0; i < 10; i++) UnityEngine.Object.FindObjectsByType<PlantGrowth>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            double all = sw.Elapsed.TotalMilliseconds / 10; sw.Restart();
            for (int i = 0; i < 10; i++) UnityEngine.Object.FindObjectsByType<PlantGrowth>(FindObjectsSortMode.None);
            double active = sw.Elapsed.TotalMilliseconds / 10; sw.Restart();
            var t = UnityEngine.Object.FindFirstObjectByType<TasksTracker>();
            for (int i = 0; i < 10; i++) { var _ = t._currentTrialIndex; }
            return $"FindFirst {first:0.00} ms, FindAll(incl. inactive) {all:0.00} ms, FindAll {active:0.00} ms, field {sw.Elapsed.TotalMilliseconds / 10:0.000} ms; fps {1f / Time.smoothDeltaTime:0}";
        }
        if (a[0] == "scanbench") return ScanBench();
        if (a[0] == "ai") return Sync.AiSync.Describe();
        if (a[0] == "tp")
        {
            var mv = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            var to = new Vector3(F(a[1]), 0, F(a[2]));
            // tp x z [y]; without y, the first solid surface from above (trigger volumes over the village don't count)
            if (a.Length > 3) to.y = F(a[3]);
            else to.y = Physics.Raycast(to + Vector3.up * 500, Vector3.down, out var hit, 1000, ~0, QueryTriggerInteraction.Ignore)
                ? hit.point.y + 1 : mv.transform.position.y;
            var rb = mv.GetComponent<Rigidbody>();
            if (rb != null) rb.position = to;
            mv.transform.position = to;
            return $"moved to {to}";
        }
        if (a[0] == "tpto")
        {
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().transform;
            var target = Sync.PlayerSync.NearestPuppetTransform(me.position);
            if (target == null) return "no puppet";
            me.position = target.position - target.forward * 4 + Vector3.up * 0.5f;
            me.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(target.position - me.position, Vector3.up));
            return $"moved to {me.position}";
        }
        if (a[0] == "pshot") return PuppetShot(a.Length > 1 ? a[1] : "front");
        if (a[0] == "tasklist")
        {
            var tt = UnityEngine.Object.FindFirstObjectByType<TasksTracker>();
            if (tt == null || tt.trials == null) return "no TasksTracker";
            var trial = tt.trials[Math.Clamp(tt._currentTrialIndex, 0, tt.trials.Count - 1)];
            var sb = new System.Text.StringBuilder($"trial {tt._currentTrialIndex} {trial.trialName?.ToString()}");
            for (int i = 0; i < trial.tasks.Count; i++) sb.Append($"\n  {i} {(trial.tasks[i].IsComplete ? "x" : " ")} {trial.tasks[i].ID?.ToString()}");
            return sb.ToString();
        }
        if (a[0] == "taskfn")
        {
            // taskfn: each current task's predicate, its result now and the lambda behind it
            var tt = UnityEngine.Object.FindFirstObjectByType<TasksTracker>();
            if (tt == null || tt.trials == null) return "no TasksTracker";
            var trial = tt.trials[Math.Clamp(tt._currentTrialIndex, 0, tt.trials.Count - 1)];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < trial.tasks.Count; i++)
            {
                var t = trial.tasks[i];
                string now, fn;
                try { now = t.checkIfSolved == null ? "-" : t.checkIfSolved.Invoke().ToString(); } catch (Exception e) { now = e.Message; }
                try { fn = t.checkIfSolved?.Method?.Name ?? "-"; } catch (Exception e) { fn = e.Message; }
                sb.Append($"\n  {i} done={t._isComplete} check={now} {fn} {t.ID?.ToString()}");
            }
            return sb.ToString();
        }
        if (a[0] == "trace") return Trace.Start(a.Length > 1 ? F(a[1]) : 20f);
        if (a[0] == "circle")
        {
            // circle <seconds>: walk a slow circle (with turns and speed changes in the camera-relative input)
            _circleFrom = Time.unscaledTime;
            _circleUntil = Time.unscaledTime + (a.Length > 1 ? F(a[1]) : 20f);
            return "walking a circle";
        }
        if (a[0] == "useitem")
        {
            // useitem [area part]: use-an-item areas nearby (the ritual's rings, swept with the pine branch) and their spots.
            // useitem <area part> at <n>: stand at spot n. useitem press | release: hold / let go of the held tool's
            // button, as the mouse would. (Equip the tool first: equip <n>.)
            var body = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            var me = body.transform.position;
            if (a.Length > 1 && (a[1] == "press" || a[1] == "release"))
            {
                var tool = UseItemManager.Instance?.currentItem;
                if (tool == null) return "holding nothing";
                // The tools read the mouse themselves; this is what a held button makes them do each tick.
                if (a[1] == "release") { tool.StopAreaInteract(); return $"release {tool.ID}"; }
                var spot = UnityEngine.Object.FindObjectsByType<UseItemInteract>(FindObjectsSortMode.None)
                    .Where(u => u.interactItem == tool.Type && !u.solved).OrderBy(u => (u.transform.position - me).sqrMagnitude).FirstOrDefault();
                if (spot == null) return $"nothing here to use {tool.ID} on";
                tool.AreaInteract(spot.interactionType, 2f, 0.75f);
                return $"press {tool.ID} ({spot.interactionType}), nearest spot {(spot.transform.position - me).magnitude:0.0} m";
            }
            var areas = UnityEngine.Object.FindObjectsByType<UseItemInteractArea>(FindObjectsSortMode.None)
                .Where(x => a.Length < 2 || Sync.ScenePath.Of(x.transform).Contains(a[1]))
                .OrderBy(x => (x.transform.position - me).sqrMagnitude).ToList();
            string Show(UseItemInteractArea x)
            {
                var spots = new List<string>();
                foreach (var u in x.interacts) spots.Add(u == null ? "?" : (u.solved ? "x" : u.wasInteractedWith ? "w" : ".") + (u.gameObject.activeInHierarchy ? "" : "(off)") + (u.TryCast<UseItemInteract_OverTime>() is { } o ? $"{o.timeInteractedSoFar:0.0}/{o.timeToInteract:0.0}" : ""));
                return $"{(x.transform.position - me).magnitude:0}m {Sync.ScenePath.Of(x.transform)} solved={x.allWereSolved} [{string.Join(" ", spots)}]";
            }
            if (a.Length > 3 && a[2] == "solve")
            {
                // useitem <area part> solve <n>: what the host does for a guest who finished spot n (see UseItemSync)
                if (areas.Count == 0) return "no such area";
                Sync.UseItemSync.SolveFor(areas[0].interacts[int.Parse(a[3])], -1);
                return Show(areas[0]);
            }
            if (a.Length > 3 && a[2] == "at")
            {
                if (areas.Count == 0) return "no such area";
                var u = areas[0].interacts[int.Parse(a[3])];
                var to = u.transform.position + Vector3.up * 0.3f;
                var rb = body.GetComponent<Rigidbody>();
                if (rb != null) { rb.position = to; rb.linearVelocity = Vector3.zero; }
                body.transform.position = to;
                return $"at spot {a[3]} {to}: {Show(areas[0])}";
            }
            return string.Join("\n", areas.Take(6).Select(Show));
        }
        if (a[0] == "drop")
        {
            // drop: put down what this player carries, as the drop key would
            foreach (var co in UnityEngine.Object.FindObjectsByType<CarriableObject>(FindObjectsSortMode.None))
                if (co.isBeingCarried && !Sync.CarrySync.CarriedByOther(co)) { co.StopCarrying(); return $"dropped {co.name}"; }
            return "carrying nothing";
        }
        if (a[0] == "pos")
        {
            var me = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
            return $"pos {me?.transform.position} camYaw {Camera.main?.transform.eulerAngles.y:0} bodyYaw {me?.transform.eulerAngles.y:0}";
        }
        if (a[0] == "walkto")
        {
            // walkto x z [seconds]: walk there with real movement input (carried things come along)
            _walkTo = new Vector3(F(a[1]), 0, F(a[2]));
            _walkToUntil = Time.unscaledTime + (a.Length > 3 ? F(a[3]) : 60f);
            return $"walking to {a[1]}, {a[2]}";
        }
        if (a[0] == "walk")
        {
            var input = new Vector2(F(a[1]), F(a[2]));
            PlayerMovement_2021.DebugMoveInputOverride = new Il2CppSystem.Nullable<Vector2>(input);
            _walkUntil = Time.unscaledTime + F(a[3]);
            return $"walking {input} for {a[3]} s";
        }
        var w = UnityEngine.Object.FindFirstObjectByType<WeatherManager>();
        if (w == null) return "no WeatherManager";
        switch (a[0])
        {
            case "time":
                w.SetDayTime(F(a[1]));
                return $"time {w.dayTime:0.00}";
            case "weather" when a.Length > 1 && a[1] == "rain":
                w.ApplyWeatherState(0.95f, 1f, 1f, 1f, 0.8f, 1f, 0.4f);
                return "rain";
            case "weather" when a.Length > 1 && a[1] == "clear":
                w.ApplyWeatherState(0f, 0f, 0f, 0f, 0.1f, 0f, 0f);
                return "clear";
            default:
                return "unknown command";
        }
    }

    // Renders the nearest puppet through a temporary camera drawn over the game's own, then captures the screen.
    private static string PuppetShot(string side)
    {
        var main = Camera.main;
        var local = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
        var puppet = local == null ? null : Sync.PlayerSync.NearestPuppetTransform(local.transform.position);
        if (main == null || puppet == null) return "no camera or puppet";
        var dir = side switch { "back" => -puppet.forward, "left" => -puppet.right, "right" => puppet.right, _ => puppet.forward };
        var go = new GameObject("RekaCoop_PuppetCam");
        var cam = go.AddComponent<Camera>();
        cam.CopyFrom(main);
        cam.depth = main.depth + 10;
        var focus = puppet.position + Vector3.up * 1.0f;
        go.transform.position = focus + dir * 2.2f + Vector3.up * 0.3f;
        go.transform.LookAt(focus);
        ScreenCapture.CaptureScreenshot(Path.Combine(BepInEx.Paths.GameRootPath, "puppet.png"));
        _photoCam = go;
        _restoreCameraFrame = Time.frameCount + 3;
        return $"photographing puppet from {side} at {Vector3.Distance(local.transform.position, puppet.position):0.0} m";
    }

    private static string SkipTutorial()
    {
        Cheats.SkipTo = 1;
        RitualIdentifier.ritualFinished = true;
        RitualIdentifier.isRitual = false;
        SaveLoadGame.SkipTutorialAndJumpToFirstMap();
        return "skipping tutorial";
    }

    private static string Blockers()
    {
        var slg = UnityEngine.Object.FindFirstObjectByType<SaveLoadGame>();
        return $"refused={slg?.SavingIsRefusedNow} blockers=[{SaveBlockers.GetDebugString()}] talking={DialogueUILogic.isTalkingOrInQueue} popup={PopUpManager.isShowingPopupOrInQueue} loading={PopUpManager.IsInLoadingScreen}";
    }

    private static string Unblock(bool step)
    {
        if (!step) return Blockers();
        var card = UnityEngine.Object.FindFirstObjectByType<PopUp_Chapter>();
        if (PopUpManager.isShowingPopupOrInQueue && card != null) card.OnConfirmPressed();
        else if (DialogueUILogic.isTalkingOrInQueue && DialogueUILogic.Instance != null)
        {
            DialogueUILogic.Instance.SkipToLastLine();
            DialogueUILogic.Instance.StopTalkAndCallOnFinished();
        }
        else UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>()?.UnblockMovement();
        return Blockers();
    }

    private static string HutProbe(ChickenLegsBehaviour h) =>
        $"controlled={h.isBeingControlled} mode={h.CurrentControlMode} inputAllowed={h.InputAllowed} moveAxis={h.moveAxis} raw={h.oldMoveAxisRaw} " +
        $"sprint={h.toggleSprintToggledOn} speed={h.currentSpeed:0.00}/{h.targetSpeed:0.00} auto={h.isAutoMoving} standing={h.isStanding} moving={h.HutIsMoving} " +
        $"player={(h.player != null)} pos={h.transform.position} yaw={h.transform.eulerAngles.y:0} cam={Camera.main?.name} lookAt={h.lookAtEngaged} " +
        $"takeover<{h.lookAtTakeoverDistance} always<{h.lookAtAlwaysEngageDistance} dot>{h.lookAtCameraDot} sitPrompt={h._sitPromptVisible} focus={h._hasSitFocus} id={h.myInteractionID} atHome={h.playerAtHomeBase}";

    private static string DescribeDest(TravelLocation d) =>
        d == null ? "null" : $"cost={d.travelCost} pois={d.poiCount} village={d.hasVillage} tier={d.usedItemTierForCreation} preset={(d.preset != null ? d.preset.name : "-")} spawn=[{(d.poiIDsToSpawn != null ? string.Join(",", d.poiIDsToSpawn.ToArray()) : "")}]";

    private static string Click(string label)
    {
        var pressed = new System.Collections.Generic.List<string>();
        foreach (var b in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsSortMode.None))
        {
            var text = b.GetComponentInChildren<TMPro.TMP_Text>()?.text?.Trim();
            if (!b.interactable || !(string.Equals(text, label, StringComparison.OrdinalIgnoreCase) || (string.IsNullOrEmpty(text) && b.name == label))) continue;
            b.onClick.Invoke();
            pressed.Add(b.name);
        }
        return $"pressed [{string.Join(", ", pressed)}] then {Blockers()}";
    }

    private static string ScanBench()
    {
        var sb = new System.Text.StringBuilder();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var roots = new System.Collections.Generic.List<GameObject>();
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            roots.AddRange(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).GetRootGameObjects());
        var home = BB_Building.Home;
        if (home != null && !roots.Contains(home.root.gameObject)) { sb.Append("home root not in scenes; "); roots.AddRange(home.gameObject.scene.GetRootGameObjects()); }
        sb.Append($"{roots.Count} roots in {sw.Elapsed.TotalMilliseconds:0.00} ms; ");
        void Walk<T>(bool inactive) where T : Component
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            double worst = 0; int n = 0; string worstName = "";
            foreach (var r in roots)
            {
                var one = System.Diagnostics.Stopwatch.StartNew();
                n += r.GetComponentsInChildren<T>(inactive).Length;
                if (one.Elapsed.TotalMilliseconds > worst) { worst = one.Elapsed.TotalMilliseconds; worstName = r.name; }
            }
            double walk = t.Elapsed.TotalMilliseconds; t.Restart();
            int m = UnityEngine.Object.FindObjectsByType<T>(inactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;
            sb.Append($"\n{typeof(T).Name}: walk {n} in {walk:0.00} ms (worst root {worstName} {worst:0.00}), find {m} in {t.Elapsed.TotalMilliseconds:0.00} ms");
        }
        Walk<PlantGrowth>(true);
        Walk<IvyGrowth>(true);
        Walk<InteractableInventoryLogic>(false);
        Walk<GeneralAIController>(false);
        Walk<POI_SaveLoad>(false);
        sb.Append("\n" + Sync.StationSync.Bench());
        return sb.ToString();
    }

    private static string HutFix()
    {
        foreach (var d in Resources.FindObjectsOfTypeAll<DeactivateHomeForTutorial>())
            if (d.gameObject.scene.IsValid()) d.ReactivateThisGameObject();
        var huts = Resources.FindObjectsOfTypeAll<ChickenLegsBehaviour>();
        ChickenLegsBehaviour legs = null;
        foreach (var h in huts) if (h.gameObject.scene.IsValid()) legs = h;
        if (legs == null) return "no hut in the scene";
        for (var t = legs.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
        legs.enabled = true;
        var player = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>().transform;
        legs.TeleportToPositionAndSnapOntoTerrain(player.position + player.forward * 15f);
        legs.SitDownImmediately();
        return $"hut at {legs.transform.position} active={legs.gameObject.activeInHierarchy}";
    }

    private static string Status()
    {
        var huts = UnityEngine.Object.FindObjectsByType<ChickenLegsBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var hut = huts.Length > 0 ? huts[0] : null;
        var w = UnityEngine.Object.FindFirstObjectByType<WeatherManager>();
        var unlocked = new System.Collections.Generic.List<string>();
        if (UseItemManager.Instance != null)
            foreach (var item in UseItemManager.Instance.allItems) if (item.unlocked) unlocked.Add(item.ID);
        return $"tutorial={SaveLoadGame.LoadedToTutorial} chapter={TasksTracker.CurrentChapterTag}/{TasksTracker.currentChapterState} " +
               $"ritualFinished={RitualIdentifier.ritualFinished} hut={(hut != null ? hut.transform.position + " active=" + hut.gameObject.activeInHierarchy : "none")} " +
               $"time={w?.dayTime:0.00} override={w?.manualOverride} items=[{string.Join(",", unlocked)}]";
    }

    private static string Equip(int n)
    {
        var m = UseItemManager.Instance;
        if (m == null) return "no UseItemManager";
        var tools = new System.Collections.Generic.List<BaseUseItem>();
        foreach (var item in m.allItems) if (item.unlocked) tools.Add(item);
        if (n == -1) { m.EquipNone(); return "put away"; }
        if (n < 0 || n >= tools.Count)
            return $"holding {(m.currentItem != null ? m.currentItem.ID : "nothing")}; tools: {string.Join(", ", tools.Select((t, i) => $"{i} {t.ID}"))}";
        m.SetCurrentItem(tools[n]);
        return $"holding {tools[n].ID}";
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    private static string TPath(Transform t) => t.parent == null ? t.name : TPath(t.parent) + "/" + t.name;
}
#endif
