namespace RekaCoop.Net;

// Wire format: [byte MsgType][body]. Bump Version on any change to a message layout.
public static class Protocol
{
    public const int Version = 55;
    public const string ConnectKey = "reka-coop";
    public const int DefaultPort = 27877;
}

public enum MsgType : byte
{
    Hello = 1,      // client -> host: int version, string name
    HelloAck = 2,   // host -> client: int assignedPlayerId
    Reject = 3,     // host -> client: string reason
    Ping = 4,       // either way: double senderTime
    Pong = 5,       // reply: double echoedTime
    PlayerLeft = 6, // host -> clients: int playerId

    FirstGameMessage = 16,
    PlayerState = 16,  // int playerId, PlayerSnapshot (see Sync/PlayerSnapshot.cs; arm bones while carrying); host relays client states
    Appearance = 17,   // int playerId, AppearanceData; host stores, relays, and replays to late joiners
    Weather = 18,      // host -> clients: SerializableWeather value fields (+ forecast), see Sync/WorldSync.cs
    Gear = 19,         // int playerId, GearState (see Sync/Gear.cs); host stores, relays, and replays to late joiners
    World = 20,        // host -> client: byte kind; Save: string folder, string slot, int n, n x (string file, gzip bytes); Travelling: (nothing); see Sync/WorldTransfer.cs
    HutState = 21,     // host -> clients: hut root pose, standing, sit/stand ground effect, animator params/states (see Sync/HutSync.cs)
    HutRequest = 22,   // client -> host: byte kind (call/stand/sit/steer start/steer/steer stop), Vector3 pos (steer: move axis x, y, sprint)
    Inventory = 23,    // byte kind: Hello (guest key) / Full (host -> guest: last applied seq, whole pack) / Delta (seq, changes); see Sync/InventorySync.cs
    WorldObject = 24,  // byte kind: Collected / Damage (key, amount) both ways, Ready (client in world) / History (host -> client) / Denied (host -> client: taken first); see Sync/WorldObjects.cs
    Build = 25,        // byte kind: Changes (removed indices, placed/changed HouseBlocks; host relays) / Ready (client) / Snapshot (host -> client); see Sync/BuildSync.cs
    Dialogue = 26,     // byte kind: host -> clients: Line (int leader, name, title, text) / Options (int leader, labels) / Chosen (int leader, label) / End / Camera (speaker head path, name); client -> host: Pick (int) / Next / Talk (byte act, string path); see Sync/DialogueSync.cs
    Reward = 27,       // unused since protocol 39 (rewards travel in the shared pack)
    Story = 28,        // byte kind: Ready (client) / Flags (host -> client: n x (byte table, string id, int value)); see Sync/StorySync.cs
    Container = 29,    // byte kind: Contents (key, slots, items; both ways, host relays) / Ready (client) / Snapshot (host -> client); see Sync/ContainerSync.cs
    Trial = 30,        // byte kind: Ready (client) / State (host -> client: int n, n x (string key, int value)); see Sync/TrialSync.cs
    Station = 31,      // byte kind: State (key, xml; both ways, host relays) / Ready (client) / Snapshot (host -> client); see Sync/StationSync.cs
    Poi = 32,          // byte kind: State (uniqueID, gzip xml; both ways, host relays) / Ready (client) / Snapshot (host -> client) / Request (client: uniqueID); see Sync/PoiSync.cs
    Name = 33,         // int playerId, string name (any -> host, relayed; host -> newcomer for everyone); see Sync/NameTags.cs
    Ai = 34,           // host -> client, unreliable, 8 per packet: int n, n x (string placeId/elementId, float x, y, z, yaw, short behaviour, float goal x, y, z); see Sync/AiSync.cs
    Blueprint = 35,    // byte kind: State (all sites as SavedBBs xml, "" = none; both ways, host relays) / Ready (client); see Sync/BlueprintSync.cs
    Carry = 36,        // byte kind: Pick / Drop (int playerId, string elementId, pose: relative to the carrier's body / where it was put down; Pick then the animal's animator as in Mirror; both ways, host relays) / Denied (host -> client: elementId) / Ready (client); see Sync/CarrySync.cs
    Mirror = 37,       // host -> clients, unreliable, 10 Hz (1 Hz while still), animals and villagers near a guest: byte n, n x (string key, float x, y, z, yaw, byte m, m x half animator param, byte l, l x (int state hash, float normalized time)); see Sync/MirrorSync.cs
    Birds = 38,        // int playerId, byte n, n x (byte slot, float x, y, z, pitch, yaw, byte visibility): the sender's ravens in flight, unreliable 10 Hz; host relays; see Sync/BirdSync.cs
    Saveable = 39,     // byte kind: Ready (client) / State (host -> client: int n, n x (string key, gzip xml of the component's saved progress, guidance, collection, place history)); see Sync/SaveableSync.cs
    Active = 40,       // byte kind: Ready (client) / Changes (host -> client: int n, n x (string key = root name/child[i]/..., bool on)); see Sync/ActiveSync.cs
    Digest = 41,       // byte kind: Digest (host -> clients every 15 s: int n, n x (string category, bool has, ulong hash)) / DetailRequest (client: category) / Detail (host -> client: category, gzip key/value lines); see Sync/DigestSync.cs
    Popup = 42,        // byte kind: Loot (title, n x (possession name, amount), m x block name; both ways, host relays) / Decision (host -> leader: path) / Watch (host -> others: path) / Choice (leader -> host: path, option) / Chapter (int tag, name, bool completed) / Unlock (use item id, bool epic) / Message (text, sprite name, int type, single label or ""): host -> clients; see Sync/PopupSync.cs
    Condition = 43,    // byte kind: Condition (string path of a GenericPOICondition, bool value) / GateClose, GateOpen (int playerId, string gate path, float x, y, z of the player); both ways, host relays; see Sync/ConditionSync.cs
    Tutorial = 44,     // byte kind: State (int block, int solved tasks; guest -> host when it got ahead, host -> clients on change and every 3 s); see Sync/TutorialSync.cs
    Map = 45,          // byte kind: Ready (client) / Change (int n, n x (string compass element ID, bool highlighted); both ways, host relays) / All (host -> client: int n, n x ID); see Sync/MapSync.cs
    Report = 46,       // byte kind: Problem (string reportId (12 hex), int playerId, string note); any -> host, relayed; each copy uploads its log under that id; see Diagnostics/ReportSync.cs
    Intent = 47,       // guest -> host: byte kind, string scene path, args by kind; what a guest does to the story (see Sync/Intents.cs)
}
