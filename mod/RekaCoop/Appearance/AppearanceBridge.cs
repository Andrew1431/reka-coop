using Il2CppSystem.Collections.Generic;
using MaterialFloatDef = CharacterAssembler.GenerationDefinition.MaterialFloatSettingDefinition;

namespace RekaCoop.Appearance;

// Converts between AppearanceData and the game's character types, and applies looks through the game's own code.
internal static class AppearanceBridge
{
    private static SaveLoadCharacterCustomisation Customisation =>
        RekaCoop.Sync.Find.One<SaveLoadCharacterCustomisation>();

    // The local player's current look, via the same path the game uses when saving.
    public static AppearanceData CaptureLocal()
    {
        var c = Customisation;
        if (c == null) return null;
        var save = new SaveGame();
        c.GetCharacterState(save);
        return save.saveableCharacter == null ? null : FromSaveable(save.saveableCharacter);
    }

    // Re-dresses the local player in the world. RestoreCharacterState only takes effect while a save is
    // loading, so rebuild through the assembler instead, the same way puppets are dressed.
    public static bool ApplyLocal(AppearanceData data)
    {
        var assembler = RekaCoop.Sync.Find.One<PlayerMovement_2021>()?.GetComponent<CharacterAssembler>();
        var def = ToDefinition(data, assembler);
        if (def == null) return false;
        assembler.DestroyAllWearableMeshes();
        assembler.SpawnFromGenerationDefinition(def);
        return true;
    }

    // A generation definition for building this look on any CharacterAssembler (puppets).
    public static CharacterAssembler.GenerationDefinition ToDefinition(AppearanceData data, CharacterAssembler assembler)
    {
        var c = Customisation;
        if (c == null || assembler == null) return null;
        var body = assembler.cbd ?? (assembler.characterBodyDefinitions?.Count > 0 ? assembler.characterBodyDefinitions[0] : null);
        return ToSaveable(data).ToGenerationDefinition(c.pRef, body);
    }

    private static AppearanceData FromSaveable(SaveGame.SaveableCharacter s)
    {
        var data = new AppearanceData
        {
            VersionId = s.saveGameVersionID ?? "",
            MaskIndex = s.maskIndex,
            WearingMask = s.wearingMask,
        };
        if (s.wearableMeshes != null) foreach (var m in s.wearableMeshes) data.Meshes.Add(m);
        if (s.wearableMaterials != null) foreach (var m in s.wearableMaterials) data.Materials.Add(m);
        if (s.wearableTextures != null) foreach (var t in s.wearableTextures) data.Textures.Add(t);
        if (s.materialFloatSettingDefinitions != null)
        {
            foreach (var def in s.materialFloatSettingDefinitions)
            {
                var floats = new System.Collections.Generic.List<(string, float)>();
                if (def.pfloats != null) foreach (var p in def.pfloats) floats.Add((p.parameterName, p.value));
                data.MaterialFloats.Add((def.name, floats));
            }
        }
        return data;
    }

    private static SaveGame.SaveableCharacter ToSaveable(AppearanceData data)
    {
        var s = new SaveGame.SaveableCharacter
        {
            saveGameVersionID = data.VersionId,
            maskIndex = data.MaskIndex,
            wearingMask = data.WearingMask,
            wearableMeshes = ToList(data.Meshes),
            wearableMaterials = ToList(data.Materials),
            wearableTextures = ToList(data.Textures),
            materialFloatSettingDefinitions = new List<MaterialFloatDef>(),
        };
        foreach (var (name, floats) in data.MaterialFloats)
        {
            var def = new MaterialFloatDef { name = name, pfloats = new List<MaterialFloatDef.ParameterFloat>() };
            foreach (var (param, value) in floats)
                def.pfloats.Add(new MaterialFloatDef.ParameterFloat { parameterName = param, value = value });
            s.materialFloatSettingDefinitions.Add(def);
        }
        return s;
    }

    private static List<string> ToList(System.Collections.Generic.List<string> items)
    {
        var list = new List<string>();
        foreach (var i in items) list.Add(i);
        return list;
    }
}
