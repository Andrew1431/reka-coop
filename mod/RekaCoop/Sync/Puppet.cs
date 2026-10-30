using System.Collections.Generic;
using UnityEngine;

namespace RekaCoop.Sync;

// A remote player's body: a stripped copy of the local player's body (meshes, bones, animator only),
// moved by interpolating received snapshots on a delayed clock.
public class Puppet
{
    private const float InterpDelay = 0.1f;
    private const float CrossfadeAfter = 0.15f;  // let parameters drive transitions first

    public readonly int PlayerId;
    public readonly GameObject Root;
    public readonly PuppetGear Gear;
    private readonly Animator _anim;
    private readonly List<(float time, PlayerSnapshot snap)> _buffer = new();
    private readonly float[] _stateMismatchSince = new float[PlayerSnapshot.Layers];
    private PlayerSnapshot _armsFrom, _armsTo;
    private float _armsT;

    // Components that only draw or animate. Everything else (movement, saving, IDs, audio, physics) is removed.
    private static readonly HashSet<string> Keep = new()
    {
        "Transform", "Animator", "SkinnedMeshRenderer", "MeshRenderer", "MeshFilter", "LODGroup",
        "CharacterAssembler",  // rebuilds the clothes and body meshes for the remote player's look
    };

    // Replaces the copied local look with the remote player's: drop the copied wearable meshes, then let
    // the game's CharacterAssembler spawn the wearables for that look onto the puppet's skeleton.
    public void ApplyLook(Appearance.AppearanceData look)
    {
        var assembler = Root.GetComponent<CharacterAssembler>();
        var def = Appearance.AppearanceBridge.ToDefinition(look, assembler);
        if (def == null)
        {
            Plugin.Logger.LogWarning($"Puppet {PlayerId}: no CharacterAssembler or body definition, keeping copied look");
            return;
        }
        for (int i = Root.transform.childCount - 1; i >= 0; i--)
        {
            var child = Root.transform.GetChild(i);
            if (child.name.EndsWith("(Clone)") && child.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                Object.Destroy(child.gameObject);
        }
        _wanted = look;
        _ourBuilds++;
        assembler.SpawnFromGenerationDefinition(def);
        Plugin.Logger.LogInfo($"Puppet {PlayerId}: building look with {look.Meshes.Count} meshes");
    }

    // The look this puppet should wear, and how many of the assembler's builds are ours. A build that finishes and
    // isn't ours (the copied assembler dressing the copy in the local player's look) gets this look put back on.
    private Appearance.AppearanceData _wanted;
    private int _ourBuilds, _foreignFixes;

    private void OnGenerated()
    {
        if (_ourBuilds > 0) { _ourBuilds--; return; }
        if (_wanted == null || Root == null || _foreignFixes >= 3) return;
        _foreignFixes++;
        Plugin.Logger.LogWarning($"Puppet {PlayerId}: dressed by something else; putting their look back on");
        ApplyLook(_wanted);
    }

    public Puppet(int playerId, GameObject template)
    {
        PlayerId = playerId;

        // Cloning under an inactive parent keeps Awake/OnEnable from running on the game's components.
        var holder = new GameObject($"RekaCoop_PuppetHolder_{playerId}");
        holder.SetActive(false);
        var body = Object.Instantiate(template, holder.transform);
        body.name = $"RekaCoop_Puppet_{playerId}";
        Strip(body);

        Root = body;
        body.transform.SetParent(null, false);
        Object.Destroy(holder);
        _anim = body.GetComponent<Animator>();
        _anim.applyRootMotion = false;
        _anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        body.AddComponent<PuppetAnimEvents>();
        // The copy's assembler would otherwise dress it again on start, in the local player's look.
        var assembler = body.GetComponent<CharacterAssembler>();
        if (assembler != null)
        {
            assembler.generateRandomOnAwake = false;
            assembler.initializeFromGenerationDefinition = false;
            assembler.OnCharacterGenerated?.AddListener(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(new System.Action(OnGenerated)));
        }
        body.SetActive(true);
        Gear = new PuppetGear(this);
    }

    private static void Strip(GameObject body)
    {
        // Constraint components depend on rig components, so destroy in passes until nothing else goes.
        for (int pass = 0; pass < 4; pass++)
        {
            bool removed = false;
            foreach (var c in body.GetComponentsInChildren<Component>(true))
            {
                if (c == null || Keep.Contains(c.GetIl2CppType().Name)) continue;
                Object.DestroyImmediate(c);
                removed = true;
            }
            if (!removed) break;
        }
    }

    public void Push(PlayerSnapshot snap)
    {
        _buffer.Add((Time.unscaledTime, snap));
        if (_buffer.Count > 30) _buffer.RemoveAt(0);
    }

    public void Update()
    {
        if (_buffer.Count == 0) return;
        float renderTime = Time.unscaledTime - InterpDelay;

        int i = _buffer.Count - 1;
        while (i > 0 && _buffer[i - 1].time > renderTime) i--;
        var b = _buffer[i];
        var a = i > 0 ? _buffer[i - 1] : b;
        float t = b.time > a.time ? Mathf.Clamp01((renderTime - a.time) / (b.time - a.time)) : 1f;

        if (a.snap.OnHut != b.snap.OnHut) a = b;  // boarding or leaving: no blend across frames
        var pos = Vector3.Lerp(a.snap.Position, b.snap.Position, t);
        float yaw = Mathf.LerpAngle(a.snap.Yaw, b.snap.Yaw, t);
        var floor = HutSync.Floor;
        if (b.snap.OnHut && floor != null)
        {
            pos = floor.TransformPoint(pos);
            yaw += floor.eulerAngles.y;
        }
        Root.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));

