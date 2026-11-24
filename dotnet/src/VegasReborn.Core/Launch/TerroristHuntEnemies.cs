using System.Buffers.Binary;
using System.Text;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Launch;

/// <summary>
/// Terrorist Hunt equipment in the copy's shared team package. Every Terrorist Hunt spawn
/// point names one R6EquipmentTemplate from this package. The game draws each terrorist's
/// primary weapon and pistol from the template's m_EquipmentPrimary and m_EquipmentPistol
/// lists (a float weighted pick over m_fPercent), then rolls the template's attachment
/// chances once per terrorist and applies them to both weapons: m_fSilencerProbability adds
/// a suppressor where the weapon takes one, m_fLaserProbability (Vegas 2) a laser where the
/// weapon takes one, otherwise m_fScopeProbability a random scope, otherwise no attachment.
/// Those rolls override the per-entry attachment levels. The shipped Terrorist Hunt
/// templates set none of the chances, so their terrorists never carry attachments.
///
/// This adds the campaign's sniper rifles (both games) and, in Vegas 2, the riot shield
/// (EquipmentTemplates.Shields, as the Convention Center shield terrorists use it), spreads
/// each weapon category's weight evenly over every weapon the game has in that category,
/// gives the pistol slot every pistol, and sets the attachment chances. Weights are scaled so
/// each primary list totals exactly 100.
/// The package is always rebuilt from the installed original, and only the pinned original
/// is changed: names and class imports are appended, no export is added or removed, so
/// object indexes, the package GUID and the export count stay as shipped.
/// </summary>
internal static class TerroristHuntEnemies
{
    internal sealed record Addition(string WeaponClass, float Percent, byte AttachmentLevel);

    internal sealed record Edit(string Template, IReadOnlyList<Addition> Additions);

    internal sealed record Chance(string Property, float Value);

    /// <summary>
    /// <paramref name="Arsenal"/> lists every weapon of a category, keyed by class-name prefix.
    /// List entries of that category are replaced by all of them.
    /// </summary>
    internal sealed record Spec(string File, string OriginalSha256, IReadOnlyList<Edit> Edits,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Arsenal, IReadOnlyList<Chance> Chances);

    private const string Primary = "m_EquipmentPrimary";
    private const string Pistol = "m_EquipmentPistol";

