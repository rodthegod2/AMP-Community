using AMP.Data;
using AMP.Logging;
using ModIO;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace AMP.ModSync {
    // Host side of the Quest mapping (PC hosts only). When hosting starts, every enabled host mod (not AMP, not a personal mod) without a
    // current answer is looked up on mod.io, one request at a time: a mod from mod.io is checked for a Quest file, a confirmed match for its
    // live Quest file, any other mod is searched by name (ModMatcher). Answers are stored in hostingSettings.modMatches; matches that need the
    // host's Yes/No wait in the AMP page. Joiners get the result as ModEntry.questModioId. Two host folders never get the same mod.io mod.
    // Read-only mod.io use, no login. Main thread, except QuestIdFor, Running and Stop.
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
        private static readonly MatchGeneration generation = new MatchGeneration();
        private static List<ModEntry> hostMods = new List<ModEntry>(); // main thread: the mods of the running pass (for TakenBy)

        // Completes when the current pass has asked mod.io about every mod (or was stopped)
        internal static Task PassDone { get { return pass.Task; } }

        // A pass is still asking mod.io: joiners on another platform get the mod list again when it is done (ModSyncHost)
        internal static bool Running { get { return !pass.Task.IsCompleted; } }

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

        // When hosting starts on PC, before the host's mods are known: same-platform joiners don't wait for this pass, others get the list
        // again when it is done (ModSyncHost). Returns the generation Run must be called with.
        internal static int Begin() {
            Stop();
            pending.Clear();
            pass = new TaskCompletionSource<bool>();
            return generation.Begin();
        }

        // Once the host's mods are known (main thread). Does nothing when hosting stopped (or started again) since Begin.
        internal static void Run(List<ModEntry> mods, string[] personalMods, int forGeneration) {
            if(!generation.IsCurrent(forGeneration)) return;
            var personal = new HashSet<string>(personalMods ?? new string[0], StringComparer.OrdinalIgnoreCase);
            hostMods = mods.Where(m => m != null && m.enabled && !m.isAmp && !personal.Contains(m.folder)).ToList();
            Publish();
            ModManager.instance.StartCoroutine(Pass(hostMods, forGeneration));
        }

        // Hosting stopped (or starts again): the running pass ends, nobody waits for it. Any thread.
        internal static void Stop() {
            generation.Stop();
            pass.TrySetResult(false);
        }

        // The host's answer from the AMP page (or the test autopilot): Yes stores the match, No stores "no Quest version".
        // A Yes for a mod.io mod another host folder already stands for is refused (it would be installed twice on Quest).
        internal static void Answer(Candidate candidate, bool yes) {
            if(candidate == null || !pending.Remove(candidate)) return;
            string taken = yes ? ModMatcher.TakenBy(candidate.modio.id, candidate.folder, Matches(), hostMods) : null;
            if(taken != null) {
                Store(candidate.folder, new ModMatch { modioId = 0, checkedTicks = DateTime.UtcNow.Ticks });
                Log.Warn(Defines.SERVER, "Quest match: " + candidate.modName + " can't be mod.io " + candidate.modio.id + ", that is already the Quest version of " + taken + ".");
                return;
            }
            Store(candidate.folder, new ModMatch { modioId = yes ? candidate.modio.id : 0, confirmed = true, checkedTicks = DateTime.UtcNow.Ticks });
            Log.Info(Defines.SERVER, "Quest match: " + candidate.modName + (yes ? " -> mod.io " + candidate.modio.id + " (\"" + candidate.modio.name + "\")" : ": no Quest version") + ", confirmed by the host.");
            if(!yes) return;
            // Other questions about the same mod.io mod can only be answered No now
            foreach(Candidate other in pending.Where(c => c.modio.id == candidate.modio.id).ToList()) {
                pending.Remove(other);
                Store(other.folder, new ModMatch { modioId = 0, checkedTicks = DateTime.UtcNow.Ticks });
                Log.Info(Defines.SERVER, "Quest match: " + other.modName + " can't be mod.io " + other.modio.id + " too (already " + candidate.modName + ").");
            }
        }

        // Waits for the request spacing, asks mod.io once (ask gets the callback that sets answered), waits for the answer.
        // False when the pass was stopped meanwhile (also checked before the request is sent).
        private static IEnumerator Request(int current, Func<float> nextRequest, Action<float> setNext, Action ask, Func<bool> answered, Action<bool> stillCurrent) {
            while(Time.realtimeSinceStartup < nextRequest()) yield return null;
            if(!generation.IsCurrent(current)) { stillCurrent(false); yield break; }
            setNext(Time.realtimeSinceStartup + RequestSpacing);
            ask();
            float asked = Time.realtimeSinceStartup;
            while(!answered() && Time.realtimeSinceStartup - asked < AnswerTimeout) yield return null;
            stillCurrent(generation.IsCurrent(current));
        }

        private static IEnumerator Pass(List<ModEntry> mods, int current) {
            float nextRequest = 0f;
            int errors = 0;
            bool live = true;
            foreach(ModEntry mod in mods) {
                if(!generation.IsCurrent(current)) yield break;
                ModMatch stored = StoredFor(mod.folder);
                MatchCheck check = ModMatcher.CheckFor(mod, stored, DateTime.UtcNow);
                if(check == MatchCheck.None) continue;
                if(errors >= MaxErrorsInARow) { Log.Warn(Defines.SERVER, "Quest match: mod.io isn't answering, the remaining mods are checked next time."); break; }

                if(check == MatchCheck.OwnPage || check == MatchCheck.LiveFile) {
                    // Installed from mod.io: Quest players get the same page if it has a Quest file. A confirmed match: is its Quest file still live?
                    long id = check == MatchCheck.OwnPage ? mod.modioId : stored.modioId;
                    ModProfile? profile = null;
                    string error = null;
                    bool answered = false, gone = false;
                    yield return Request(current, () => nextRequest, t => nextRequest = t,
                                         () => ModioClient.GetMod(id, true, (p, e, g) => { profile = p; error = e; gone = g; answered = true; }), () => answered, c => live = c);
                    if(!live) yield break;
                    if(!answered || (error != null && !gone)) { errors++; Log.Warn(Defines.SERVER, "Quest match: mod.io " + id + " (" + mod.name + "): " + (error ?? "no answer")); continue; }
                    errors = 0;
                    bool quest = !gone && ModioClient.PlatformAvailability(profile.Value).Quest;
                    if(check == MatchCheck.OwnPage) {
                        Store(mod.folder, new ModMatch { modioId = quest ? mod.modioId : 0, checkedTicks = DateTime.UtcNow.Ticks });
                    } else {
                        Store(mod.folder, new ModMatch { modioId = stored.modioId, confirmed = true, checkedTicks = DateTime.UtcNow.Ticks, noQuestFile = !quest });
                    }
                    Log.Info(Defines.SERVER, "Quest match: " + mod.name + (check == MatchCheck.OwnPage ? " is" : " was matched to") + " mod.io " + id
                        + (gone ? ", which is gone" : " (\"" + profile.Value.name + "\" by " + profile.Value.creator.username + ", " + ModioClient.PlatformAvailability(profile.Value) + ")"));
                    continue;
                }

                ModioCandidate found = null;
                MatchScore score = MatchScore.None;
                bool failed = false;
                foreach(string phrase in ModMatcher.SearchPhrases(mod.name)) {
                    List<ModProfile> results = null;
                    string error = null;
                    bool answered = false;
                    yield return Request(current, () => nextRequest, t => nextRequest = t,
                                         () => ModioClient.Search(phrase, (list, e) => { results = list; error = e; answered = true; }), () => answered, c => live = c);
                    if(!live) yield break;
                    if(!answered || error != null) { failed = true; errors++; Log.Warn(Defines.SERVER, "Quest match: searching \"" + phrase + "\" failed: " + (error ?? "no answer")); break; }
                    errors = 0;
                    found = ModMatcher.Best(mod, results.Select(ToCandidate), out score);
                    if(found != null) break;
                }
                if(failed) continue;

                string taken = found != null ? ModMatcher.TakenBy(found.id, mod.folder, Matches(), mods) : null;
                if(taken == null && found != null && pending.Any(c => c.modio.id == found.id)) taken = pending.First(c => c.modio.id == found.id).folder;
                if(taken != null) {
                    Log.Info(Defines.SERVER, "Quest match: " + mod.name + " looks like mod.io " + found.id + " (\"" + found.name + "\"), but that is already the Quest version of " + taken + ".");
                    score = MatchScore.None;
                }

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
            if(generation.IsCurrent(current)) pass.TrySetResult(true);
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
