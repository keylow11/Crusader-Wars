using Xunit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CrusaderWars.data.save_file;

namespace CrusaderWars.tests
{
    [CollectionDefinition("DragonSystem", DisableParallelization = true)]
    public class DragonSystemCollection { }

    /// <summary>
    /// Rider-dragon links and save cleanup, using character blocks in the format of a real AGOT save
    /// (Amax rides Daevour, Rhaegar rides Firkrax, Daenerys has the rider trait but no dragon).
    /// </summary>
    [Collection("DragonSystem")]
    public class DragonSystemTests : IDisposable
    {
        const int DRAGONRIDER = 448, DRAGONLESS = 449, MAIMED = 50;

        readonly string _previousDirectory;
        readonly string _testDirectory;

        static string Variable(string flag, string identity, bool first = false) =>
            (first ? "" : " {\n") +
            $"\t\t\t\t\tflag={flag}\n\t\t\t\t\tdata={{\n\t\t\t\t\t\ttype=char\n\t\t\t\t\t\tidentity={identity}\n\t\t\t\t\t}}\n\n\t\t\t\t}}\n";

        static string ValueVariable(string flag, bool first = false) =>
            (first ? "" : " {\n") +
            $"\t\t\t\t\tflag={flag}\n\t\t\t\t\tdata={{\n\t\t\t\t\t}}\n\n\t\t\t\t}}\n";

        static string CharList(string name, params string[] ids) =>
            $"\t\t\t\t\tname=\"{name}\"\n" +
            string.Concat(ids.Select(id => $"\t\t\t\t\titem={{\n\t\t\t\t\t\ttype=char\n\t\t\t\t\t\tidentity={id}\n\t\t\t\t\t}}\n\n")) +
            $"\t\t\t\t\tduration={{ {ids.Length} }}\n\t\t\t\t}}\n";

        static string Character(string id, string name, string traits, string variables, string? lists = null) =>
            $"{id}={{\n\tfirst_name=\"{name}\"\n\ttraits={{ {traits} }}\n\talive_data={{\n\t\tvariables={{\n\t\t\tdata={{ {{\n{variables} }}\n" +
            (lists != null ? $"\t\t\tlist={{ {{\n{lists} }}\n" : "") +
            "\t\t}\n\t\thealth=4.7\n\t}\n}\n";

        static string Amax() => Character("134712", "Amax", $"12 {DRAGONRIDER} 30",
            Variable("current_dragon", "135010", first: true) + ValueVariable("gene_dragon_wounded"));

        static string Daevour(string? lists = null) => Character("135010", "Daevour", "447 458",
            ValueVariable("owned_dragon", first: true) + Variable("current_rider", "134712") + ValueVariable("in_dragonpit"),
            lists ?? CharList("current_rider_list", "134712"));

        static string Rhaegar() => Character("33697978", "Rhaegar", $"{DRAGONRIDER} 9",
            ValueVariable("owned_dragon_rider", first: true) + Variable("current_dragon", "16908022"));

        static string Firkrax() => Character("16908022", "Firkrax", "447",
            Variable("current_rider", "33697978", first: true), CharList("current_rider_list", "33697978"));

        static string Daenerys() => Character("135498", "Daenerys", $"5 {DRAGONRIDER}",
            ValueVariable("head_dragonkeeper", first: true));

        public DragonSystemTests()
        {
            _previousDirectory = Directory.GetCurrentDirectory();
            _testDirectory = Path.Combine(Path.GetTempPath(), "cw_dragon_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_testDirectory, "data", "save_file_data"));
            Directory.SetCurrentDirectory(_testDirectory);

            File.WriteAllText(Writter.DataFilesPaths.Living_Path(),
                Amax() + Daevour() + Rhaegar() + Firkrax() + Daenerys());

            ArmiesReader.save_file_traits = new List<(string name, int index)>
            {
                ("maimed", MAIMED), ("dragon", 447), ("dragonrider", DRAGONRIDER), ("dragonless_dragonrider", DRAGONLESS)
            };
            SetStatic("RiderTraitKeys", new List<string> { "dragonrider" });
            SetStatic("UnitKey", "test_dragon_unit");
            SetStatic("LoadedLinksKey", null);
            DragonSystem.ResetBattleResults();
        }

        public void Dispose()
        {
            DragonSystem.ResetBattleResults();
            SetStatic("RiderTraitKeys", new List<string>());
            SetStatic("UnitKey", string.Empty);
            SetStatic("LoadedLinksKey", null);
            Directory.SetCurrentDirectory(_previousDirectory);
            try { Directory.Delete(_testDirectory, true); } catch { }
        }

        static void SetStatic(string field, object? value)
        {
            typeof(DragonSystem).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);
        }

        static bool IsRider(string id, params int[] traits)
        {
            var method = typeof(DragonSystem).GetMethod("IsRider", BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(string), typeof(List<(int, string)>) })!;
            var traitList = traits.Select(t => (t, ArmiesReader.GetTraitKey(t))).ToList();
            return (bool)method.Invoke(null, new object[] { id, traitList })!;
        }

