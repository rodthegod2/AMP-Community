using AMP.Data;
using AMP.Logging;
using ModIO;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AMP.ModSync {
    // Host side of the Quest mapping. When hosting starts, every enabled host mod without a decided answer is looked up on mod.io, one request
    // at a time: a mod from mod.io is checked for a Quest file, any other mod is searched by name (ModMatcher). Answers are stored in
    // hostingSettings.modMatches; matches that need the host's Yes/No wait in the AMP page. Joiners get the result as ModEntry.questModioId.
    // Read-only mod.io use, no login. Main thread, except QuestIdFor and PassDone.
    internal static class QuestMatcher {
        private const float RequestSpacing  = 1.5f; // seconds between two mod.io requests
        private const float AnswerTimeout   = 30f;
        private const int   MaxErrorsInARow = 3;    // mod.io unreachable: give up for this hosting session

        internal class Candidate {
            internal string folder;
            internal string modName;
            internal ModioCandidate modio;
        }

        private static readonly List<Candidate> pending = new List<Candidate>();                 // main thread
        private static volatile Dictionary<string, long> questIds = new Dictionary<string, long>(); // replaced, never changed: read from any thread
        private static volatile TaskCompletionSource<bool> pass = Finished();
        private static int generation;

        // Completes when the current pass has asked mod.io about every mod (or was stopped)
        internal static Task PassDone { get { return pass.Task; } }

        // The match the AMP page asks the host about next, null when there is none
        internal static Candidate Current { get { return pending.FirstOrDefault(); } }

        internal static Candidate PendingFor(string folder) {
            return pending.FirstOrDefault(c => string.Equals(c.folder, folder, StringComparison.OrdinalIgnoreCase));
        }

        // What joiners get as ModEntry.questModioId (see ModMatcher.QuestId)
        internal static long QuestIdFor(string folder) {
            long id;
            return folder != null && questIds.TryGetValue(folder, out id) ? id : 0;
        }

        // Stored answer for a folder, null if none
        internal static ModMatch StoredFor(string folder) {
            ModMatch match;
            return folder != null && Matches().TryGetValue(folder, out match) ? match : null;
        }

        // When hosting starts, before the host's mods are known: joiners wait for this pass (ModSyncHost, for a while at most)
        internal static void Begin() {
            Stop();
            pending.Clear();
            pass = new TaskCompletionSource<bool>();
        }

        // Once the host's mods are known (main thread)
        internal static void Run(List<ModEntry> mods) {
            int current = generation;
            Publish();
            ModManager.instance.StartCoroutine(Pass(mods.Where(m => m != null && m.enabled && !m.isAmp).ToList(), current));
        }

        // Hosting stopped (or starts again): the running pass ends, nobody waits for it. Any thread.
        internal static void Stop() {
            Interlocked.Increment(ref generation);
            pass.TrySetResult(false);
        }

        // The host's answer from the AMP page (or the test autopilot): Yes stores the match, No stores "no Quest version"
        internal static void Answer(Candidate candidate, bool yes) {
            if(candidate == null || !pending.Remove(candidate)) return;
            Store(candidate.folder, new ModMatch { modioId = yes ? candidate.modio.id : 0, confirmed = true, checkedTicks = DateTime.UtcNow.Ticks });
            Log.Info(Defines.SERVER, "Quest match: " + candidate.modName + (yes ? " -> mod.io " + candidate.modio.id + " (\"" + candidate.modio.name + "\")" : ": no Quest version") + ", confirmed by the host.");
        }

        private static IEnumerator Pass(List<ModEntry> mods, int current) {
            float nextRequest = 0f;
            int errors = 0;
            foreach(ModEntry mod in mods) {
                if(current != generation) yield break;
                if(!ModMatcher.NeedsCheck(StoredFor(mod.folder), DateTime.UtcNow)) continue;
                if(errors >= MaxErrorsInARow) { Log.Warn(Defines.SERVER, "Quest match: mod.io isn't answering, the remaining mods are checked next time."); break; }

                if(mod.modioId > 0) {
                    // Installed from mod.io: Quest players get the same page if it has a Quest file
                    while(Time.realtimeSinceStartup < nextRequest) yield return null;
                    nextRequest = Time.realtimeSinceStartup + RequestSpacing;
                    ModProfile? profile = null;
                    string error = null;
                    bool answered = false;
                    ModioClient.GetMod(mod.modioId, (p, e) => { profile = p; error = e; answered = true; });
                    float asked = Time.realtimeSinceStartup;
                    while(!answered && Time.realtimeSinceStartup - asked < AnswerTimeout) yield return null;
                    if(current != generation) yield break;
                    if(!answered || error != null) { errors++; Log.Warn(Defines.SERVER, "Quest match: mod.io " + mod.modioId + " (" + mod.name + "): " + (error ?? "no answer")); continue; }
                    errors = 0;
                    ModioAvailability available = ModioClient.PlatformAvailability(profile.Value);
                    Store(mod.folder, new ModMatch { modioId = available.Quest ? mod.modioId : 0, checkedTicks = DateTime.UtcNow.Ticks });
                    Log.Info(Defines.SERVER, "Quest match: " + mod.name + " is mod.io " + mod.modioId + " (\"" + profile.Value.name + "\" by " + profile.Value.creator.username + ", " + available + ")");
                    continue;
                }

                ModioCandidate found = null;
                MatchScore score = MatchScore.None;
                bool failed = false;
                foreach(string phrase in ModMatcher.SearchPhrases(mod.name)) {
                    while(Time.realtimeSinceStartup < nextRequest) yield return null;
                    nextRequest = Time.realtimeSinceStartup + RequestSpacing;
                    List<ModProfile> results = null;
                    string error = null;
                    bool answered = false;
                    ModioClient.Search(phrase, (list, e) => { results = list; error = e; answered = true; });
                    float asked = Time.realtimeSinceStartup;
                    while(!answered && Time.realtimeSinceStartup - asked < AnswerTimeout) yield return null;
                    if(current != generation) yield break;
                    if(!answered || error != null) { failed = true; errors++; Log.Warn(Defines.SERVER, "Quest match: searching \"" + phrase + "\" failed: " + (error ?? "no answer")); break; }
                    errors = 0;
                    found = ModMatcher.Best(mod, results.Select(ToCandidate), out score);
                    if(found != null) break;
                }
                if(failed) continue;

                if(score == MatchScore.Auto) {
                    Store(mod.folder, new ModMatch { modioId = found.id, confirmed = true, checkedTicks = DateTime.UtcNow.Ticks });
                    Log.Info(Defines.SERVER, "Quest match: " + mod.name + " -> mod.io " + found.id + " (\"" + found.name + "\" by " + found.author + ")");
                } else if(score == MatchScore.Confirm) {
                    pending.Add(new Candidate { folder = mod.folder, modName = mod.name, modio = found });
                    Log.Info(Defines.SERVER, "Quest match: " + mod.name + " might be mod.io " + found.id + " (\"" + found.name + "\" by " + found.author + "), asking the host.");
                } else {
                    Store(mod.folder, new ModMatch { modioId = 0, checkedTicks = DateTime.UtcNow.Ticks });
                    Log.Info(Defines.SERVER, "Quest match: no Quest version of " + mod.name + " found on mod.io.");
                }
            }
            if(current == generation) pass.TrySetResult(true);
        }

        private static ModioCandidate ToCandidate(ModProfile p) {
            ModioAvailability available = ModioClient.PlatformAvailability(p);
            return new ModioCandidate { id = p.id.id, name = p.name ?? "", author = p.creator.username ?? "", android = available.android, oculus = available.oculus };
        }

        private static Dictionary<string, ModMatch> Matches() {
            SafeFile.HostingSettings settings = ModManager.safeFile.hostingSettings;
            if(settings.modMatches == null) settings.modMatches = new Dictionary<string, ModMatch>();
            return settings.modMatches;
        }

        private static void Store(string folder, ModMatch match) {
            Matches()[folder] = match;
            try {
                ModManager.safeFile.Save();
            } catch(Exception e) {
                Log.Warn(Defines.SERVER, "Quest match: couldn't save the config. " + e.Message);
            }
            Publish();
        }

        private static void Publish() {
            var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach(KeyValuePair<string, ModMatch> m in Matches()) {
                if(m.Key != null) ids[m.Key] = ModMatcher.QuestId(m.Value);
            }
            questIds = ids;
        }

        private static TaskCompletionSource<bool> Finished() {
            var done = new TaskCompletionSource<bool>();
            done.SetResult(true);
            return done;
        }
    }
}
