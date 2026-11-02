using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using CrusaderWars.data.save_file;
using CrusaderWars.unit_mapper;

namespace CrusaderWars
{
    /// <summary>
    /// The outcome of a dragon that fought in the battle. Keyed by the rider's character ID.
    /// </summary>
    public class DragonFate
    {
        public string RiderID { get; }
        public string? DragonCharacterID { get; set; }
        public bool RiderSlain { get; }
        public int? RiderInjuryTrait { get; }

        public DragonFate(string riderId, bool riderSlain, int? riderInjuryTrait)
        {
            RiderID = riderId;
            RiderSlain = riderSlain;
            RiderInjuryTrait = riderInjuryTrait;
        }
    }

    /// <summary>
    /// Dragons for the AGOT playthrough. A commander or knight brings their dragon into battle as a separate unit
    /// when they have a rider trait AND a living dragon names them as its current rider. Dragons can pass to a new
    /// rider in CK3, so the link is read from the save before every battle; the trait alone is not enough.
    /// If the dragon unit is wiped out, the dragon character dies in the save and the rider is either slain or
    /// permanently injured. Because the save is edited directly, AGOT's own on_death cleanup never runs, so the
    /// rider and dragon variables are cleaned up here the same way AGOT does it.
    ///
    /// Everything is configured by an optional Dragons.xml inside the loaded unit mapper folder:
    /// <code>
    /// &lt;Dragons&gt;
    ///   &lt;RiderTrait key="dragonrider"/&gt;
    ///   &lt;DragonlessTrait key="dragonless_dragonrider"/&gt;
    ///   &lt;DragonVariable name="current_dragon"/&gt;
    ///   &lt;RiderVariable name="current_rider"/&gt;
    ///   &lt;Dragon key="attila_unit_key" soldiers="1" experience="5"/&gt;
    ///   &lt;RiderFate slain_chance="50"/&gt;
    /// &lt;/Dragons&gt;
    /// </code>
    /// Without that file (or without a unit key) dragons are disabled and battles work as before.
    /// </summary>
    public static class DragonSystem
    {
        public const string CONFIG_FILE_NAME = "Dragons.xml";
        public const string UNIT_NAME = "Dragon";
        public const string SCRIPT_TYPE_PREFIX = "dragon";

        static string? LoadedConfigPath;
        static List<string> RiderTraitKeys = new List<string>();
        static string DragonlessTraitKey = "dragonless_dragonrider";
        static string DragonVariable = "current_dragon";
        static string RiderVariable = "current_rider";
        static string UnitKey = string.Empty;
        static int Soldiers = 1;
        static int Experience = 5;
        static int RiderSlainChance = 50;

        // rider ID -> the living dragon that names them as its current rider
        static Dictionary<string, string> DragonOfRider = new Dictionary<string, string>();
        static string? LoadedLinksKey;

        static readonly Dictionary<string, DragonFate> SlainDragons = new Dictionary<string, DragonFate>();
        static readonly HashSet<string> SlainRiders = new HashSet<string>();

        public static bool IsEnabled()
        {
            LoadConfig();
            return RiderTraitKeys.Any() && !string.IsNullOrEmpty(UnitKey);
        }

        public static string GetUnitKey() { LoadConfig(); return UnitKey; }
        public static int GetSoldiers() { LoadConfig(); return Soldiers; }
        public static int GetExperience() { LoadConfig(); return Experience; }

