using System.Buffers.Binary;
using System.Text;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Launch;

public sealed class TerroristHuntEnemiesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vr-hunt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private const string Template = "Equipment.MP_TH_Equip01";

    private static readonly Dictionary<string, IReadOnlyList<string>> Arsenal = new()
    {
        ["R6Assault"] = ["R6AssaultAK47", "R6AssaultM8"],
        ["R6Sub"] = ["R6SubP90"],
        ["R6Sniper"] = ["R6SniperSV98", "R6SniperPSG1"],
        ["R6Pistol"] = ["R6PistolMK23", "R6PistolUSP40"],
    };

    private static TerroristHuntEnemies.Spec Spec(string file = "Maps/TH.uppc", string hash = "") => new(file, hash,
        [new(Template, [new("R6SniperSV98", 5, 1), new("R6Shield", 5, 0)])],
        Arsenal,
        [new("m_fScopeProbability", 0.5f), new("m_fSilencerProbability", 0.25f)]);

    [Theory]
    [InlineData(66)]
    [InlineData(71)]
    public void SpreadsEachCategoryOverTheWholeArsenal(int licensee)
    {
        var patched = TerroristHuntEnemies.Patch(TeamPackage.Build(licensee), Spec());

        Assert.NotNull(patched);
        var package = TerroristHuntEnemies.Package.Read(patched!)!;
        Assert.NotNull(package);
        var primary = List(package, "m_EquipmentPrimary");
        Assert.Equal(
            ["R6Game.R6AssaultAK47", "R6Game.R6AssaultM8", "R6Game.R6SubP90", "R6Game.R6PistolMK23", "R6Game.R6PistolUSP40",
                "R6Game.R6SniperSV98", "R6Game.R6SniperPSG1", "R6Game.R6Shield"],
            primary.Select(entry => entry.Weapon));
        // 45/45/10 scaled to make room for 5 + 5, then each category shared evenly.
        Assert.Equal([20.25f, 20.25f, 40.5f, 4.5f, 4.5f, 2.5f, 2.5f, 5f], primary.Select(entry => entry.Percent));
        Assert.Equal([1, 1, 1, 1, 1, 1, 1, 0], primary.Select(entry => (int)entry.Level));
        Assert.Equal(100f, primary.Sum(entry => entry.Percent), 3);

        var pistol = List(package, "m_EquipmentPistol");
        Assert.Equal(["R6Game.R6PistolMK23", "R6Game.R6PistolUSP40"], pistol.Select(entry => entry.Weapon));
        Assert.Equal([50f, 50f], pistol.Select(entry => entry.Percent));
    }

    [Theory]
    [InlineData(66)]
    [InlineData(71)]
    public void SetsTheAttachmentChancesAndKeepsOtherProperties(int licensee)
    {
        var package = TerroristHuntEnemies.Package.Read(TerroristHuntEnemies.Patch(TeamPackage.Build(licensee), Spec())!)!;

        Assert.Equal(0.5f, Float(package, "m_fScopeProbability"));
        Assert.Equal(0.25f, Float(package, "m_fSilencerProbability"));
        var data = package.Data[package.FindExport(Template)];
        var skill = package.Properties(data, 0, out var end).Single(tag => tag.Name == "m_iSkill");
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(skill.ValueAt)));
        Assert.Equal(data.Length, end);
    }

    [Fact]
    public void OverwritesAnExistingChance()
    {
        var package = TerroristHuntEnemies.Package.Read(TerroristHuntEnemies.Patch(TeamPackage.Build(66, scope: 0.1f), Spec())!)!;

        var data = package.Data[package.FindExport(Template)];
        Assert.Single(package.Properties(data, 0, out _), tag => tag.Name == "m_fScopeProbability");
        Assert.Equal(0.5f, Float(package, "m_fScopeProbability"));
    }

    [Theory]
    [InlineData(66)]
    [InlineData(71)]
    public void KeepsExportsTheGuidAndOtherObjects(int licensee)
    {
        var original = TeamPackage.Build(licensee);
        var patched = TerroristHuntEnemies.Patch(original, Spec())!;
        var before = TerroristHuntEnemies.Package.Read(original)!;
        var after = TerroristHuntEnemies.Package.Read(patched)!;

        Assert.Equal(before.Exports.Count, after.Exports.Count);
        // M8, USP40, SV98, PSG1 and the shield are new classes; m_fSilencerProbability is a new name.
        Assert.Equal(before.Imports.Count + 5, after.Imports.Count);
        Assert.Equal(before.Names.Count + 6, after.Names.Count);
        Assert.Equal(before.Names, after.Names.Take(before.Names.Count));
        Assert.Equal(before.Imports.Select(import => string.Join(',', import)),
            after.Imports.Take(before.Imports.Count).Select(import => string.Join(',', import)));
        Assert.Equal(before.Data[1], after.Data[1]);
        Assert.Equal(original.AsSpan(0x2c, 16).ToArray(), patched.AsSpan(0x2c, 16).ToArray());
        Assert.Equal(Template, after.ObjectPath(1));
    }

    [Fact]
    public void ReusesExistingClassImports()
    {
        var original = TeamPackage.Build(66);
        var spec = new TerroristHuntEnemies.Spec("Maps/TH.uppc", "", [new(Template, [new("R6SubP90", 10, 1)])],
            new Dictionary<string, IReadOnlyList<string>>(), []);
        var package = TerroristHuntEnemies.Package.Read(TerroristHuntEnemies.Patch(original, spec)!)!;

        Assert.Equal(TerroristHuntEnemies.Package.Read(original)!.Imports.Count, package.Imports.Count);
        Assert.Equal(2, List(package, "m_EquipmentPrimary").Count(entry => entry.Weapon == "R6Game.R6SubP90"));
    }

    [Fact]
    public void UnexpectedLayoutsAreLeftAlone()
    {
        var original = TeamPackage.Build(66);
        Assert.Null(TerroristHuntEnemies.Patch(original, Spec() with { Edits = [new("Equipment.Missing", [new("R6SniperSV98", 5, 1)])] }));
        Assert.Null(TerroristHuntEnemies.Patch(TeamPackage.Build(66, pistol: false), Spec()));
        Assert.Null(TerroristHuntEnemies.Patch(original[..^4], Spec()));
        var wrongVersion = (byte[])original.Clone();
        wrongVersion[4] = 0xf2;
        Assert.Null(TerroristHuntEnemies.Patch(wrongVersion, Spec()));
    }

    [Fact]
    public void ApplyRebuildsTheCopyFromThePinnedOriginalOnly()
    {
        var original = TeamPackage.Build(66);
        var spec = Spec(hash: Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(original)));
        var source = Path.Combine(_root, "source");
        var copy = Path.Combine(_root, "copy");
        Write(source, spec.File, original);
        Write(copy, spec.File, [1, 2, 3]);

        TerroristHuntEnemies.Apply(copy, source, spec);
        Assert.Equal(TerroristHuntEnemies.Patch(original, spec), File.ReadAllBytes(Path.Combine(copy, spec.File)));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(source, spec.File)));

        // Any other original is copied unchanged.
        var other = TeamPackage.Build(71);
        Write(source, spec.File, other);
        TerroristHuntEnemies.Apply(copy, source, spec);
        Assert.Equal(other, File.ReadAllBytes(Path.Combine(copy, spec.File)));
    }

    [Fact]
    public void BothTitlesListTheFixAndAPinnedPackage()
    {
        foreach (var title in TitleCatalog.All)
        {
            Assert.NotNull(title.Fix(TitleCatalog.HuntEnemies));
            var spec = TerroristHuntEnemies.For(title)!;
            Assert.NotEmpty(spec.Edits);
            Assert.All(spec.Edits, edit => Assert.InRange(edit.Additions.Sum(addition => addition.Percent), 1f, 20f));
            Assert.Contains("R6Pistol", spec.Arsenal.Keys);
            Assert.All(spec.Arsenal, category => Assert.All(category.Value, weapon => Assert.StartsWith(category.Key, weapon)));
            Assert.All(spec.Chances, chance => Assert.InRange(chance.Value, 0f, 1f));
            Assert.Contains(spec.Chances, chance => chance.Property == "m_fScopeProbability");
        }
    }

    private static void Write(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static float Float(TerroristHuntEnemies.Package package, string property)
    {
        var data = package.Data[package.FindExport(Template)];
        var tag = package.Properties(data, 0, out _).Single(tag => tag.Name == property);
        Assert.Equal("FloatProperty", tag.Type);
        return BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(tag.ValueAt));
    }

    private static List<(string Weapon, float Percent, byte Level)> List(TerroristHuntEnemies.Package package, string list)
    {
        var data = package.Data[package.FindExport(Template)];
        var array = package.Properties(data, 0, out _).Single(tag => tag.Name == list);
        var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(array.ValueAt));
        var p = array.ValueAt + 4;
        var result = new List<(string, float, byte)>();
        for (var i = 0; i < count; i++)
        {
            var tags = package.Properties(data, p, out p).ToDictionary(tag => tag.Name);
            result.Add((package.ObjectPath(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(tags["m_Inventory"].ValueAt))),
                BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(tags["m_fPercent"].ValueAt)),
                data[tags["m_eAttachmentLevel"].ValueAt]));
        }
        Assert.Equal(array.ValueAt + array.Size, p);
        return result;
    }

    /// <summary>A synthetic team package in the shipped layout (no game data).</summary>
    private static class TeamPackage
    {
        private static readonly string[] Names =
        [
            "None", "ObjectProperty", "FloatProperty", "ByteProperty", "ArrayProperty", "m_Inventory", "m_fPercent",
            "m_eAttachmentLevel", "m_EquipmentPrimary", "Core", "Class", "Package", "R6Game", "R6EquipmentTemplate",
            "R6AssaultAK47", "R6SubP90", "R6PistolMK23", "Equipment", "MP_TH_Equip01", "IntProperty", "m_iSkill",
            "m_EquipmentPistol", "m_fScopeProbability",
        ];

        private static int N(string name) => Array.IndexOf(Names, name);

        private static byte[] List(string property, (int Weapon, float Percent)[] entries)
        {
            var elements = new MemoryStream();
            Ints(elements, entries.Length);
            foreach (var (weapon, percent) in entries)
            {
                Ints(elements, N("m_Inventory"), N("ObjectProperty"), 4, 0, weapon);
                Ints(elements, N("m_fPercent"), N("FloatProperty"), 4, 0, BitConverter.SingleToInt32Bits(percent));
                Ints(elements, N("m_eAttachmentLevel"), N("ByteProperty"), 1, 0);
                elements.WriteByte(1);
                Ints(elements, N("None"));
            }
            var result = new MemoryStream();
            Ints(result, N(property), N("ArrayProperty"), (int)elements.Length, 0);
            elements.WriteTo(result);
            return result.ToArray();
        }

        public static byte[] Build(int licensee, bool pistol = true, float? scope = null)
        {
            // Imports: -1 R6Game package, -2..-4 weapon classes, -5 R6EquipmentTemplate class, -6 Core.Package class.
            int[][] imports =
            [
                [N("Core"), N("Package"), 0, N("R6Game")],
                [N("Core"), N("Class"), -1, N("R6AssaultAK47")],
                [N("Core"), N("Class"), -1, N("R6SubP90")],
                [N("Core"), N("Class"), -1, N("R6PistolMK23")],
                [N("Core"), N("Class"), -1, N("R6EquipmentTemplate")],
                [N("Core"), N("Class"), 0, N("Package")],
            ];
            var template = new MemoryStream();
            if (pistol)
            {
                template.Write(List("m_EquipmentPistol", [(-4, 100f)]));
            }
            template.Write(List("m_EquipmentPrimary", [(-2, 45f), (-3, 45f), (-4, 10f)]));
            if (scope is { } chance)
            {
                Ints(template, N("m_fScopeProbability"), N("FloatProperty"), 4, 0, BitConverter.SingleToInt32Bits(chance));
            }
            Ints(template, N("m_iSkill"), N("IntProperty"), 4, 0, 3, N("None"));
            byte[][] data = [template.ToArray(), [1, 0, 0, 0]];
            int[][] exports =
            [
                [-5, 0, 2, N("MP_TH_Equip01"), 0, 0, 0x000f0004, 0, 0, 0],
                [-6, 0, 0, N("Equipment"), 0, 0, 0x00070004, 0, 0, 0],
            ];

            var names = new MemoryStream();
            foreach (var name in Names)
            {
                var bytes = Encoding.Latin1.GetBytes(name);
                if (licensee == 71)
                {
                    names.WriteByte((byte)bytes.Length);
                    names.Write(bytes);
                }
                else
                {
                    Ints(names, bytes.Length + 1);
                    names.Write(bytes);
                    names.WriteByte(0);
                    Ints(names, 0, 0x00070010);
                }
            }
            var output = new MemoryStream();
            output.Write(new byte[0x48]);
            names.WriteTo(output);
            for (var i = 0; i < exports.Length; i++)
            {
                exports[i][7] = data[i].Length;
                exports[i][8] = (int)output.Position;
                output.Write(data[i]);
            }
            var importOffset = (int)output.Position;
            foreach (var import in imports)
            {
                Ints(output, import);
            }
            var exportOffset = (int)output.Position;
            foreach (var export in exports)
            {
                Ints(output, export);
            }
            var result = output.ToArray();
            var header = new MemoryStream();
            Ints(header, unchecked((int)0x9e2a83c1), 241 | (licensee << 16), 0, licensee == 71 ? 15 : 14, 9,
                Names.Length, 0x48, exports.Length, exportOffset, imports.Length, importOffset,
                0x11111111, 0x22222222, 0x33333333, 0x44444444, 1, exports.Length, Names.Length);
            header.ToArray().CopyTo(result, 0);
            return result;
        }

        private static void Ints(Stream stream, params int[] values)
        {
            foreach (var value in values)
            {
                stream.Write(BitConverter.GetBytes(value));
            }
        }
    }
}
