using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CrusaderWars.data.save_file
{
    /// <summary>
    /// Finds the members of the AGOT Kingsguard in a CK3 save.
    ///
    /// AGOT keeps the Kingsguard seats as variables on the Iron Throne title
    /// (h_the_iron_throne), one per seat, each pointing at the knight in it:
    ///   variables={ data={ {
    ///       flag="kingsguard_lord_commander"
    ///       data={ type=character identity=67237715 }
    ///     } { flag="kingsguard_1" data={ type=character identity=33719803 } } ... } }
    /// (confirmed against a CK3 1.19 AGOT save). This scanner runs alongside the
    /// normal save read, one line at a time, and does not depend on the name of
    /// any enclosing block. Besides that layout it also accepts, as fallbacks:
    ///   - a block with position="kingsguard_1" and a councillor=/character=/holder=
    ///     style field for the member's character ID (the field may also sit in the
    ///     block directly around it);
    ///   - a block keyed by an object ID that holds the position, plus a character
    ///     elsewhere that points at that object with council_task=ID;
    ///   - a plain kingsguard_1=CHARACTER_ID field.
    /// Without AGOT nothing matches, so the result is simply empty.
    /// </summary>
    internal static class KingsguardScanner
    {
        private static readonly Regex PositionRx =
            new Regex("^\"?(kingsguard_lord_commander|kingsguard_\\d+)\"?$", RegexOptions.Compiled);

        // Field names that can hold the member's character ID, in order of preference.
        private static readonly string[] CharacterFields = { "councillor", "character", "holder", "employee", "member" };

        // CK3 writes this value for "no character".
        private const string NullId = "4294967295";

        private sealed class Frame
        {
            public string Key = "";
            public string? Position;
            public bool PositionFromChild;
            public string? CouncilTask;
            public Dictionary<string, string>? Ids;
            // A variable value block: data={ type=character identity=ID }
            public bool TypeIsCharacter;
            public string? Identity;
        }

        private static readonly Stack<Frame> _stack = new Stack<Frame>();
        private static readonly Dictionary<string, string> _members = new Dictionary<string, string>();   // character ID -> position
        private static readonly Dictionary<string, string> _objectPositions = new Dictionary<string, string>(); // object ID -> position
        private static readonly Dictionary<string, string> _characterTasks = new Dictionary<string, string>();  // character ID -> council task ID

        public static int Count => _members.Count;

        public static bool IsMember(string? characterId)
        {
            return !string.IsNullOrEmpty(characterId) && _members.ContainsKey(characterId!);
        }

        public static bool IsLordCommander(string? characterId)
        {
            return !string.IsNullOrEmpty(characterId)
                   && _members.TryGetValue(characterId!, out var pos)
                   && pos == "kingsguard_lord_commander";
        }

        public static void Reset()
        {
            _stack.Clear();
            _members.Clear();
            _objectPositions.Clear();
            _characterTasks.Clear();
        }

        public static void ScanLine(string line)
        {
            string t = line.Trim();
            if (t.Length == 0) return;

            // Block opening: key={  (or a bare "{" for an anonymous list entry)
            if (t.EndsWith("{") && CountBraces(t) == 1)
            {
                int eq = t.IndexOf('=');
                string key = eq > 0 ? t.Substring(0, eq).Trim() : "";
                _stack.Push(new Frame { Key = key });
                return;
            }

            if (t == "}")
            {
                Pop();
                return;
            }

            int balance = CountBraces(t);
            if (balance != 0)
            {
                // Unusual layouts: keep the block depth right and move on.
                for (int i = 0; i < balance; i++) _stack.Push(new Frame());
                for (int i = 0; i > balance; i--) Pop();
                return;
            }

            int idx = t.IndexOf('=');
            if (idx <= 0) return;
            string field = t.Substring(0, idx).Trim();
            string value = t.Substring(idx + 1).Trim();
            if (value.StartsWith("{")) return; // single-line list such as council={ 1 2 3 }

            // kingsguard_1=CHARACTER_ID
            if (PositionRx.IsMatch(field) && IsId(value))
            {
                AddMember(value, field.Trim('"'));
                return;
            }

            if (_stack.Count == 0) return;
            Frame f = _stack.Peek();

            if (value.IndexOf("kingsguard", StringComparison.Ordinal) >= 0)
            {
                var m = PositionRx.Match(value);
                if (m.Success) f.Position = m.Groups[1].Value;
                return;
            }

            if (field == "council_task" && IsId(value))
            {
                f.CouncilTask = value;
                return;
            }

            if (field == "type" && value.Trim('"') == "character")
            {
                f.TypeIsCharacter = true;
                return;
            }

            if (field == "identity" && IsId(value))
            {
                f.Identity = value;
                return;
            }

            if (Array.IndexOf(CharacterFields, field) >= 0 && IsId(value))
            {
                if (f.Ids == null) f.Ids = new Dictionary<string, string>();
                if (!f.Ids.ContainsKey(field)) f.Ids[field] = value;
            }
        }

        /// <summary>Joins indirect references and logs the result. Call once the whole save has been read.</summary>
        public static void Finish()
        {
            while (_stack.Count > 0) Pop();

            foreach (var kv in _characterTasks)
            {
                if (_objectPositions.TryGetValue(kv.Value, out var position))
                    AddMember(kv.Key, position);
            }

            if (_members.Count > 0)
            {
                Program.Logger.Debug($"Kingsguard: found {_members.Count} member(s): " +
                    string.Join(", ", _members.Select(kv => $"{kv.Key} ({kv.Value})")));
            }
            else
            {
                Program.Logger.Debug("Kingsguard: no Kingsguard positions found in this save.");
            }
        }

        private static void Pop()
        {
            if (_stack.Count == 0) return;
            Frame f = _stack.Pop();
            Frame? parent = _stack.Count > 0 ? _stack.Peek() : null;

            // data={ type=character identity=ID } is the value of the variable around it:
            // give the ID to that variable's block, where the seat name (flag=) lives.
            if (f.TypeIsCharacter && f.Identity != null && parent != null)
            {
                if (parent.Ids == null) parent.Ids = new Dictionary<string, string>();
                if (!parent.Ids.ContainsKey("identity")) parent.Ids["identity"] = f.Identity;
            }

            if (f.Position != null)
            {
                string? id = FindCharacterId(f);
                if (id != null)
                {
                    AddMember(id, f.Position);
                }
                else
                {
                    if (IsId(f.Key)) _objectPositions[f.Key] = f.Position;
                    // The position may sit in a sub-block next to the character field: hand it up one level only.
                    if (parent != null && parent.Position == null && !f.PositionFromChild)
                    {
                        parent.Position = f.Position;
                        parent.PositionFromChild = true;
                    }
                }
            }

            if (f.CouncilTask != null)
            {
                if (IsId(f.Key)) _characterTasks[f.Key] = f.CouncilTask;
                else if (parent != null && parent.CouncilTask == null) parent.CouncilTask = f.CouncilTask;
            }
        }

        private static string? FindCharacterId(Frame f)
        {
            if (f.Ids == null) return null;
            if (f.Ids.TryGetValue("identity", out var variableTarget)) return variableTarget;
            foreach (var name in CharacterFields)
            {
                if (f.Ids.TryGetValue(name, out var id)) return id;
            }
            return null;
        }

        private static void AddMember(string characterId, string position)
        {
            if (!IsId(characterId)) return;
            _members[characterId] = position;
        }

        private static bool IsId(string s)
        {
            if (string.IsNullOrEmpty(s) || s == NullId) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        private static int CountBraces(string s)
        {
            int n = 0;
            bool inQuotes = false;
            foreach (char c in s)
            {
                if (c == '"') inQuotes = !inQuotes;
                else if (!inQuotes)
                {
                    if (c == '{') n++;
                    else if (c == '}') n--;
                }
            }
            return n;
        }
    }
}
