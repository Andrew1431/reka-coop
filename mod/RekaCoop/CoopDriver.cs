using System;
using System.Text;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop;

// The one persistent MonoBehaviour. Owns the session, per-frame work and the on-screen status line.
public class CoopDriver : MonoBehaviour
{
    public CoopDriver(IntPtr ptr) : base(ptr) { }

    internal static CoopSession Session;
    private static MultiTransport _net;

    private static string _notice;
    // Guest: shown until the host's world loads (the host is at the menu, or starting a new journey).
    internal static string Waiting;
    private static GUIStyle _noticeStyle;
    private static float _noticeUntil;
    private bool _loggedSaveRoot;
    private string _status = "";
    private float _nextStatus;

    private void Start()
    {
        try
        {
            if (Instance.Role == Role.None) return;
            // Auto (no options): Steam only, added by SteamLobby once Steam is up. A dev host listens on UDP now and on
            // Steam once Steam is up; a dev guest uses one or the other.
            _net = Instance.Auto ? new MultiTransport()
                : new MultiTransport(Instance.Role == Role.Client && Instance.ConnectAddress == null ? new SteamTransport() : new UdpTransport());
            Session = new CoopSession(SteamName() ?? Instance.Name ?? DefaultName, _net);
            Session.Log += msg => Plugin.Logger.LogInfo($"[net] {msg}");
            Session.Rejected += OnRejected;
            Application.add_quitting(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(OnQuitting));
            Sync.PlayerSync.Attach(Session);
            Sync.CarrySync.Attach(Session);
            Sync.MirrorSync.Attach(Session);
            Appearance.AppearanceSync.Attach(Session);
            Sync.WorldSync.Attach(Session);
            Sync.GearSync.Attach(Session);
            Sync.WorldTransfer.Attach(Session);
            Sync.HutSync.Attach(Session);
            Sync.InventorySync.Attach(Session);
            Sync.WorldObjects.Attach(Session);
            Sync.BuildSync.Attach(Session);
            Sync.BlueprintSync.Attach(Session);
            Sync.DialogueSync.Attach(Session);
            Sync.Intents.Attach(Session);
            Sync.StorySync.Attach(Session);
            Sync.ContainerSync.Attach(Session);
            Sync.TrialSync.Attach(Session);
            Sync.SaveableSync.Attach(Session);
            Sync.ActiveSync.Attach(Session);
            Sync.DigestSync.Attach(Session);
            Sync.PopupSync.Attach(Session);
            Sync.ConditionSync.Attach(Session);
            Sync.TutorialSync.Attach(Session);
            Sync.MapSync.Attach(Session);
            Sync.StationSync.Attach(Session);
            Sync.NameTags.Attach(Session);
            Sync.AiSync.Attach(Session);
            Sync.PoiSync.Attach(Session);
            Sync.BirdSync.Attach(Session);
            Diagnostics.ReportSync.Attach(Session);
            if (Instance.Auto) StartHosting();
            else if (Instance.Role == Role.Host)
            {
                // Another copy of Reka holding the port mustn't stop hosting: Steam friends don't need it.
                Session.Open = false;  // until the host is in a world (WatchHosting)
                try { Session.Host(Instance.Port); }
                catch (InvalidOperationException e)
                {
                    Plugin.Logger.LogWarning($"{e.Message}; hosting over Steam only");
                    Notice($"UDP port {Instance.Port} is in use (another copy of Reka?). Friends can still join over Steam, not by IP.");
                }
            }
            else if (Instance.ConnectAddress != null) Session.Connect(Instance.ConnectAddress, Instance.Port);
            // else: SteamLobby connects once it has found the host's lobby
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }

    private void LateUpdate()
    {
        if (Session != null) { Sync.PlayerSync.LateTick(); Sync.DialogueSync.LateTick(); }
    }

    private void Update()
    {
        try
        {
            if (Session != null)
            {
                // Steam starts after us; until then we're "player". Hello goes out on connect, which is later.
                if ((Session.LocalName == DefaultName || Session.LocalName == Instance.Name) && SteamName() is string steamName) Session.LocalName = steamName;
                Session.Poll();
                WatchGuestQuit();
                WatchHostsWorld();
                WatchHosting();
                Steam.SteamLobby.Tick(Session, _net);
                Sync.PlayerSync.Tick(Session);
                Sync.WorldSync.Tick(Session);
                Sync.WorldTransfer.Tick(Session);
                Sync.CarrySync.Tick(Session);
                Sync.MirrorSync.Tick(Session);
                Sync.HutSync.Tick(Session);
                Sync.DialogueSync.Tick(Session);
                Sync.ActingPlayer.Tick(Session);
                Sync.AreaSync.Tick(Session);
                Sync.BirdSync.Tick(Session);
            }
            Diagnostics.Reporter.Tick(Session);
            UpdateCheck.Tick();
            Dev.WindowTitle.Tick();
#if DEVBUILD
            Dev.AutoLoad.Tick();
            Dev.BotWalk.Tick();
            Dev.Shot.Tick();
            Dev.Commands.Tick();
            Dev.Discovery.Tick();
#endif

            if (!_loggedSaveRoot && FileIO.InstanceIfExists != null && FileIO.InstanceIfExists.isInitialised)
            {
                _loggedSaveRoot = true;
                Plugin.Logger.LogInfo(SaveIsolation.Describe());
            }

            if (Time.unscaledTime >= _nextStatus)
            {
                _nextStatus = Time.unscaledTime + 0.5f;
                _status = BuildStatus();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError(e);
        }
    }

    // Auto: back to hosting our own world (at the start, and after leaving someone else's).
    private static void StartHosting()
    {
        Instance.BecomeHost();
        SaveIsolation.UseOwnRoot();
        Session.Open = false;
        Session.Host(0);  // Steam only; SteamLobby adds its transport, which then listens
    }

    internal static void ResumeHosting() => StartHosting();

    // Auto: a friend's invite was accepted at the main menu. Their world goes in the guest folder, not ours.
    internal static void BecomeGuest()
    {
        Instance.BecomeGuest();
        SaveIsolation.UseGuestRoot();
    }

    // Host: friends can join only while there's a world to share. Back at the main menu, everyone goes back to theirs
    // and joins again from Steam once the host is in a world again. Not while travelling or loading (no menu then).
    private float _nextHostingCheck;

    private void WatchHosting()
    {
        // Finding the menu walks every object in the scene: twice a second, not every frame (seen: host at ~16 fps).
        if (Session.Role != Role.Host || Time.unscaledTime < _nextHostingCheck) return;
        _nextHostingCheck = Time.unscaledTime + 0.5f;
        if (!Session.Open && Sync.WorldTransfer.HostInWorld)
        {
            Session.Open = true;
            Plugin.Logger.LogInfo("Hosting: in a world; friends can join");
        }
        else if (Session.Open && Sync.MenuWatch.Up)
        {
            Session.Open = false;
            Plugin.Logger.LogInfo("Hosting: back at the main menu; closed");
            if (Session.IsConnected) Session.Close("The host went back to the main menu. Join again from Steam once they're back in a world.");
        }
    }

    // Guest: the host ended the session (or turned us away). Back to our own main menu and saves.
    private void OnRejected(string reason)
    {
        if (Session.Role != Role.Client) return;
        Plugin.Logger.LogInfo($"Session ended by the host: {reason}");
        Notice(reason);
        Steam.SteamLobby.Leave();
        if (Sync.PlayerSync.InWorld)
        {
            var pause = FindFirstObjectByType<PauseMenu>(FindObjectsInactive.Include);
            if (pause != null) pause.BackToMainMenu();
        }
        if (Instance.Auto) _hostAtMenu = true;  // our own saves only once the host's world is gone (WatchHostsWorld)
    }

    private bool _hostAtMenu;

    private void WatchHostsWorld()
    {
        if (Session.Role == Role.Client && Sync.PlayerSync.InWorld) Sync.WorldSync.HostsWorldLoaded = true;
        if (!Sync.MenuWatch.Up) return;
        if (Sync.WorldSync.HostsWorldLoaded) Plugin.Logger.LogInfo("Saves: the host's world is unloaded");
        Sync.WorldSync.HostsWorldLoaded = false;
        if (_hostAtMenu) { _hostAtMenu = false; StartHosting(); }
    }

    // Not the Windows user name: that would show on the host's screen.
    private const string DefaultName = "player";
    private bool _guestWasInWorld;

    // Guest back at the main menu from the host's world (quit from the pause menu): leave, or the host keeps a
    // frozen body and an invite does nothing (we'd still count as connected).
    private void WatchGuestQuit()
    {
        if (Session.Role != Role.Client || !Session.IsConnected) { _guestWasInWorld = false; return; }
        if (Sync.PlayerSync.InWorld) { _guestWasInWorld = true; return; }
        if (!_guestWasInWorld || Sync.WorldTransfer.LoadingHostWorld || !Sync.MenuWatch.Up) return;
        _guestWasInWorld = false;
        Plugin.Logger.LogInfo("Back at the main menu: leaving the host");
        Session.Leave();
        Steam.SteamLobby.Leave();
        Notice(Instance.ConnectAddress == null
            ? "You left the host's world. Join them again from Steam to rejoin."
            : "You left the host's world. Restart Reka to rejoin.");
        if (Instance.Auto) StartHosting();
    }

    private static string SteamName()
    {
        try { return Steamworks.SteamClient.IsValid ? Steamworks.SteamClient.Name : null; }
        catch { return null; }
    }

    // The game shuts Steam down while quitting, before OnDestroy; say goodbye while Steam still sends.
    private static readonly Action OnQuitting = () => { try { Session?.Stop(); } catch { } Diagnostics.Reporter.EndSession(); };

    private void OnDestroy()
    {
        Steam.SteamLobby.Leave();
        Session?.Stop();
        Diagnostics.Reporter.EndSession();
    }

    private string BuildStatus()
    {
        var sb = new StringBuilder($"Reka Co-op {Plugin.PluginVersion} | {Instance.Name ?? "default"}");
        if (Session != null)
        {
            sb.Append($" | {Session.State}");
            if (Steam.SteamLobby.Status.Length > 0) sb.Append($" | {Steam.SteamLobby.Status}");
            foreach (var p in Session.Players.Values)
                if (p.Ready) sb.Append($" | {p.Name}#{p.PlayerId} {p.RttMs:0}ms");
            sb.Append($" | puppets {Sync.PlayerSync.PuppetCount}");
        }
        return sb.ToString();
    }

    // A message under the status line for a while, for when the game has no dialogue box to show it in.
    internal static void Notice(string text)
    {
        _notice = text;
        _noticeUntil = Time.unscaledTime + 15f;
    }

    private void OnGUI()
    {
        var text = _notice != null && Time.unscaledTime < _noticeUntil ? _notice : Waiting;
        if (text != null)
        {
            // The default box is too faint over bright scenes (the main menu): a dark band with larger text.
            if (_noticeStyle == null || _noticeStyle.normal.background == null)
            {
                var bg = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };  // or a scene change unloads it
                bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.75f));
                bg.Apply();
                _noticeStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, padding = new RectOffset(12, 12, 6, 6) };
                _noticeStyle.normal.background = bg;
                _noticeStyle.normal.textColor = Color.white;
            }
            GUI.Label(new Rect(8, 52, 900, 34), text, _noticeStyle);
        }
        GUI.Label(new Rect(8, 4, 1100, 22), _status);
        if (Steam.SteamLobby.CanInvite && GUI.Button(new Rect(8, 26, 130, 22), "Invite friends")) Steam.SteamLobby.OpenInvite();
        if (Session is { IsConnected: true }) Sync.NameTags.Draw(Sync.PlayerSync.PuppetHeads());
        Diagnostics.Reporter.OnGUI();
    }
}
