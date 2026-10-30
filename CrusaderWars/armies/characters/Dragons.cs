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
    /// Dragons for the AGOT playthrough. A commander or knight with a rider trait brings their dragon
    /// into battle as a separate unit. If that unit is wiped out, the dragon character dies in the save
    /// and the rider is either slain or permanently injured.
    ///
    /// Everything is configured by an optional Dragons.xml inside the loaded unit mapper folder:
    /// <code>
    /// &lt;Dragons&gt;
    ///   &lt;RiderTrait key="dragonrider"/&gt;
    ///   &lt;DragonVariable name="current_dragon"/&gt;
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
        static string DragonVariable = "current_dragon";
        static string UnitKey = string.Empty;
        static int Soldiers = 1;
        static int Experience = 5;
        static int RiderSlainChance = 50;

        static readonly Dictionary<string, DragonFate> SlainDragons = new Dictionary<string, DragonFate>();

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
            RiderTraitKeys = new List<string>();
            DragonVariable = "current_dragon";
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

                string? variable = root.SelectSingleNode("DragonVariable")?.Attributes?["name"]?.Value?.Trim();
                if (!string.IsNullOrEmpty(variable)) DragonVariable = variable;

                var dragonNode = root.SelectSingleNode("Dragon");
                UnitKey = dragonNode?.Attributes?["key"]?.Value?.Trim() ?? string.Empty;
                if (int.TryParse(dragonNode?.Attributes?["soldiers"]?.Value, out int soldiers) && soldiers > 0) Soldiers = soldiers;
                if (int.TryParse(dragonNode?.Attributes?["experience"]?.Value, out int experience)) Experience = Math.Clamp(experience, 0, 9);

                if (int.TryParse(root.SelectSingleNode("RiderFate")?.Attributes?["slain_chance"]?.Value, out int slainChance))
                    RiderSlainChance = Math.Clamp(slainChance, 0, 100);

                Program.Logger.Debug($"Dragons: loaded {configPath}. Rider traits: [{string.Join(", ", RiderTraitKeys)}], " +
                                     $"variable: {DragonVariable}, unit key: '{UnitKey}', soldiers: {Soldiers}, experience: {Experience}, rider slain chance: {RiderSlainChance}%.");
            }
            catch (Exception ex)
            {
                Program.Logger.Debug($"Dragons: failed to read {configPath}: {ex.Message}. Dragons are disabled.");
                RiderTraitKeys = new List<string>();
                UnitKey = string.Empty;
            }
        }

        //---------------------------------//
        //-------- Before the battle ------//
        //---------------------------------//

        static bool HasRiderTrait(List<(int, string)> traits)
        {
            return traits.Any(t => RiderTraitKeys.Contains(t.Item2));
        }

        public static bool IsRider(Knight knight)
        {
            return IsEnabled() && HasRiderTrait(knight.GetTraits());
        }

        public static bool IsRider(CommanderSystem commander)
        {
            return IsEnabled() && HasRiderTrait(commander.GetTraits());
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

                SlainDragons[riderId] = new DragonFate(riderId, riderSlain, injuryTrait);
                Program.Logger.Debug($"Dragons: the dragon of rider {riderId} in army {army.ID} was slain. " +
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
        /// Called with each rider's save block before the Living file is edited, to find which character is their dragon.
        /// </summary>
        public static void ReadDragonCharacterID(string riderId, List<string> riderBlock)
        {
            if (!SlainDragons.TryGetValue(riderId, out var fate)) return;

            LoadConfig();
            int flagIdx = riderBlock.FindIndex(l => Regex.IsMatch(l, $@"flag=""?{Regex.Escape(DragonVariable)}""?\s*$"));
            if (flagIdx != -1)
            {
                for (int i = flagIdx + 1; i < Math.Min(flagIdx + 10, riderBlock.Count); i++)
                {
                    var idMatch = Regex.Match(riderBlock[i], @"identity=(\d+)");
                    if (idMatch.Success)
                    {
                        fate.DragonCharacterID = idMatch.Groups[1].Value;
                        Program.Logger.Debug($"Dragons: rider {riderId} rides dragon character {fate.DragonCharacterID}.");
                        return;
                    }
                }
            }

            Program.Logger.Debug($"Dragons: could not find variable '{DragonVariable}' on rider {riderId}. The dragon character will not be removed from the save.");
        }

        /// <summary>
        /// Applies dragon fates to a character block of the Living file: kills slain dragons and gives surviving riders their injury.
        /// Rider deaths are handled by the normal slain-character logic.
        /// </summary>
        public static void EditCharacterBlock(string charId, List<string> charBlock)
        {
            if (!SlainDragons.Any()) return;

            bool isSlainDragon = SlainDragons.Values.Any(f => f.DragonCharacterID == charId);
            if (isSlainDragon && !charBlock.Any(l => l.Trim() == "dead_data={"))
            {
                Program.Logger.Debug($"Dragons: dragon character {charId} was slain in battle.");
                data.battle_results.BattleResult.MarkCharacterBlockAsDead(charBlock);
                return;
            }

            if (SlainDragons.TryGetValue(charId, out var fate) && !fate.RiderSlain && fate.RiderInjuryTrait.HasValue)
            {
                int traitsIdx = charBlock.FindIndex(l => l.Trim().StartsWith("traits={"));
                if (traitsIdx == -1) return;

                string trait = fate.RiderInjuryTrait.Value.ToString();
                bool hasTrait = Regex.IsMatch(charBlock[traitsIdx], $@"[{{\s]{trait}[\s}}]");
                if (!hasTrait)
                {
                    int closeIdx = charBlock[traitsIdx].LastIndexOf('}');
                    if (closeIdx != -1)
                    {
                        charBlock[traitsIdx] = charBlock[traitsIdx].Substring(0, closeIdx).TrimEnd() + $" {trait} }}";
                        Program.Logger.Debug($"Dragons: rider {charId} was permanently injured (trait {trait}) when their dragon fell.");
                    }
                }
            }
        }
    }
}