        static void LoadConfig()
        {
            string? mapperPath = UnitMappers_BETA.GetLoadedUnitMapperPath();
            string? configPath = mapperPath != null ? Path.Combine(mapperPath, CONFIG_FILE_NAME) : null;
            if (configPath == LoadedConfigPath) return;

            LoadedConfigPath = configPath;
            LoadedLinksKey = null;
            RiderTraitKeys = new List<string>();
            DragonlessTraitKey = "dragonless_dragonrider";
            DragonVariable = "current_dragon";
            RiderVariable = "current_rider";
            UnitKey = string.Empty;
            Soldiers = 1;
            Experience = 5;
            RiderSlainChance = 50;

            if (configPath == null || !File.Exists(configPath))
            {
                Program.Logger.Debug($"Dragons: no {CONFIG_FILE_NAME} in the loaded unit mapper. Dragons are disabled.");
                return;
            }

            try
            {
                var doc = new XmlDocument();
                doc.Load(configPath);
                var root = doc.DocumentElement;
                if (root == null) return;

                foreach (XmlNode node in root.SelectNodes("RiderTrait")!)
                {
                    string? key = node.Attributes?["key"]?.Value?.Trim();
                    if (!string.IsNullOrEmpty(key)) RiderTraitKeys.Add(key);
                }

                string? dragonless = root.SelectSingleNode("DragonlessTrait")?.Attributes?["key"]?.Value?.Trim();
                if (dragonless != null) DragonlessTraitKey = dragonless;

                string? variable = root.SelectSingleNode("DragonVariable")?.Attributes?["name"]?.Value?.Trim();
                if (!string.IsNullOrEmpty(variable)) DragonVariable = variable;

                string? riderVariable = root.SelectSingleNode("RiderVariable")?.Attributes?["name"]?.Value?.Trim();
                if (!string.IsNullOrEmpty(riderVariable)) RiderVariable = riderVariable;

                var dragonNode = root.SelectSingleNode("Dragon");
                UnitKey = dragonNode?.Attributes?["key"]?.Value?.Trim() ?? string.Empty;
                if (int.TryParse(dragonNode?.Attributes?["soldiers"]?.Value, out int soldiers) && soldiers > 0) Soldiers = soldiers;
                if (int.TryParse(dragonNode?.Attributes?["experience"]?.Value, out int experience)) Experience = Math.Clamp(experience, 0, 9);

                if (int.TryParse(root.SelectSingleNode("RiderFate")?.Attributes?["slain_chance"]?.Value, out int slainChance))
                    RiderSlainChance = Math.Clamp(slainChance, 0, 100);

                Program.Logger.Debug($"Dragons: loaded {configPath}. Rider traits: [{string.Join(", ", RiderTraitKeys)}], dragonless trait: '{DragonlessTraitKey}', " +
                                     $"variables: {DragonVariable}/{RiderVariable}, unit key: '{UnitKey}', soldiers: {Soldiers}, experience: {Experience}, rider slain chance: {RiderSlainChance}%.");
            }
            catch (Exception ex)
            {
                Program.Logger.Debug($"Dragons: failed to read {configPath}: {ex.Message}. Dragons are disabled.");
                RiderTraitKeys = new List<string>();
                UnitKey = string.Empty;
            }
        }

        //---------------------------------//
        //------- Rider-dragon links ------//
        //---------------------------------//

