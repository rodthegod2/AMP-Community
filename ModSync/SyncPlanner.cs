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
        public long       modioId;     // source "modio": the mod.io mod to download (on Quest it can be another page than the host's)
        public string     cacheHash;   // set after a download that isn't stored under the host's hash (another platform's build)
        public string     modioTitle;  // cross-platform: the mod.io page's title and this platform's file size, once looked up (shown before applying)
        public long       modioBytes;

        public ModEntry Mod { get { return hostMod ?? localMod; } }
    }

    public class SyncPlan {
        public List<SyncStep> steps = new List<SyncStep>();
        public bool loadOrderDiffers;
        public bool crossPlatform;     // the host plays on another platform: downloads are this platform's builds, their size isn't known here
        public bool ampDiffers;

        // Mods a crossplay player can't get only produce a warning; everything else needs setting up first
        public bool BlocksJoin { get { return loadOrderDiffers || steps.Any(s => s.action != SyncAction.Unavailable); } }

        public IEnumerable<SyncStep> Of(SyncAction action) { return steps.Where(s => s.action == action); }
        public IEnumerable<SyncStep> Steps(SyncAction first, SyncAction second) { return Of(first).Concat(Of(second)); }

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
                string line = "  " + (step.Mod.isAmp ? "AMP update (host's build) (stays installed; your previous build is kept in AMP_ModCache/old)" : step.Mod.name);
                if((action == SyncAction.Add || action == SyncAction.Replace) && !crossPlatform) line += " (" + Megabytes(step.Mod.sizeBytes) + ")";
                if(action == SyncAction.Add && crossPlatform) {
                    line += string.IsNullOrEmpty(step.modioTitle) ? " (from mod.io)"
                          : " (from mod.io: \"" + step.modioTitle + "\"" + (step.modioBytes > 0 ? ", " + Megabytes(step.modioBytes) : "") + ")";
                }
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
            plan.crossPlatform = !samePlatform;
            var personal = new HashSet<string>(host.personalMods ?? new string[0], StringComparer.OrdinalIgnoreCase);
            Func<ModEntry, bool> leftAlone = m => m.isAmp || personal.Contains(m.folder);

            // Both lists come from other players' JSON, so null entries are skipped rather than crashing the join
            List<ModEntry> hostMods = (host.mods ?? new List<ModEntry>()).Where(m => m != null).ToList();
            List<ModEntry> mine = local.Where(m => m != null && m.enabled && !leftAlone(m)).ToList();
            var matched = new HashSet<ModEntry>();
            var shared  = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // folders both sides have enabled (AMP and personal mods excluded)
            var questIds = new HashSet<long>(); // across platforms: mod.io mods already standing for a host mod (one may not be installed twice)

            // Pairs first, so the mod.io mods our own copies already stand for are known before anything is added
            var pairs = new List<KeyValuePair<ModEntry, ModEntry>>(); // host mod, our copy (null: we don't have it)
            foreach(ModEntry hostMod in hostMods.Where(m => m.enabled && !leftAlone(m))) {
                ModEntry localMod = mine.FirstOrDefault(m => !matched.Contains(m) && SameMod(hostMod, m, samePlatform));
                if(localMod != null) {
                    matched.Add(localMod);
                    if(!samePlatform) {
                        if(QuestModioId(hostMod) > 0) questIds.Add(QuestModioId(hostMod));
                        if(localMod.modioId > 0) questIds.Add(localMod.modioId);
                    }
                }
                pairs.Add(new KeyValuePair<ModEntry, ModEntry>(hostMod, localMod));
            }

            foreach(KeyValuePair<ModEntry, ModEntry> pair in pairs) {
                ModEntry hostMod = pair.Key, localMod = pair.Value;
                long modioId = samePlatform ? hostMod.modioId : QuestModioId(hostMod);
                string source = modioId > 0 ? "modio" : (samePlatform ? "host" : "");

                if(localMod == null) {
                    // A second host folder mapped to the same mod.io mod (a 1.1.0 host prevents it): only the first gets it
                    bool duplicate = !samePlatform && modioId > 0 && !questIds.Add(modioId);
                    plan.steps.Add(new SyncStep { action = source == "" || duplicate ? SyncAction.Unavailable : SyncAction.Add, hostMod = hostMod,
                                                  source = duplicate ? "" : source, modioId = duplicate ? 0 : modioId });
                    continue;
                }
                shared.Add(hostMod.folder);

                // Builds differ between platforms, so versions are only compared on the same platform.
                // A 1.0.0 host counted modio-thumbnail.png: our copy's hash by its rules counts as the same version.
                if(samePlatform && !string.IsNullOrEmpty(hostMod.hash) && !string.IsNullOrEmpty(localMod.hash) && !localMod.HasHash(hostMod.hash)) {
                    // mod.io only serves its latest version, which may not be the host's, so replacements always come from the host
                    plan.steps.Add(new SyncStep { action = SyncAction.Replace, hostMod = hostMod, localMod = localMod, source = "host" });
                }
            }

            foreach(ModEntry localMod in mine.Where(m => !matched.Contains(m))) {
                plan.steps.Add(new SyncStep { action = SyncAction.Disable, localMod = localMod });
            }

            plan.loadOrderDiffers = samePlatform && !SameOrder(host.loadOrder, localLoadOrder, shared);

            // A disabled AMP copy (folder starting with "_") is not the one that loads, so it doesn't count
            ModEntry hostAmp  = hostMods.FirstOrDefault(m => m.isAmp && m.enabled);
            ModEntry localAmp = local.FirstOrDefault(m => m != null && m.isAmp && m.enabled);
            string hostBuild  = !string.IsNullOrEmpty(host.ampBuild) ? host.ampBuild : (hostAmp != null ? hostAmp.hash : "");
            plan.ampDiffers   = !string.IsNullOrEmpty(hostBuild) && (localAmp == null || localAmp.hash != hostBuild);

            // AMP updates itself from the host like a mod (PC only: builds differ per platform). Never disabled.
            if(samePlatform && plan.ampDiffers && hostAmp != null && !string.IsNullOrEmpty(hostAmp.hash)) {
                plan.steps.Add(new SyncStep { action = localAmp == null ? SyncAction.Add : SyncAction.Replace, hostMod = hostAmp, localMod = localAmp, source = "host" });
            }
            return plan;
        }

        // Across platforms: the host's questModioId when it sent one (-1: the host knows there is no Quest version), else its own mod.io id
        // (older hosts). 0: nothing to download.
        internal static long QuestModioId(ModEntry hostMod) {
            if(hostMod.questModioId != 0) return Math.Max(0, hostMod.questModioId);
            return hostMod.modioId;
        }

        // a: the host's mod, b: ours
        internal static bool SameMod(ModEntry a, ModEntry b, bool samePlatform) {
            if(string.Equals(a.folder, b.folder, StringComparison.OrdinalIgnoreCase)) return true;
            if(samePlatform) return false;
            if(b.modioId > 0 && (b.modioId == a.modioId || b.modioId == a.questModioId)) return true;
            if(a.modioId > 0 && b.modioId > 0) return false;
            return Normalize(a.name) != "" && Normalize(a.name) == Normalize(b.name);
        }

        internal static string Normalize(string text) {
            return new string((text ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        // Only the relative order of mods both sides have counts. A side without a loadorder.json has no opinion.
        private static bool SameOrder(string[] host, string[] local, HashSet<string> shared) {
            if(host == null || local == null || host.Length == 0 || local.Length == 0) return true;
            var inHost  = new HashSet<string>(host,  StringComparer.OrdinalIgnoreCase);
            var inLocal = new HashSet<string>(local, StringComparer.OrdinalIgnoreCase);
            string[] a = host.Where(n => shared.Contains(n) && inLocal.Contains(n)).ToArray();
            string[] b = local.Where(n => shared.Contains(n) && inHost.Contains(n)).ToArray();
            return a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
        }
    }
}
