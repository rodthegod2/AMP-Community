using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AMP.ModSync {
    // A mod.io search result as the matcher sees it (filled from ModIO.ModProfile in-game)
    public class ModioCandidate {
        public long   id;
        public string name   = "";
        public string author = ""; // creator.username
        public bool   android;
        public bool   oculus;

        public bool Quest { get { return android || oculus; } }
    }

    // What the host knows about the Quest version of one of its mods (hostingSettings.modMatches, by mod folder)
    public class ModMatch {
        public long modioId;      // the mod.io mod Quest players get; 0 = no Quest version
        public bool confirmed;    // decided for good (exact match, or the host answered): never asked again
        public long checkedTicks; // UTC ticks of the last mod.io check; undecided entries are checked again after a while
    }

    public enum MatchScore { None, Confirm, Auto }

    // Pairs a host mod that didn't come from mod.io (Nexus etc.) with a mod.io mod that has a Quest build
    public static class ModMatcher {
        public static readonly TimeSpan RecheckAfter = TimeSpan.FromDays(7);

        // Auto: same name and same author. Confirm: same name by someone else (or an unknown author), or one name contains the other
        // (at least 4 letters, e.g. "JujutsuSorcerers" / "Jujutsu Sorcerers (Nomad)"). Only mod.io mods with a live Quest file count.
        // mod.io titles often add a tagline after "|" ("Fascia | SFX + VFX"): the part before it counts as the name too.
        public static MatchScore Score(ModEntry hostMod, ModioCandidate candidate) {
            if(hostMod == null || candidate == null || !candidate.Quest) return MatchScore.None;
            string a = SyncPlanner.Normalize(hostMod.name), b = SyncPlanner.Normalize(candidate.name);
            if(a.Length == 0 || b.Length == 0) return MatchScore.None;
            int bar = (candidate.name ?? "").IndexOf('|');
            bool sameName = a == b || (bar > 0 && a == SyncPlanner.Normalize(candidate.name.Substring(0, bar)));
            if(sameName) return SameAuthor(hostMod.author, candidate.author) ? MatchScore.Auto : MatchScore.Confirm;
            if(Math.Min(a.Length, b.Length) >= 4 && (a.Contains(b) || b.Contains(a))) return MatchScore.Confirm;
            return MatchScore.None;
        }

        // The first Auto in mod.io's order, else the first Confirm; null when nothing matches
        public static ModioCandidate Best(ModEntry hostMod, IEnumerable<ModioCandidate> candidates, out MatchScore score) {
            List<ModioCandidate> list = (candidates ?? new ModioCandidate[0]).Where(c => c != null).ToList();
            foreach(MatchScore wanted in new[] { MatchScore.Auto, MatchScore.Confirm }) {
                ModioCandidate found = list.FirstOrDefault(c => Score(hostMod, c) == wanted);
                if(found != null) { score = wanted; return found; }
            }
            score = MatchScore.None;
            return null;
        }

        // The mod's name, then the same with its CamelCase words split ("JujutsuSorcerers" -> "Jujutsu Sorcerers"): mod.io searches words
        public static List<string> SearchPhrases(string name) {
            var result = new List<string>();
            string trimmed = (name ?? "").Trim();
            if(trimmed.Length == 0) return result;
            result.Add(trimmed);
            string split = SplitWords(trimmed);
            if(split != trimmed) result.Add(split);
            return result;
        }

        private static string SplitWords(string text) {
            var sb = new StringBuilder();
            for(int i = 0; i < text.Length; i++) {
                char c = text[i];
                if(i > 0 && char.IsUpper(c) && char.IsLower(text[i - 1])) sb.Append(' ');
                sb.Append(c == '_' ? ' ' : c);
            }
            return sb.ToString();
        }

        // What a host sends as ModEntry.questModioId for a stored match: the id, -1 for "no Quest version", 0 when nothing is known yet
        public static long QuestId(ModMatch match) {
            if(match == null) return 0;
            return match.modioId > 0 ? match.modioId : -1;
        }

        // A mod is (re)checked on mod.io when nothing is stored, or the stored answer wasn't final and is old
        public static bool NeedsCheck(ModMatch match, DateTime utcNow) {
            if(match == null) return true;
            if(match.confirmed) return false;
            return utcNow.Ticks - match.checkedTicks > RecheckAfter.Ticks;
        }

        private static bool SameAuthor(string a, string b) {
            string x = SyncPlanner.Normalize(a), y = SyncPlanner.Normalize(b);
            return x.Length > 0 && x != "unknown" && x == y;
        }
    }
}
