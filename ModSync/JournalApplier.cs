using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace AMP.ModSync {
    // Changes the Mods folder as described by a journal, and undoes it. Plain renames/moves inside the same drive, so it is fast.
    // Used in-game (Quest, and planning) and by the PC helper after the game has closed. Never throws.
    public static class JournalApplier {
        public const string OffPrefix = "_AMPoff_";
        public const string OldPrefix = "_AMPold_";
        public static int RetryMs = 30 * 1000; // antivirus / OneDrive can hold files briefly after the game exits (settable for tests)

        public static bool Apply(SwapJournal journal, string modsFolder, string cacheRoot, string journalDir) {
            if(journal.ops.Any(o => !SafeFolder(o))) return Fail(journal, "Unsafe folder name in journal.");
            try {
                foreach(SwapOp op in journal.ops.Where(o => !o.done)) {
                    Do(op, journal, modsFolder, cacheRoot, journalDir);
                    op.done = true;
                }
                journal.state = SwapState.Applied;
                journal.lastError = "";
                return true;
            } catch(Exception e) {
                journal.lastError = e.Message;
                // Back to normal only if the rollback fully worked; otherwise startup must offer "Restore my mods"
                bool rolledBack = Undo(journal, modsFolder, cacheRoot, journalDir, true);
                journal.state = rolledBack ? SwapState.Restored : SwapState.RestorePending;
                return false;
            }
        }

        // Temporary: undo everything. Keep: undo only Disable and LoadOrder.
        public static bool Restore(SwapJournal journal, string modsFolder, string cacheRoot, string journalDir) {
            if(journal.ops.Any(o => !SafeFolder(o))) return Fail(journal, "Unsafe folder name in journal.");
            bool ok = Undo(journal, modsFolder, cacheRoot, journalDir, journal.mode == SwapMode.Temporary);
            journal.state = ok ? SwapState.Restored : journal.state;
            return ok;
        }

        private static bool Undo(SwapJournal journal, string modsFolder, string cacheRoot, string journalDir, bool everything) {
            bool ok = true;
            for(int i = journal.ops.Count - 1; i >= 0; i--) {
                SwapOp op = journal.ops[i];
                if(!op.done) continue;
                if(!everything && (op.kind == SwapOpKind.Install || op.kind == SwapOpKind.MoveAside)) continue;
                try {
                    UndoOne(op, journal, modsFolder, cacheRoot, journalDir);
                    op.done = false;
                } catch(Exception e) {
                    journal.lastError = e.Message;
                    ok = false; // keep going: undo as much as possible, the journal remembers what's left
                }
            }
            return ok;
        }

        private static void Do(SwapOp op, SwapJournal journal, string mods, string cacheRoot, string journalDir) {
            switch(op.kind) {
                case SwapOpKind.Disable:   MoveDir(Path.Combine(mods, op.folder), Path.Combine(mods, OffPrefix + op.folder)); break;
                case SwapOpKind.MoveAside: MoveDir(Path.Combine(mods, op.folder), Path.Combine(mods, OldPrefix + op.folder)); break;
                case SwapOpKind.Install:   MoveDir(Path.Combine(cacheRoot, "mods", op.hash, "content"), Path.Combine(mods, op.folder)); break;
                case SwapOpKind.LoadOrder: {
                    string file = Path.Combine(mods, "loadorder.json");
                    Directory.CreateDirectory(journalDir);
                    journal.hadLoadOrder = File.Exists(file);
                    if(journal.hadLoadOrder) File.Copy(file, Path.Combine(journalDir, "loadorder.backup.json"), true);
                    File.WriteAllText(file, "{\"modNames\":[" + string.Join(",", journal.hostLoadOrder.Select(n => "\"" + n.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]}");
                    break;
                }
            }
        }

        private static void UndoOne(SwapOp op, SwapJournal journal, string mods, string cacheRoot, string journalDir) {
            switch(op.kind) {
                case SwapOpKind.Disable:   MoveDir(Path.Combine(mods, OffPrefix + op.folder), Path.Combine(mods, op.folder)); break;
                case SwapOpKind.MoveAside: MoveDir(Path.Combine(mods, OldPrefix + op.folder), Path.Combine(mods, op.folder)); break;
                case SwapOpKind.Install: {
                    string content = Path.Combine(cacheRoot, "mods", op.hash, "content");
                    Directory.CreateDirectory(Path.GetDirectoryName(content));
                    MoveDir(Path.Combine(mods, op.folder), content);
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

        // Retries for a while: files can stay locked for a moment after the game exits
        private static void MoveDir(string from, string to) {
            if(!Directory.Exists(from)) throw new IOException("Missing folder: " + from);
            if(Directory.Exists(to)) throw new IOException("Folder already exists: " + to);
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

        private static bool SafeFolder(SwapOp op) {
            if(op.kind == SwapOpKind.LoadOrder) return true;
            string folder = op.folder ?? "";
            if(TransferRules.NormalizeRelativePath(folder) != folder || folder.Contains("/")) return false;
            if(op.kind == SwapOpKind.Install) return op.hash != null && op.hash.Length == 64 && op.hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
            return true;
        }

        private static bool Fail(SwapJournal journal, string error) {
            journal.lastError = error;
            return false;
        }

        // Entry point for the PC helper (PowerShell). Returns "" on success or an error text. Writes the updated journal back.
        public static string RunFromHelper(string journalPath, string modsFolder, string cacheRoot, string action) {
            try {
                SwapJournal journal = SwapJournal.FromJson(File.ReadAllText(journalPath));
                if(journal == null) return "Journal unreadable.";
                string journalDir = Path.GetDirectoryName(journalPath);
                bool ok = action == "apply" ? Apply(journal, modsFolder, cacheRoot, journalDir)
                                            : Restore(journal, modsFolder, cacheRoot, journalDir);
                File.WriteAllText(journalPath, journal.ToJson());
                return ok ? "" : journal.lastError;
            } catch(Exception e) {
                return e.Message;
            }
        }
    }
}