    // Weapons with attachment options in KellerGame/Config/PCKellerWeaponsConfig.ini, less the
    // fixed mounted guns. Each one is carried by a shipped campaign equipment template or is a
    // multiplayer loadout weapon.
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Vegas1Arsenal =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["R6Assault"] = ["R6Assault552Commando", "R6AssaultAK47", "R6AssaultAUGA3", "R6AssaultFamas", "R6AssaultG36C",
                "R6AssaultG3KA4", "R6AssaultM8", "R6AssaultMTAR21", "R6AssaultSCARHCQC"],
            ["R6Sub"] = ["R6SubMAC11", "R6SubMP5N", "R6SubMP7A1", "R6SubMP9", "R6SubP90", "R6SubUMP45"],
            ["R6LMG"] = ["R6LMG21E", "R6LMGM249SPW", "R6LMGMG36", "R6LMGMK46"],
            ["R6Shotgun"] = ["R6Shotgun870MCS", "R6ShotgunM3", "R6ShotgunSpas12", "R6ShotgunXM26LSS"],
            ["R6Sniper"] = ["R6SniperM40A1", "R6SniperPSG1", "R6SniperSV98", "R6SniperScoutTactical"],
            ["R6Pistol"] = ["R6Pistol92FS", "R6PistolDesertEagle", "R6PistolGlock18", "R6PistolMK23", "R6PistolRagingBull",
                "R6PistolUSP40"],
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Vegas2Arsenal =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["R6Assault"] = ["R6Assault552Commando", "R6AssaultAK47", "R6AssaultAKS74U", "R6AssaultAUGA3", "R6AssaultFNC",
                "R6AssaultFamas", "R6AssaultG36C", "R6AssaultG3KA4", "R6AssaultL85A2", "R6AssaultM468", "R6AssaultM8",
                "R6AssaultMTAR21", "R6AssaultSCARHCQC"],
            ["R6Sub"] = ["R6SubAUGPara", "R6SubMAC11", "R6SubMP5N", "R6SubMP7A1", "R6SubMP9", "R6SubP90", "R6SubType05JS",
                "R6SubUMP45", "R6SubVZ83"],
            ["R6LMG"] = ["R6LMG21E", "R6LMGM249SPW", "R6LMGMG36", "R6LMGMK46"],
            ["R6Shotgun"] = ["R6Shotgun500Tactical", "R6Shotgun870MCS", "R6ShotgunM3", "R6ShotgunSpas12", "R6ShotgunXM26LSS"],
            ["R6Sniper"] = ["R6SniperL96ArticWarfare", "R6SniperM40A1", "R6SniperPSG1", "R6SniperSR25SD", "R6SniperSV98",
                "R6SniperScoutTactical"],
            ["R6Pistol"] = ["R6Pistol92FS", "R6PistolDesertEagle", "R6PistolGlock18", "R6PistolMK23", "R6PistolP99",
                "R6PistolRagingBull", "R6PistolUSP40"],
        };

    public static Spec Vegas1 { get; } = new(
        "KellerGame/Content/CookedPc/Maps/00_MP_Common/00_MP_TH_Teams.uppc",
        "db676b8660aaef5a0a43c2346bac7a8445d4d39fbb7740e69aa3412215efb5ea",
        [
            // Shared by every Vegas 1 Terrorist Hunt map. Both rifles are campaign sniper
            // loadouts (RS5_EquipmentTemplates *_Sniper).
            new("Equipment.MP_TH_Equip01", [new("R6SniperSV98", 5, 1), new("R6SniperPSG1", 5, 1)]),
        ],
        Vegas1Arsenal,
        // Vegas 1 templates have no laser chance. Campaign templates use scope chances from 0 to 1.
        [new("m_fScopeProbability", 0.5f), new("m_fSilencerProbability", 0.35f)]);

    public static Spec Vegas2 { get; } = new(
        "KellerGame/CookedPC/MapsBishop/Mb00_Common/Mb00_TH_Teams.uppc",
        "17a5b9fb0e92ee4dd0465eb9e022b0662a43622740f32d748a0c36f5b8117e3e",
        [
            // Rifles from EquipmentTemplates_Bishop campaign sniper templates; the shield as
            // EquipmentTemplates.Shields lists it (attachment level 0).
            new("Equipment.TH_Military_01", [new("R6SniperL96ArticWarfare", 6, 1), new("R6Shield", 6, 0)]),
            new("Equipment.TH_Guerrilla_01", [new("R6SniperSV98", 6, 1), new("R6Shield", 6, 0)]),
            new("Equipment.TH_Mercs_01", [new("R6SniperScoutTactical", 6, 1), new("R6Shield", 6, 0)]),
            new("Equipment.MP_TH_Equip01", [new("R6SniperM40A1", 6, 1), new("R6Shield", 6, 0)]),
        ],
        Vegas2Arsenal,
        // On a weapon that takes a laser, a laser roll replaces the scope roll.
        [new("m_fScopeProbability", 0.5f), new("m_fSilencerProbability", 0.35f), new("m_fLaserProbability", 0.35f)]);

    public static Spec? For(GameTitle title) =>
        title.Key == TitleCatalog.Vegas1.Key ? Vegas1 : title.Key == TitleCatalog.Vegas2.Key ? Vegas2 : null;

    /// <summary>Write the copy's team package from the installed original.</summary>
    public static void Apply(string copy, string source, Spec spec)
    {
        var original = Path.Combine(source, spec.File);
        var target = Path.Combine(copy, spec.File);
        if (!File.Exists(original) || !File.Exists(target) || !PrivateFile.IsWithin(target, copy))
        {
            return;
        }
        var bytes = File.ReadAllBytes(original);
        var expected = PrivateFile.Sha256(bytes) == spec.OriginalSha256 ? Patch(bytes, spec) ?? bytes : bytes;
        if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(expected))
        {
            PrivateFile.Write(target, expected);
        }
    }

    /// <summary>The package with every edit applied, or null when its layout is not the expected one.</summary>
    internal static byte[]? Patch(byte[] original, Spec spec)
    {
        try
        {
            var package = Package.Read(original);
            if (package is null)
            {
                return null;
            }
            foreach (var edit in spec.Edits)
            {
                if (!package.RewriteList(edit.Template, Primary, entries => Spread(Add(entries, edit.Additions), spec.Arsenal))
                    || !package.RewriteList(edit.Template, Pistol, entries => Spread(entries, spec.Arsenal))
                    || spec.Chances.Any(chance => !package.SetFloat(edit.Template, chance.Property, chance.Value)))
                {
                    return null;
                }
            }
            return package.Write();
        }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>One equipment list entry: an R6Game class name (or None), its weight and attachment level.</summary>
    internal sealed record Entry(string Weapon, double Weight, byte Level);

    /// <summary>Appends <paramref name="additions"/>, scaling the existing weights so the list totals 100.</summary>
    internal static List<Entry>? Add(List<Entry> entries, IReadOnlyList<Addition> additions)
    {
        var total = entries.Sum(entry => entry.Weight);
        var added = additions.Sum(addition => (double)addition.Percent);
        if (!(total > 0) || !(added > 0) || added >= 100)
        {
            return null;
        }
        var scale = (100 - added) / total;
        return [.. entries.Select(entry => entry with { Weight = entry.Weight * scale }),
            .. additions.Select(addition => new Entry(addition.WeaponClass, addition.Percent, addition.AttachmentLevel))];
    }

    /// <summary>
    /// Replaces the entries of each arsenal category with every weapon of that category,
    /// sharing the category's total weight evenly, at the category's first attachment level.
    /// A category takes the position of its first entry; other entries are kept as they are.
    /// </summary>
    internal static List<Entry>? Spread(List<Entry>? entries, IReadOnlyDictionary<string, IReadOnlyList<string>> arsenal)
    {
        if (entries is null)
        {
            return null;
        }
        string? Category(string weapon) => arsenal.Keys.FirstOrDefault(prefix => weapon.StartsWith(prefix, StringComparison.Ordinal));
        var result = new List<Entry>();
        var done = new HashSet<string>();
        foreach (var entry in entries)
        {
            if (Category(entry.Weapon) is not { } category)
            {
                result.Add(entry);
                continue;
            }
            if (!done.Add(category))
            {
                continue;
            }
            var weight = entries.Where(other => Category(other.Weapon) == category).Sum(other => other.Weight);
            var weapons = arsenal[category];
            result.AddRange(weapons.Select(weapon => new Entry(weapon, weight / weapons.Count, entry.Level)));
        }
        return result;
    }

    /// <summary>
    /// The small uncompressed package layout these files ship in (file version 241; licensee 66
    /// is Vegas 1, 71 is Vegas 2): header, names, export data, imports, export table.
    /// </summary>
    internal sealed class Package
    {
        private const int NameCountAt = 0x14, NameOffsetAt = 0x18, ExportCountAt = 0x1c, ExportOffsetAt = 0x20;
        private const int ImportCountAt = 0x24, ImportOffsetAt = 0x28, GenerationsAt = 0x3c, HeaderSize = 0x48;
        private const ulong Vegas1NameFlags = 0x0007001000000000;

        private readonly byte[] _header;
        private readonly bool _shortNames;
        private readonly int _originalNames;
        private readonly byte[] _originalNameTable;

        public List<string> Names { get; } = [];
        public List<int[]> Imports { get; } = [];
        public List<int[]> Exports { get; } = [];
        public List<byte[]> ExportExtras { get; } = [];
        public List<byte[]> Data { get; } = [];

        private Package(byte[] header, bool shortNames, byte[] nameTable, int names)
        {
            _header = header;
            _shortNames = shortNames;
            _originalNameTable = nameTable;
            _originalNames = names;
        }

        public static Package? Read(byte[] d)
        {
            if (d.Length < HeaderSize || I(d, 0) != unchecked((int)0x9e2a83c1) || U16(d, 4) != 241)
            {
                return null;
            }
            var licensee = U16(d, 6);
            if (licensee is not (66 or 71))
            {
                return null;
            }
            int nameCount = I(d, NameCountAt), nameOffset = I(d, NameOffsetAt), exportCount = I(d, ExportCountAt),
                exportOffset = I(d, ExportOffsetAt), importCount = I(d, ImportCountAt), importOffset = I(d, ImportOffsetAt);
            // One generation that matches the tables; the name table follows it directly.
            if (I(d, GenerationsAt) != 1 || I(d, GenerationsAt + 4) != exportCount || I(d, GenerationsAt + 8) != nameCount
                || nameOffset != HeaderSize || nameCount <= 0 || exportCount <= 0 || importCount <= 0)
            {
                return null;
            }
            var p = nameOffset;
            var names = new List<string>();
            for (var i = 0; i < nameCount; i++)
            {
                if (licensee == 71)
                {
                    int length = d[p];
                    names.Add(Encoding.Latin1.GetString(d, p + 1, length));
                    p += 1 + length;
                }
                else
                {
                    var length = I(d, p);
                    if (length <= 0 || d[p + 4 + length - 1] != 0)
                    {
                        return null;
                    }
                    names.Add(Encoding.Latin1.GetString(d, p + 4, length - 1));
                    p += 4 + length + 8;
                }
            }
            var package = new Package(d[..HeaderSize], licensee == 71, d[nameOffset..p], nameCount);
            package.Names.AddRange(names);
            if (importOffset + importCount * 16 != exportOffset)
            {
                return null;
            }
            for (var i = 0; i < importCount; i++)
            {
                package.Imports.Add([I(d, importOffset + 16 * i), I(d, importOffset + 16 * i + 4),
                    I(d, importOffset + 16 * i + 8), I(d, importOffset + 16 * i + 12)]);
            }
            var e = exportOffset;
            for (var i = 0; i < exportCount; i++)
            {
                var entry = new int[10];
                for (var j = 0; j < 10; j++)
                {
                    entry[j] = I(d, e + 4 * j);
                }
                var components = entry[9];
                if (components < 0 || components > 64)
                {
                    return null;
                }
                package.Exports.Add(entry);
                package.ExportExtras.Add(d[(e + 40)..(e + 40 + 8 * components)]);
                e += 40 + 8 * components;
            }
            if (e != d.Length)
            {
                return null;
            }
            // Export data is contiguous, in table order, between the names and the imports.
            var at = p;
            foreach (var entry in package.Exports)
            {
                if (entry[8] != at || entry[7] < 0)
                {
                    return null;
                }
                package.Data.Add(d[at..(at + entry[7])]);
                at += entry[7];
            }
            return at == importOffset ? package : null;
        }

        public byte[] Write()
        {
            var output = new MemoryStream();
            output.Write(_header);
            output.Write(_originalNameTable);
            Span<byte> value = stackalloc byte[8];
            foreach (var name in Names.Skip(_originalNames))
            {
                var bytes = Encoding.Latin1.GetBytes(name);
                if (_shortNames)
                {
                    output.WriteByte(checked((byte)bytes.Length));
                    output.Write(bytes);
                }
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(value, bytes.Length + 1);
                    output.Write(value[..4]);
                    output.Write(bytes);
                    output.WriteByte(0);
                    BinaryPrimitives.WriteUInt64LittleEndian(value, Vegas1NameFlags);
                    output.Write(value);
                }
            }
            for (var i = 0; i < Exports.Count; i++)
            {
                Exports[i][7] = Data[i].Length;
                Exports[i][8] = (int)output.Position;
                output.Write(Data[i]);
            }
            var importOffset = (int)output.Position;
            foreach (var import in Imports)
            {
                WriteInts(output, import);
            }
            var exportOffset = (int)output.Position;
            for (var i = 0; i < Exports.Count; i++)
            {
                WriteInts(output, Exports[i]);
                output.Write(ExportExtras[i]);
            }
            var result = output.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(NameCountAt), Names.Count);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(ExportOffsetAt), exportOffset);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(ImportCountAt), Imports.Count);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(ImportOffsetAt), importOffset);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(GenerationsAt + 8), Names.Count);
            return result;
        }

        /// <summary>Name of a name reference (Vegas 2 packs an instance number in the high 16 bits).</summary>
        public string Name(int reference) =>
            (reference >> 16) == 0 ? Names[reference & 0xffff] : throw new InvalidDataException("numbered name");

        public string ObjectPath(int reference)
        {
            if (reference == 0)
            {
                return "None";
            }
            var (name, outer) = reference < 0
                ? (Imports[-reference - 1][3], Imports[-reference - 1][2])
                : (Exports[reference - 1][3], Exports[reference - 1][2]);
            return (outer != 0 ? ObjectPath(outer) + "." : "") + Name(name);
        }

        private int NameIndex(string name)
        {
            var index = Names.IndexOf(name);
            if (index < 0)
            {
                Names.Add(name);
                index = Names.Count - 1;
            }
            return index;
        }

        /// <summary>The import of R6Game class <paramref name="name"/>, added beside the existing R6Game classes.</summary>
        private int ClassImport(string name)
        {
            var template = Imports.FirstOrDefault(import => Name(import[1]) == "Class" && import[2] < 0
                && Name(Imports[-import[2] - 1][3]) == "R6Game" && Imports[-import[2] - 1][2] == 0)
                ?? throw new InvalidDataException("no R6Game class import");
            for (var i = 0; i < Imports.Count; i++)
            {
                if (Imports[i][0] == template[0] && Imports[i][1] == template[1] && Imports[i][2] == template[2]
                    && Name(Imports[i][3]) == name)
                {
                    return -i - 1;
                }
            }
            Imports.Add([template[0], template[1], template[2], NameIndex(name)]);
            return -Imports.Count;
        }

        internal sealed record Tag(string Name, string Type, int SizeAt, int ValueAt, int Size);

        /// <summary>Tagged properties from <paramref name="start"/> up to and including None.</summary>
        internal List<Tag> Properties(byte[] data, int start, out int end)
        {
            var tags = new List<Tag>();
            var p = start;
            while (true)
            {
                var name = Name(I(data, p));
                if (name == "None")
                {
                    end = p + 4;
                    return tags;
                }
                var type = Name(I(data, p + 4));
                if (type is not ("ObjectProperty" or "FloatProperty" or "IntProperty" or "ByteProperty" or "ArrayProperty" or "NameProperty"))
                {
                    throw new InvalidDataException("unsupported property type " + type);
                }
                var size = I(data, p + 8);
                if (size < 0 || p + 16 + size > data.Length)
                {
                    throw new InvalidDataException("property outside the object");
                }
                tags.Add(new Tag(name, type, p + 8, p + 16, size));
                p += 16 + size;
            }
        }

        public int FindExport(string path)
        {
            for (var i = 0; i < Exports.Count; i++)
            {
                if (ObjectPath(i + 1) == path)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>The template's tagged properties, or null when it is not a template in the expected form.</summary>
        private (int Export, byte[] Data, List<Tag> Tags)? Template(string template)
        {
            var export = FindExport(template);
            if (export < 0 || ObjectPath(Exports[export][0]) != "R6Game.R6EquipmentTemplate")
            {
                return null;
            }
            var data = Data[export];
            var tags = Properties(data, 0, out var objectEnd);
            return objectEnd == data.Length ? (export, data, tags) : null;
        }

        /// <summary>
        /// Replaces an equipment list with <paramref name="transform"/> of its entries. Each written
        /// element is a copy of the first one with its inventory, weight and level replaced.
        /// False when the template or list is not present in the expected form.
        /// </summary>
        public bool RewriteList(string template, string list, Func<List<Entry>, List<Entry>?> transform)
        {
            if (Template(template) is not var (export, data, tags))
            {
                return false;
            }
            var array = tags.FirstOrDefault(tag => tag.Name == list && tag.Type == "ArrayProperty");
            if (array is null)
            {
                return false;
            }
            var count = I(data, array.ValueAt);
            var arrayEnd = array.ValueAt + array.Size;
            var p = array.ValueAt + 4;
            var elements = new List<(int Start, int End, Dictionary<string, Tag> Tags)>();
            for (var i = 0; i < count; i++)
            {
                var element = Properties(data, p, out var end).ToDictionary(tag => tag.Name);
                if (end > arrayEnd)
                {
                    return false;
                }
                elements.Add((p, end, element));
                p = end;
            }
            if (count <= 0 || p != arrayEnd || elements.Any(element =>
                    !element.Tags.TryGetValue("m_Inventory", out var inventory) || inventory.Size != 4
                    || !element.Tags.TryGetValue("m_fPercent", out var percent) || percent.Size != 4
                    || !element.Tags.TryGetValue("m_eAttachmentLevel", out var level) || level.Size != 1))
            {
                return false;
            }
            var entries = new List<Entry>();
            foreach (var element in elements)
            {
                var weapon = ObjectPath(I(data, element.Tags["m_Inventory"].ValueAt));
                if (weapon != "None" && !weapon.StartsWith("R6Game.", StringComparison.Ordinal))
                {
                    return false;
                }
                entries.Add(new Entry(weapon == "None" ? weapon : weapon["R6Game.".Length..],
                    BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(element.Tags["m_fPercent"].ValueAt)),
                    data[element.Tags["m_eAttachmentLevel"].ValueAt]));
            }
            var rewritten = transform(entries);
            if (rewritten is null || rewritten.Count == 0)
            {
                return false;
            }
            var body = new MemoryStream();
            WriteInts(body, [rewritten.Count]);
            foreach (var entry in rewritten)
            {
                body.Write(Element(data, elements[0], entry.Weapon == "None" ? 0 : ClassImport(entry.Weapon),
                    (float)entry.Weight, entry.Level));
            }
            var result = Splice(data, array.ValueAt, arrayEnd, body.ToArray());
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(array.SizeAt), (int)body.Length);
            Data[export] = result;
            return true;
        }

        /// <summary>Sets a float property on the template, adding it before None when absent.</summary>
        public bool SetFloat(string template, string property, float value)
        {
            if (Template(template) is not var (export, data, tags))
            {
                return false;
            }
            var existing = tags.FirstOrDefault(tag => tag.Name == property);
            if (existing is not null)
            {
                if (existing.Type != "FloatProperty" || existing.Size != 4)
                {
                    return false;
                }
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(existing.ValueAt), value);
                return true;
            }
            var tag = new MemoryStream();
            WriteInts(tag, [NameIndex(property), NameIndex("FloatProperty"), 4, 0, BitConverter.SingleToInt32Bits(value)]);
            Data[export] = Splice(data, data.Length - 4, data.Length - 4, tag.ToArray());
            return true;
        }

        private static byte[] Splice(byte[] data, int from, int to, byte[] replacement)
        {
            var result = new byte[data.Length - (to - from) + replacement.Length];
            data.AsSpan(0, from).CopyTo(result);
            replacement.CopyTo(result, from);
            data.AsSpan(to).CopyTo(result.AsSpan(from + replacement.Length));
            return result;
        }

        private static byte[] Element(byte[] data, (int Start, int End, Dictionary<string, Tag> Tags) element,
            int inventory, float percent, byte level)
        {
            var bytes = data[element.Start..element.End];
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(element.Tags["m_fPercent"].ValueAt - element.Start), percent);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(element.Tags["m_Inventory"].ValueAt - element.Start), inventory);
            bytes[element.Tags["m_eAttachmentLevel"].ValueAt - element.Start] = level;
            return bytes;
        }

        private static void WriteInts(Stream output, int[] values)
        {
            Span<byte> value = stackalloc byte[4];
            foreach (var item in values)
            {
                BinaryPrimitives.WriteInt32LittleEndian(value, item);
                output.Write(value);
            }
        }

        private static int I(byte[] d, int at) => at < 0 || at + 4 > d.Length
            ? throw new InvalidDataException("read outside the package")
            : BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));

        private static int U16(byte[] d, int at) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
    }
}