        /// <summary>
        /// Reads which living dragon each character currently rides from the Living file of the loaded save.
        /// Only living characters are in that file, so a dead dragon never counts.
        /// </summary>
        static void LoadLinks()
        {
            string livingPath = Writter.DataFilesPaths.Living_Path();
            if (!File.Exists(livingPath))
            {
                DragonOfRider = new Dictionary<string, string>();
                LoadedLinksKey = null;
                return;
            }

            var info = new FileInfo(livingPath);
            string key = $"{info.LastWriteTimeUtc.Ticks}_{info.Length}_{RiderVariable}_{DragonVariable}";
            if (key == LoadedLinksKey) return;
            LoadedLinksKey = key;

            var riderOfDragon = new Dictionary<string, string>();
            var dragonOfRiderVar = new Dictionary<string, string>();
            using (StreamReader streamReader = new StreamReader(livingPath))
            {
                string? line;
                string? charId = null;
                string? pendingVariable = null;
                int linesSinceFlag = 0;
                while ((line = streamReader.ReadLine()) != null)
                {
                    var charStart = Regex.Match(line, @"^(\d+)={");
                    if (charStart.Success)
                    {
                        charId = charStart.Groups[1].Value;
                        pendingVariable = null;
                        continue;
                    }
                    if (charId == null) continue;

                    string trimmed = line.Trim();
                    if (trimmed == $"flag={RiderVariable}" || trimmed == $"flag={DragonVariable}")
                    {
                        pendingVariable = trimmed.Substring("flag=".Length);
                        linesSinceFlag = 0;
                        continue;
                    }

                    if (pendingVariable != null)
                    {
                        var idMatch = Regex.Match(trimmed, @"^identity=(\d+)");
                        if (idMatch.Success)
                        {
                            if (pendingVariable == RiderVariable) riderOfDragon[charId] = idMatch.Groups[1].Value;
                            else dragonOfRiderVar[charId] = idMatch.Groups[1].Value;
                            pendingVariable = null;
                        }
                        else if (++linesSinceFlag > 8)
                        {
                            pendingVariable = null;
                        }
                    }
                }
            }

            // The dragon's own current_rider decides who rides it now; when it passes to someone new this follows it
            DragonOfRider = new Dictionary<string, string>();
            foreach (var link in riderOfDragon)
            {
                string dragonId = link.Key, riderId = link.Value;
                DragonOfRider[riderId] = dragonId;
                if (dragonOfRiderVar.TryGetValue(riderId, out string? riderSays) && riderSays != dragonId)
                {
                    Program.Logger.Debug($"Dragons: dragon {dragonId} names {riderId} as its rider, but the rider's {DragonVariable} points to {riderSays}. Using dragon {dragonId}.");
                }
            }
            foreach (var stale in dragonOfRiderVar.Where(v => !riderOfDragon.ContainsKey(v.Value)))
            {
                Program.Logger.Debug($"Dragons: {stale.Key}'s {DragonVariable} points to {stale.Value}, which is dead or has another rider. No dragon unit for them.");
            }

            Program.Logger.Debug($"Dragons: {DragonOfRider.Count} living dragon(s) with a rider in this save.");
        }

        /// <summary>
        /// The living dragon this character rides, or null if they have none.
        /// </summary>
        public static string? GetDragonOf(string riderId)
        {
            LoadLinks();
            return DragonOfRider.TryGetValue(riderId, out string? dragonId) ? dragonId : null;
        }

        /// <summary>
        /// Every character that currently rides a living dragon, whether or not they are in this battle.
        /// </summary>
        public static List<string> GetLinkedRiderIDs()
        {
            LoadLinks();
            return DragonOfRider.Keys.ToList();
        }

        //---------------------------------//
        //-------- Before the battle ------//
        //---------------------------------//

        static bool HasRiderTrait(List<(int, string)> traits)
        {
            return traits.Any(t => RiderTraitKeys.Contains(t.Item2));
        }

        static bool IsRider(string id, List<(int, string)> traits)
        {
            if (!IsEnabled() || !HasRiderTrait(traits)) return false;
            if (GetDragonOf(id) != null) return true;

            Program.Logger.Debug($"Dragons: {id} has a rider trait but no living dragon names them as its rider. No dragon unit.");
            return false;
        }

        public static bool IsRider(Knight knight)
        {
            return IsRider(knight.GetID(), knight.GetTraits());
        }

        public static bool IsRider(CommanderSystem commander)
        {
            return IsRider(commander.ID, commander.GetTraits());
        }

        /// <summary>
        /// Every dragon rider fighting in this army: the commander and the knights.
        /// </summary>
        public static List<(string id, string name, Culture? culture)> GetRiders(Army army)
        {
            var riders = new List<(string id, string name, Culture? culture)>();
            if (!IsEnabled()) return riders;

            if (army.Commander != null && IsRider(army.Commander))
            {
                riders.Add((army.Commander.ID, army.Commander.Name, army.Commander.GetCultureObj()));
            }

            foreach (var knight in army.Knights?.GetKnightsList() ?? new List<Knight>())
            {
                if (IsRider(knight) && !riders.Any(r => r.id == knight.GetID()))
                {
                    riders.Add((knight.GetID(), knight.GetName(), knight.GetCultureObj()));
                }
            }

            return riders;
        }

