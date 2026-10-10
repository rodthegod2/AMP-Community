using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace AMP.ModSync {
    // Changes the Mods folder as described by a journal, and undoes it. Plain renames/moves inside the same drive, so it is fast.
    // Used in-game (Quest, and planning) and by the PC helper after the game has closed. Never throws.
    //
    // Crash safety: `persist` is called after every change of the journal. An op is marked `started` (and saved) before it touches the disk
    // and `done` after, and undoing is idempotent, so whatever moment the process dies, "Restore" can still put everything back.
    public static class JournalApplier {
        public const string OffPrefix      = "_AMPoff_";
        public const string OldPrefix      = "_AMPold_";      // older builds kept replaced mods inside Mods under this name
        public const string ConflictPrefix = "_AMPconflict_"; // something was in the way of a restore and was moved here (never deleted)
        public static int RetryMs = 30 * 1000; // antivirus / OneDrive can hold files briefly after the game exits (settable for tests)

        // What the helper should tell the player after a run (Keep leaves replaced mods outside Mods; conflicts were moved aside)
        public static string LastNote = "";

        // Replaced mods wait here, outside Mods (the game ignores it), until a Temporary swap ends
        public static string OldFolder(string cacheRoot, SwapJournal journal) {
            return Path.Combine(cacheRoot, "old", journal.id);
        }

        public static bool Apply(SwapJournal journal, string modsFolder, string cacheRoot, string journalDir, Action<SwapJournal> persist = null) {
            LastNote = "";
            string unsafeReason = Validate(journal);
            if(unsafeReason != null) return Fail(journal, unsafeReason);
            try {
                string problem = Precheck(journal, modsFolder, cacheRoot);
                if(problem != null) throw new IOException(problem);

                foreach(SwapOp op in journal.ops.Where(o => !o.done)) {
                    op.started = true;
                    Persist(persist, journal);
                    Do(op, journal, modsFolder, cacheRoot, journalDir);
                    op.done = true;
                    op.started = false;
                    Persist(persist, journal);
                }
                journal.state = SwapState.Applied;
                journal.lastError = "";
                Persist(persist, journal);
                LastNote = Note(journal, cacheRoot);
                return true;
            } catch(Exception e) {
                journal.lastError = e.Message;
                // Back to normal only if the rollback fully worked; otherwise startup must offer "Restore my mods"
                bool rolledBack = Undo(journal, modsFolder, cacheRoot, journalDir, true, persist);
                journal.state = rolledBack ? SwapState.Restored : SwapState.RestorePending;
                TryPersist(persist, journal);
                LastNote = Note(journal, cacheRoot);
                return false;
            }
        }

        // Temporary: undo everything. Keep: undo only Disable and LoadOrder.
        public static bool Restore(SwapJournal journal, string modsFolder, string cacheRoot, string journalDir, Action<SwapJournal> persist = null) {
            LastNote = "";
            string unsafeReason = Validate(journal);
            if(unsafeReason != null) return Fail(journal, unsafeReason);
            bool ok = Undo(journal, modsFolder, cacheRoot, journalDir, journal.mode == SwapMode.Temporary, persist);
            if(ok) {
                journal.state = SwapState.Restored;
            } else {
                journal.state = SwapState.RestorePending;
                journal.restoreFailures++;
            }
            TryPersist(persist, journal);
            LastNote = Note(journal, cacheRoot);
            return ok;
        }

        // Fails early, before anything moved, when a name is taken or a source is missing. Simulates the ops in order on the folder names.
        private static string Precheck(SwapJournal journal, string mods, string cacheRoot) {
            var names = new HashSet<string>(Directory.GetDirectories(mods).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            foreach(SwapOp op in journal.ops.Where(o => !o.done)) {
                string off = OffPrefix + op.folder;
                switch(op.kind) {
                    case SwapOpKind.Disable:
                        if(!names.Contains(op.folder)) return "The mod folder \"" + op.folder + "\" wasn't found.";
                        if(names.Contains(off)) return "The folder \"" + off + "\" already exists.";
                        names.Remove(op.folder);
                        names.Add(off);
                        break;
                    case SwapOpKind.MoveAside:
                        if(!names.Contains(op.folder)) return "The mod folder \"" + op.folder + "\" wasn't found.";
                        if(Directory.Exists(Path.Combine(OldFolder(cacheRoot, journal), op.folder))) return "A saved copy of \"" + op.folder + "\" already exists in " + OldFolder(cacheRoot, journal) + ".";
                        names.Remove(op.folder);
                        break;
                    case SwapOpKind.Install:
                        if(!Directory.Exists(Path.Combine(cacheRoot, "mods", op.hash, "content"))) return "The downloaded mod \"" + op.folder + "\" is missing from the cache.";
                        if(names.Contains(op.folder)) return "A mod folder named \"" + op.folder + "\" already exists.";
                        names.Add(op.folder);
                        break;
                }
            }
            return null;
        }

        private static bool Undo(SwapJournal journal, string modsFolder, string cacheRoot, string journalDir, bool everything, Action<SwapJournal> persist) {
            bool ok = true;
            for(int i = journal.ops.Count - 1; i >= 0; i--) {
                SwapOp op = journal.ops[i];
                if(!op.done && !op.started) continue; // never touched
                if(!everything && (op.kind == SwapOpKind.Install || op.kind == SwapOpKind.MoveAside)) continue;
                try {
                    UndoOne(op, journal, modsFolder, cacheRoot, journalDir);
                    op.done = false;
                    op.started = false;
                } catch(Exception e) {
                    journal.lastError = e.Message;
                    ok = false; // keep going: undo as much as possible, the journal remembers what's left
                }
                TryPersist(persist, journal);
            }
            return ok;
        }

        private static void Do(SwapOp op, SwapJournal journal, string mods, string cacheRoot, string journalDir) {
            switch(op.kind) {
                case SwapOpKind.Disable:   MoveDir(Path.Combine(mods, op.folder), Path.Combine(mods, OffPrefix + op.folder)); break;
                case SwapOpKind.MoveAside: {
                    string old = OldFolder(cacheRoot, journal);
                    Directory.CreateDirectory(old);
                    MoveDir(Path.Combine(mods, op.folder), Path.Combine(old, op.folder));
                    break;
                }
                case SwapOpKind.Install:   MoveDir(Path.Combine(cacheRoot, "mods", op.hash, "content"), Path.Combine(mods, op.folder)); break;
                case SwapOpKind.LoadOrder: {
                    string file = Path.Combine(mods, "loadorder.json");
                    Directory.CreateDirectory(journalDir);
                    journal.hadLoadOrder = File.Exists(file);
                    if(journal.hadLoadOrder) File.Copy(file, Path.Combine(journalDir, "loadorder.backup.json"), true);
                    File.WriteAllText(file, JsonConvert.SerializeObject(new { modNames = journal.hostLoadOrder }));
                    break;
                }
            }
        }

        // Idempotent: an op that was never performed, or already undone, is simply fine
        private static void UndoOne(SwapOp op, SwapJournal journal, string mods, string cacheRoot, string journalDir) {
            switch(op.kind) {
                case SwapOpKind.Disable:   MoveBack(Path.Combine(mods, OffPrefix + op.folder), Path.Combine(mods, op.folder), journal); break;
                case SwapOpKind.MoveAside: MoveBack(Path.Combine(OldFolder(cacheRoot, journal), op.folder), Path.Combine(mods, op.folder), journal); break;
                case SwapOpKind.Install: {
                    string content = Path.Combine(cacheRoot, "mods", op.hash, "content");
                    Directory.CreateDirectory(Path.GetDirectoryName(content));
                    MoveBack(Path.Combine(mods, op.folder), content, journal);
                    break;
                }
                case SwapOpKind.LoadOrder: {
                    string file = Path.Combine(mods, "loadorder.json");
                    string backup = Path.Combine(journalDir, "loadorder.backup.json");
                    if(journal.hadLoadOrder && File.Exists(backup)) File.Copy(backup, file, true);
                    else if(!journal.hadLoadOrder && File.Exists(file)) File.Delete(file);
                    break;
                }
            }
        }

        // Moves `from` to `to`. Source gone and target there: already done. Both there: the blocker is moved aside under a new name, never deleted.
        private static void MoveBack(string from, string to, SwapJournal journal) {
            bool hasFrom = Directory.Exists(from);
            bool hasTo   = Directory.Exists(to);
            if(!hasFrom) {
                if(hasTo) return;
                throw new IOException("Missing folder: " + from);
            }
            if(hasTo) {
                string aside = Path.Combine(Path.GetDirectoryName(to), ConflictPrefix + Path.GetFileName(to) + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
                MoveRetry(to, aside);
                journal.conflicts.Add(aside);
            }
            MoveRetry(from, to);
        }

        private static void MoveDir(string from, string to) {
            if(!Directory.Exists(from)) throw new IOException("Missing folder: " + from);
            if(Directory.Exists(to)) throw new IOException("Folder already exists: " + to);
            MoveRetry(from, to);
        }

        // Retries for a while: files can stay locked for a moment after the game exits
        private static void MoveRetry(string from, string to) {
            DateTime giveUp = DateTime.UtcNow.AddMilliseconds(RetryMs);
            while(true) {
                try {
                    Directory.Move(from, to);
                    return;
                } catch(IOException) when (DateTime.UtcNow < giveUp) {
                    Thread.Sleep(500);
                } catch(UnauthorizedAccessException) when (DateTime.UtcNow < giveUp) {
                    Thread.Sleep(500);
                }
            }
        }

        private static string Validate(SwapJournal journal) {
            if(journal.id == null || !Regex.IsMatch(journal.id, "^[0-9a-f]{1,64}$")) return "Unsafe journal id.";
            if(journal.ops.Any(o => !SafeFolder(o))) return "Unsafe folder name in journal.";
            return null;
        }

        private static bool SafeFolder(SwapOp op) {
            if(op.kind == SwapOpKind.LoadOrder) return true;
            string folder = op.folder ?? "";
            if(TransferRules.NormalizeRelativePath(folder) != folder || folder.Contains("/")) return false;
            if(op.kind == SwapOpKind.Install) return op.hash != null && op.hash.Length == 64 && op.hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
            return true;
        }

        private static string Note(SwapJournal journal, string cacheRoot) {
            var parts = new List<string>();
            if(journal.mode == SwapMode.Keep) {
                foreach(SwapOp op in journal.ops.Where(o => o.kind == SwapOpKind.MoveAside && o.done)) {
                    parts.Add("your previous version of " + op.folder + " is saved in " + Path.Combine(OldFolder(cacheRoot, journal), op.folder));
                }
            }
            foreach(string conflict in journal.conflicts) parts.Add("moved out of the way: " + conflict);
            return string.Join("; ", parts);
        }

        private static void Persist(Action<SwapJournal> persist, SwapJournal journal) {
            if(persist != null) persist(journal);
        }

        // While rolling back a failed save must not stop the rollback itself
        private static void TryPersist(Action<SwapJournal> persist, SwapJournal journal) {
            try { Persist(persist, journal); } catch(Exception) { }
        }

        private static bool Fail(SwapJournal journal, string error) {
            journal.lastError = error;
            return false;
        }

        // Entry point for the PC helper (PowerShell). Returns "" on success, "busy" if another helper holds the lock, or an error text.
        // Writes the journal back after every change.
        public static string RunFromHelper(string journalPath, string modsFolder, string cacheRoot, string action) {
            FileStream lockFile = null;
            try {
                string journalDir = Path.GetDirectoryName(journalPath);
                Directory.CreateDirectory(journalDir);
                try {
                    lockFile = new FileStream(Path.Combine(journalDir, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                } catch(IOException) {
                    return "busy";
                }

                SwapJournal journal = SwapJournal.LoadFrom(journalPath);
                if(journal == null) return "Journal unreadable.";
                Action<SwapJournal> persist = j => j.SaveTo(journalPath);
                bool ok = action == "apply" ? Apply(journal, modsFolder, cacheRoot, journalDir, persist)
                                            : Restore(journal, modsFolder, cacheRoot, journalDir, persist);
                return ok ? "" : journal.lastError;
            } catch(Exception e) {
                return e.Message;
            } finally {
                if(lockFile != null) lockFile.Dispose();
            }
        }
    }
}