        static void SlayDragon(string riderId, bool riderSlain, int? injury)
        {
            var slain = (Dictionary<string, DragonFate>)typeof(DragonSystem).GetField("SlainDragons", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            slain[riderId] = new DragonFate(riderId, riderSlain, injury) { DragonCharacterID = DragonSystem.GetDragonOf(riderId) };
        }

        static List<string> Block(string text) => text.TrimEnd('\n').Split('\n').ToList();

        static int Depth(List<string> block) => block.Sum(l => l.Count(c => c == '{') - l.Count(c => c == '}'));

        [Fact]
        public void LinksComeFromTheDragonsCurrentRider()
        {
            Assert.Equal("135010", DragonSystem.GetDragonOf("134712"));
            Assert.Equal("16908022", DragonSystem.GetDragonOf("33697978"));
            Assert.Null(DragonSystem.GetDragonOf("135498"));
            Assert.Equal(new[] { "134712", "33697978" }, DragonSystem.GetLinkedRiderIDs().OrderBy(x => x));
        }

        [Fact]
        public void RiderNeedsTheTraitAndALivingDragon()
        {
            Assert.True(IsRider("134712", 12, DRAGONRIDER));
            Assert.False(IsRider("135498", 5, DRAGONRIDER)); // trait, no dragon
            Assert.False(IsRider("134712", 12, 30));          // dragon, no trait
        }

        [Fact]
        public void DragonPassedToANewRiderFollowsTheDragon()
        {
            // Daevour now names Daenerys as its rider; Amax still has the trait and a stale current_dragon
            File.WriteAllText(Writter.DataFilesPaths.Living_Path(),
                Amax() + Daevour().Replace("identity=134712", "identity=135498") + Daenerys());
            SetStatic("LoadedLinksKey", null);

            Assert.True(IsRider("135498", 5, DRAGONRIDER));
            Assert.False(IsRider("134712", 12, DRAGONRIDER));
            Assert.Equal("135010", DragonSystem.GetDragonOf("135498"));
        }

        [Fact]
        public void DragonOfASlainRiderLosesItsRider()
        {
            DragonSystem.SetRiderSlain("134712");
            var block = Block(Daevour());

            DragonSystem.EditCharacterBlock("135010", block);

            Assert.DoesNotContain(block, l => l.Trim() == "flag=current_rider");
            Assert.DoesNotContain(block, l => l.Contains("current_rider_list"));
            Assert.Contains(block, l => l.Trim() == "name=\"past_riders\"");
            Assert.Contains(block, l => l.Trim() == "identity=134712");
            Assert.Contains(block, l => l.Trim() == "flag=owned_dragon");
            Assert.Contains(block, l => l.Trim() == "flag=in_dragonpit");
            Assert.DoesNotContain(block, l => l.Trim() == "dead_data={");
            Assert.Equal(0, Depth(block));
        }

        [Fact]
        public void DragonOfASlainRiderAddsThemToExistingPastRiders()
        {
            DragonSystem.SetRiderSlain("134712");
            var block = Block(Daevour(CharList("past_riders", "17877") + " {\n" + CharList("current_rider_list", "134712")));

            DragonSystem.EditCharacterBlock("135010", block);

            Assert.DoesNotContain(block, l => l.Contains("current_rider"));
            int past = block.FindIndex(l => l.Trim() == "name=\"past_riders\"");
            Assert.True(past != -1);
            var pastEntry = block.Skip(past).TakeWhile(l => !l.Trim().StartsWith("duration=")).ToList();
            Assert.Contains(pastEntry, l => l.Trim() == "identity=17877");
            Assert.Contains(pastEntry, l => l.Trim() == "identity=134712");
            Assert.Contains(block, l => l.Trim() == "duration={ 2 }");
            Assert.Equal(0, Depth(block));
        }

        [Fact]
        public void RiderWhoSurvivesTheirDragonBecomesDragonless()
        {
            SlayDragon("134712", riderSlain: false, injury: MAIMED);
            var block = Block(Amax());

            DragonSystem.EditCharacterBlock("134712", block);

            Assert.DoesNotContain(block, l => l.Trim() == "flag=current_dragon");
            Assert.Contains(block, l => l.Trim() == "flag=gene_dragon_wounded");
            string traits = block.First(l => l.Trim().StartsWith("traits="));
            Assert.Equal($"\ttraits={{ 12 30 {MAIMED} {DRAGONLESS} }}", traits);
            Assert.Equal(0, Depth(block));
        }

        [Fact]
        public void SlainDragonDies()
        {
            SlayDragon("33697978", riderSlain: true, injury: null);
            var block = Block(Firkrax());

            DragonSystem.EditCharacterBlock("16908022", block);

            Assert.Contains(block, l => l.Trim() == "dead_data={");
            Assert.DoesNotContain(block, l => l.Trim() == "alive_data={");
            Assert.Equal(0, Depth(block));
        }

        [Fact]
        public void UninvolvedCharactersAreUntouched()
        {
            SlayDragon("33697978", riderSlain: false, injury: MAIMED);
            DragonSystem.SetRiderSlain("134712");

            foreach (var (id, text) in new[] { ("135498", Daenerys()) })
            {
                var block = Block(text);
                DragonSystem.EditCharacterBlock(id, block);
                Assert.Equal(Block(text), block);
            }
        }
    }
}