        //---------------------------------//
        //-------- After the battle -------//
        //---------------------------------//

        public static void ResetBattleResults()
        {
            SlainDragons.Clear();
            SlainRiders.Clear();
        }

        /// <summary>
        /// Reads the Attila results of this army's dragon units. A dragon whose unit has no one left is slain,
        /// and its rider rolls between death and a permanent injury.
        /// </summary>
        public static void CheckForSlainDragons(Army army)
        {
            if (army.UnitsResults == null) return;

            var mainPhase = army.UnitsResults.Alive_MainPhase ?? new List<(string Script, string Type, string CultureID, string Remaining)>();
            var pursuitPhase = army.UnitsResults.Alive_PursuitPhase ?? new List<(string Script, string Type, string CultureID, string Remaining)>();

            foreach (var result in mainPhase.Where(r => r.Type.StartsWith(SCRIPT_TYPE_PREFIX)))
            {
                var riderMatch = Regex.Match(result.Type, $@"^{SCRIPT_TYPE_PREFIX}(\d+)_");
                if (!riderMatch.Success) continue;
                string riderId = riderMatch.Groups[1].Value;

                // The pursuit phase has the final count when it was fought
                var pursuitResult = pursuitPhase.FirstOrDefault(p => p.Script == result.Script);
                string finalRemaining = pursuitResult != default ? pursuitResult.Remaining : result.Remaining;

                int.TryParse(finalRemaining, out int remaining);
                if (remaining > 0)
                {
                    Program.Logger.Debug($"Dragons: the dragon of rider {riderId} in army {army.ID} survived with {remaining} remaining.");
                    continue;
                }

                LoadConfig();
                bool riderSlain = CharacterSharedRandom.Rng.Next(1, 101) <= RiderSlainChance;
                int? injuryTrait = riderSlain ? null : RollPermanentInjury();

                var fate = new DragonFate(riderId, riderSlain, injuryTrait) { DragonCharacterID = GetDragonOf(riderId) };
                SlainDragons[riderId] = fate;
                Program.Logger.Debug($"Dragons: dragon {fate.DragonCharacterID ?? "(unknown)"} of rider {riderId} in army {army.ID} was slain. " +
                                     (riderSlain ? "The rider died in the fall." : $"The rider survived with a permanent injury (trait {injuryTrait})."));
            }
        }

        static int RollPermanentInjury()
        {
            int[] permanentInjuries =
            {
                WoundedTraits.Maimed(),
                WoundedTraits.One_Legged(),
                WoundedTraits.One_Eyed(),
                WoundedTraits.Disfigured()
            };
            return permanentInjuries[CharacterSharedRandom.Rng.Next(permanentInjuries.Length)];
        }

        public static bool LostDragon(string riderId) { return SlainDragons.ContainsKey(riderId); }

        public static bool ShouldRiderDie(string riderId)
        {
            return SlainDragons.TryGetValue(riderId, out var fate) && fate.RiderSlain;
        }

        /// <summary>
        /// Records that a rider died in this battle, for any reason. If their dragon lived, it is freed so it can pass to a new rider.
        /// </summary>
        public static void SetRiderSlain(string riderId)
        {
            if (GetDragonOf(riderId) == null) return;
            SlainRiders.Add(riderId);
        }

