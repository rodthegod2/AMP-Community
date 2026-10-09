using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AMP.ModSync {
    public enum SyncAction { Add, Replace, Disable, Unavailable }

    public class SyncStep {
        public SyncAction action;
        public ModEntry   hostMod;   // null for Disable
        public ModEntry   localMod;  // null for Add and Unavailable
        public string     source = ""; // "modio" or "host" for Add/Replace

        public ModEntry Mod { get { return hostMod ?? localMod; } }
    }

    public class SyncPlan {
        public List<SyncStep> steps = new List<SyncStep>();
        public bool loadOrderDiffers;
        public bool ampDiffers;

        // Mods a crossplay player can't get only produce a warning; everything else needs setting up first
        public bool BlocksJoin { get { return loadOrderDiffers || steps.Any(s => s.action != SyncAction.Unavailable); } }

        public IEnumerable<SyncStep> Of(SyncAction action) { return steps.Where(s => s.action == action); }

        public string Describe() {
            var sb = new StringBuilder();
            Group(sb, "Download",                     SyncAction.Add);
            Group(sb, "Replace with the host's version", SyncAction.Replace);
            Group(sb, "Turn off",                     SyncAction.Disable);
            Group(sb, "Not available on your platform", SyncAction.Unavailable);
            if(loadOrderDiffers) sb.AppendLine("Mod load order differs from the host's.");
            if(ampDiffers)       sb.AppendLine("Your AMP build differs from the host's.");
            return sb.ToString().TrimEnd();
        }

        private void Group(StringBuilder sb, string title, SyncAction action) {
            List<SyncStep> group = Of(action).ToList();
            if(group.Count == 0) return;
            sb.AppendLine(title + ":");
            foreach(SyncStep step in group) {
                string line = "  " + step.Mod.name;
                if(action == SyncAction.Add || action == SyncAction.Replace) line += " (" + Megabytes(step.Mod.sizeBytes) + ")";
                if(step.Mod.hasCode && action != SyncAction.Disable) line += " [contains code]";
                sb.AppendLine(line);
            }
        }

        internal static string Megabytes(long bytes) {
            return Math.Max(1, (long) Math.Round(bytes / (1024.0 * 1024.0))) + " MB";
        }
    }

    public static class SyncPlanner {
        public static SyncPlan Plan(HostModSet host, List<ModEntry> local, string[] localLoadOrder, string localPlatform) {
            var plan = new SyncPlan();
            bool samePlatform = string.Equals(host.platform, localPlatform, StringComparison.OrdinalIgnoreCase);
            var personal = new HashSet<string>(host.personalMods ?? new string[0], StringComparer.OrdinalIgnoreCase);
            Func<ModEntry, bool> leftAlone = m => m.isAmp || personal.Contains(m.folder);

            // Both lists come from other players' JSON, so null entries are skipped rather than crashing the join
            List<ModEntry> hostMods = (host.mods ?? new List<ModEntry>()).Where(m => m != null).ToList();
            List<ModEntry> mine = local.Where(m => m != null && m.enabled && !leftAlone(m)).ToList();
            var matched = new HashSet<ModEntry>();

            foreach(ModEntry hostMod in hostMods.Where(m => m.enabled && !leftAlone(m))) {
                ModEntry localMod = mine.FirstOrDefault(m => !matched.Contains(m) && SameMod(hostMod, m, samePlatform));
                string source = hostMod.modioId > 0 ? "modio" : (samePlatform ? "host" : "");

                if(localMod == null) {
                    plan.steps.Add(new SyncStep { action = source == "" ? SyncAction.Unavailable : SyncAction.Add, hostMod = hostMod, source = source });
                    continue;
                }
                matched.Add(localMod);

                // Builds differ between platforms, so versions are only compared on the same platform
                if(samePlatform && !string.IsNullOrEmpty(hostMod.hash) && !string.IsNullOrEmpty(localMod.hash) && hostMod.hash != localMod.hash) {
                    // mod.io only serves its latest version, which may not be the host's, so replacements always come from the host
                    plan.steps.Add(new SyncStep { action = SyncAction.Replace, hostMod = hostMod, localMod = localMod, source = "host" });
                }
            }

            foreach(ModEntry localMod in mine.Where(m => !matched.Contains(m))) {
                plan.steps.Add(new SyncStep { action = SyncAction.Disable, localMod = localMod });
            }

            plan.loadOrderDiffers = samePlatform && !SameOrder(host.loadOrder, localLoadOrder, personal);

            // A disabled AMP copy (folder starting with "_") is not the one that loads, so it doesn't count
            ModEntry hostAmp  = hostMods.FirstOrDefault(m => m.isAmp && m.enabled);
            ModEntry localAmp = local.FirstOrDefault(m => m != null && m.isAmp && m.enabled);
            string hostBuild  = !string.IsNullOrEmpty(host.ampBuild) ? host.ampBuild : (hostAmp != null ? hostAmp.hash : "");
            plan.ampDiffers   = !string.IsNullOrEmpty(hostBuild) && (localAmp == null || localAmp.hash != hostBuild);
            return plan;
        }

        internal static bool SameMod(ModEntry a, ModEntry b, bool samePlatform) {
            if(string.Equals(a.folder, b.folder, StringComparison.OrdinalIgnoreCase)) return true;
            if(samePlatform) return false;
            if(a.modioId > 0 && b.modioId > 0) return a.modioId == b.modioId;
            return Normalize(a.name) != "" && Normalize(a.name) == Normalize(b.name);
        }

        internal static string Normalize(string text) {
            return new string((text ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        private static bool SameOrder(string[] host, string[] local, HashSet<string> personal) {
            string[] a = (host ?? new string[0]).Where(n => !personal.Contains(n)).ToArray();
            string[] b = (local ?? new string[0]).Where(n => !personal.Contains(n)).ToArray();
            return a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
        }
    }
}
