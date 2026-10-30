using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using LiteNetLib.Utils;

namespace RekaCoop.Appearance;

// A character's look, in the same shape as the game's Character.xml (SaveGame.SaveableCharacter):
// wearable IDs plus per-material float sliders. No Unity or game types, so it can be stored and sent freely.
public class AppearanceData
{
    public string VersionId = "";
    public List<string> Meshes = new();
    public List<string> Materials = new();
    public List<string> Textures = new();
    public List<(string Name, List<(string Param, float Value)> Floats)> MaterialFloats = new();
    public int MaskIndex;
    public bool WearingMask;

    // Stable text form, used for the profile file and for change detection.
    public string ToXml()
    {
        static XElement Strings(string name, IEnumerable<string> items) => new(name, items.Select(s => new XElement("string", s)));
        var root = new XElement("SaveableCharacter",
            new XElement("saveGameVersionID", VersionId),
            Strings("wearableMeshes", Meshes),
            Strings("wearableMaterials", Materials),
            Strings("wearableTextures", Textures),
            new XElement("materialFloatSettingDefinitions", MaterialFloats.Select(m =>
                new XElement("MaterialFloatSettingDefinition",
                    new XElement("name", m.Name),
                    new XElement("pfloats", m.Floats.Select(f =>
                        new XElement("ParameterFloat",
                            new XElement("parameterName", f.Param),
                            new XElement("value", f.Value.ToString("R", CultureInfo.InvariantCulture)))))))),
            new XElement("maskIndex", MaskIndex),
            new XElement("wearingMask", WearingMask ? "true" : "false"));
        return root.ToString();
    }

    // Reads both our profile files and the game's own Character.xml (which carries XML namespaces).
    public static AppearanceData FromXml(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        XElement El(XElement parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        List<string> Strings(string name) => El(root, name)?.Elements().Select(e => e.Value).ToList() ?? new List<string>();

        var data = new AppearanceData
        {
            VersionId = El(root, "saveGameVersionID")?.Value ?? "",
            Meshes = Strings("wearableMeshes"),
            Materials = Strings("wearableMaterials"),
            Textures = Strings("wearableTextures"),
            MaskIndex = int.TryParse(El(root, "maskIndex")?.Value, out int mask) ? mask : 0,
            WearingMask = El(root, "wearingMask")?.Value == "true",
        };
        foreach (var def in El(root, "materialFloatSettingDefinitions")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            var floats = (El(def, "pfloats")?.Elements() ?? Enumerable.Empty<XElement>())
                .Select(p => (El(p, "parameterName")?.Value ?? "",
                              float.Parse(El(p, "value")?.Value ?? "0", CultureInfo.InvariantCulture)))
                .ToList();
            data.MaterialFloats.Add((El(def, "name")?.Value ?? "", floats));
        }
        return data;
    }

    public void Write(NetDataWriter w) => w.Put(ToXml());

    public static AppearanceData Read(NetDataReader r) => FromXml(r.GetString(ushort.MaxValue));
}