        /// <summary>
        /// Applies dragon fates to a character block of the Living file:
        /// slain dragons die, surviving riders of slain dragons are injured and become dragonless,
        /// and surviving dragons of slain riders lose their rider.
        /// Rider deaths are handled by the normal slain-character logic.
        /// </summary>
        public static void EditCharacterBlock(string charId, List<string> charBlock)
        {
            if (!SlainDragons.Any() && !SlainRiders.Any()) return;
            if (charBlock.Any(l => l.Trim() == "dead_data={")) return;

            // A slain dragon dies
            var slainDragonFate = SlainDragons.Values.FirstOrDefault(f => f.DragonCharacterID == charId);
            if (slainDragonFate != null)
            {
                Program.Logger.Debug($"Dragons: dragon character {charId} of rider {slainDragonFate.RiderID} was slain in battle.");
                data.battle_results.BattleResult.MarkCharacterBlockAsDead(charBlock);
                return;
            }

            // A living dragon whose rider died is freed, like AGOT's agot_dragon.0002 does when a rider dies
            string? riderOfThisDragon = SlainRiders.FirstOrDefault(r => GetDragonOf(r) == charId);
            if (riderOfThisDragon != null)
            {
                RemoveVariable(charBlock, RiderVariable);
                bool movedToPastRiders = RenameVariableList(charBlock, $"{RiderVariable}_list", "past_riders");
                if (!movedToPastRiders)
                {
                    RemoveVariableList(charBlock, $"{RiderVariable}_list");
                    AddToVariableList(charBlock, "past_riders", riderOfThisDragon);
                }
                Program.Logger.Debug($"Dragons: rider {riderOfThisDragon} died, so dragon {charId} no longer has a rider and can pass to someone new.");
                return;
            }

            // A rider who survived their dragon's death: permanent injury, and no longer a rider, like AGOT's agot_dragon_death.0001
            if (SlainDragons.TryGetValue(charId, out var fate) && !fate.RiderSlain)
            {
                if (fate.RiderInjuryTrait.HasValue) AddTrait(charBlock, fate.RiderInjuryTrait.Value);

                RemoveVariable(charBlock, DragonVariable);
                SwapRiderTraitForDragonless(charBlock);
                Program.Logger.Debug($"Dragons: rider {charId} survived their dragon. Removed {DragonVariable} and made them dragonless.");
            }
        }

        //---------------------------------//
        //-------- Save block editing -----//
        //---------------------------------//

        static void AddTrait(List<string> charBlock, int trait)
        {
            int traitsIdx = charBlock.FindIndex(l => l.Trim().StartsWith("traits={"));
            if (traitsIdx == -1) return;

            bool hasTrait = Regex.IsMatch(charBlock[traitsIdx], $@"[{{\s]{trait}[\s}}]");
            if (hasTrait) return;

            int closeIdx = charBlock[traitsIdx].LastIndexOf('}');
            if (closeIdx != -1)
            {
                charBlock[traitsIdx] = charBlock[traitsIdx].Substring(0, closeIdx).TrimEnd() + $" {trait} }}";
                Program.Logger.Debug($"Dragons: added trait {trait}.");
            }
        }

        static int? GetTraitIndex(string traitKey)
        {
            if (string.IsNullOrEmpty(traitKey)) return null;
            int index = ArmiesReader.GetTraitIndex(traitKey);
            // GetTraitIndex returns 0 when the trait is missing, so check it really is that trait
            return ArmiesReader.GetTraitKey(index) == traitKey ? index : null;
        }

        static void SwapRiderTraitForDragonless(List<string> charBlock)
        {
            int traitsIdx = charBlock.FindIndex(l => l.Trim().StartsWith("traits={"));
            if (traitsIdx == -1) return;

            foreach (string riderTrait in RiderTraitKeys)
            {
                int? riderIndex = GetTraitIndex(riderTrait);
                if (riderIndex == null) continue;
                charBlock[traitsIdx] = Regex.Replace(charBlock[traitsIdx], $@"(?<=[{{\s]){riderIndex}(?=[\s}}])\s?", "");
            }

            int? dragonlessIndex = GetTraitIndex(DragonlessTraitKey);
            if (dragonlessIndex != null) AddTrait(charBlock, dragonlessIndex.Value);
        }