        ApplyAnimator(b.snap);
        (_armsFrom, _armsTo, _armsT) = (a.snap, b.snap, t);
    }

    // The carrier's arms over what the animator posed.
    public void LateUpdate()
    {
        var to = _armsTo?.Arms;
        if (to == null) return;
        var bones = ArmBones.Of(Root.transform);
        if (bones == null || bones.Length != to.Length) return;
        var from = _armsFrom?.Arms;
        for (int i = 0; i < bones.Length; i++)
            bones[i].localRotation = from != null && from.Length == to.Length ? Quaternion.Slerp(from[i], to[i], _armsT) : to[i];
    }

    private void ApplyAnimator(PlayerSnapshot s)
    {
        var ps = PlayerSync.Params;
        if (ps == null) return;
        for (int i = 0; i < ps.Floats.Length && i < s.Floats.Length; i++)
            _anim.SetFloat(ps.Floats[i], s.Floats[i]);
        for (int i = 0; i < ps.Bools.Length; i++)
            _anim.SetBool(ps.Bools[i], (s.Bools & (1UL << i)) != 0);

        for (int layer = 0; layer < PlayerSnapshot.Layers && layer < _anim.layerCount; layer++)
        {
            if (layer > 0) _anim.SetLayerWeight(layer, s.LayerWeight[layer]);
            int current = _anim.GetCurrentAnimatorStateInfo(layer).fullPathHash;
            if (current == s.StateHash[layer] || _anim.IsInTransition(layer))
            {
                _stateMismatchSince[layer] = -1;
                continue;
            }
            if (_stateMismatchSince[layer] < 0) _stateMismatchSince[layer] = Time.unscaledTime;
            if (Time.unscaledTime - _stateMismatchSince[layer] < CrossfadeAfter) continue;
            _anim.CrossFadeInFixedTime(s.StateHash[layer], 0.1f, layer, 0f);
            _stateMismatchSince[layer] = -1;
        }
    }

    public string Describe(Vector3 from)
    {
        int renderers = 0, visible = 0;
        foreach (var r in Root.GetComponentsInChildren<Renderer>(true))
        {
            renderers++;
            if (r.enabled && r.gameObject.activeInHierarchy && r.isVisible) visible++;
        }
        var pos = Root.transform.position;
        return $"puppet {PlayerId} at {pos} d={Vector3.Distance(from, pos):0.0}m renderers {visible}/{renderers} buffered {_buffer.Count} steps {PuppetAnimEvents.Steps}";
    }

    public void Destroy()
    {
        if (Root != null) Object.Destroy(Root);
    }
}

// Both arms, shoulder to hand, by bone name (the same skeleton on the player and on every puppet).
internal static class ArmBones
{
    private static readonly string[] Names =
        { "shoulder.l", "arm_stretch.l", "forearm_stretch.l", "hand.l", "shoulder.r", "arm_stretch.r", "forearm_stretch.r", "hand.r" };
    private static readonly Dictionary<System.IntPtr, Transform[]> Cache = new();
    private static UnityEngine.Animations.Rigging.Rig[] _rigs;

    // The player's arm IK rigs (carrying, and whatever else the game poses the arms with) are in use.
    public static bool RigsActive(Transform player)
    {
        if (_rigs == null || _rigs.Length == 0 || _rigs[0] == null)
        {
            var found = new List<UnityEngine.Animations.Rigging.Rig>();
            foreach (var rig in player.root.GetComponentsInChildren<UnityEngine.Animations.Rigging.Rig>(true))
                if (rig.name == "LeftArmRig" || rig.name == "RightArmRig") found.Add(rig);
            _rigs = found.ToArray();
        }
        foreach (var rig in _rigs) if (rig != null && rig.weight > 0.01f) return true;
        return false;
    }

    public static Transform[] Of(Transform body)
    {
        if (body == null) return null;
        if (Cache.TryGetValue(body.Pointer, out var hit) && (hit == null || hit[0] != null)) return hit;
        var found = new Transform[Names.Length];
        foreach (var t in body.GetComponentsInChildren<Transform>(true))
        {
            int i = System.Array.IndexOf(Names, t.name);
            if (i >= 0 && found[i] == null) found[i] = t;
        }
        foreach (var t in found) if (t == null) { found = null; break; }
        return Cache[body.Pointer] = found;
    }
}