        /// <summary>
        /// Finds a variable or variable list entry: the line holding its key and the line that closes its { } element.
        /// </summary>
        static (int keyIdx, int endIdx) FindVariableEntry(List<string> charBlock, string keyLine)
        {
            int keyIdx = charBlock.FindIndex(l => l.Trim() == keyLine);
            if (keyIdx == -1) return (-1, -1);

            int depth = 1;
            for (int i = keyIdx; i < charBlock.Count; i++)
            {
                depth += charBlock[i].Count(c => c == '{');
                depth -= charBlock[i].Count(c => c == '}');
                if (depth == 0) return (keyIdx, i);
            }
            return (-1, -1);
        }

        /// <summary>
        /// Removes a whole { ... } element of a variables data={ } or list={ } block.
        /// </summary>
        static bool RemoveEntry(List<string> charBlock, string keyLine)
        {
            var (keyIdx, endIdx) = FindVariableEntry(charBlock, keyLine);
            if (keyIdx < 1) return false;

            // The element opens on the line before its key: either a lone "{" or the "data={ {" / "list={ {" of the first element
            string openLine = charBlock[keyIdx - 1];
            if (openLine.Trim() == "{")
            {
                charBlock.RemoveRange(keyIdx - 1, endIdx - keyIdx + 2);
            }
            else if (openLine.TrimEnd().EndsWith("{ {"))
            {
                charBlock[keyIdx - 1] = openLine.Substring(0, openLine.LastIndexOf('{')).TrimEnd();
                charBlock.RemoveRange(keyIdx, endIdx - keyIdx + 1);
            }
            else
            {
                return false;
            }
            return true;
        }

        static void RemoveVariable(List<string> charBlock, string name)
        {
            if (RemoveEntry(charBlock, $"flag={name}"))
                Program.Logger.Debug($"Dragons: removed variable {name}.");
        }

        static void RemoveVariableList(List<string> charBlock, string name)
        {
            if (RemoveEntry(charBlock, $"name=\"{name}\""))
                Program.Logger.Debug($"Dragons: cleared variable list {name}.");
        }

        /// <summary>
        /// Renames a variable list, unless a list with the new name already exists.
        /// </summary>
        static bool RenameVariableList(List<string> charBlock, string from, string to)
        {
            if (charBlock.Any(l => l.Trim() == $"name=\"{to}\"")) return false;

            int idx = charBlock.FindIndex(l => l.Trim() == $"name=\"{from}\"");
            if (idx == -1) return false;

            charBlock[idx] = charBlock[idx].Replace($"name=\"{from}\"", $"name=\"{to}\"");
            Program.Logger.Debug($"Dragons: moved variable list {from} to {to}.");
            return true;
        }

        /// <summary>
        /// Adds a character to an existing variable list and bumps its item count. Does nothing if the list doesn't exist yet.
        /// </summary>
        static void AddToVariableList(List<string> charBlock, string name, string charId)
        {
            var (keyIdx, endIdx) = FindVariableEntry(charBlock, $"name=\"{name}\"");
            if (keyIdx == -1) return;

            for (int i = keyIdx; i <= endIdx; i++)
            {
                if (Regex.IsMatch(charBlock[i], $@"^\s*identity={charId}\s*$")) return;
            }

            int durationIdx = charBlock.FindIndex(keyIdx, endIdx - keyIdx + 1, l => l.Trim().StartsWith("duration={"));
            if (durationIdx == -1) return;

            // duration={ N ... } starts with the number of items
            var count = Regex.Match(charBlock[durationIdx], @"duration=\{\s*(\d+)");
            if (!count.Success) return;
            int newCount = int.Parse(count.Groups[1].Value) + 1;
            charBlock[durationIdx] = charBlock[durationIdx].Substring(0, count.Groups[1].Index) + newCount + charBlock[durationIdx].Substring(count.Groups[1].Index + count.Groups[1].Length);

            string indent = Regex.Match(charBlock[keyIdx], @"^\s*").Value;
            charBlock.InsertRange(durationIdx, new[]
            {
                $"{indent}item={{",
                $"{indent}\ttype=char",
                $"{indent}\tidentity={charId}",
                $"{indent}}}",
                ""
            });
            Program.Logger.Debug($"Dragons: added {charId} to variable list {name}.");
        }
    }
}
