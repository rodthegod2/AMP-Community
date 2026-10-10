using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AmpBot {
    // Everything the bot prints starts with "BOT " so the runner can grep it: BOT INFO / BOT OK <item> / BOT FAIL <reason> / BOT DONE
    public static class Out {
        private static readonly object gate = new object();
        public static string Prefix = "BOT ";

        public static void Line(string kind, string text) {
            lock(gate) {
                Console.WriteLine(Prefix + kind + " " + (text ?? "").Replace("\r", " ").Replace("\n", " | "));
                Console.Out.Flush();
            }
        }

        public static void Info(string text) { Line("INFO", text); }
    }

    // The named things a run has to see; finished when all are seen or one failure happened
    public class Checklist {
        private readonly object gate = new object();
        private readonly List<string> pending = new List<string>();
        private readonly ManualResetEvent changed = new ManualResetEvent(false);
        public string Failure { get; private set; }

        public void Expect(string item) { lock(gate) if(!pending.Contains(item)) pending.Add(item); }

        public bool IsPending(string item) { lock(gate) return pending.Contains(item); }

        public void Mark(string item, string detail = null) {
            lock(gate) {
                if(!pending.Remove(item)) return;
                Out.Line("OK", item + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
                changed.Set();
            }
        }

        public void Fail(string reason) {
            lock(gate) {
                if(Failure != null) return;
                Failure = reason;
                changed.Set();
            }
        }

        // True when every item was seen; false on failure or timeout (Failure says why)
        public bool Wait(TimeSpan timeout) {
            DateTime until = DateTime.UtcNow + timeout;
            while(true) {
                lock(gate) {
                    if(Failure != null) return false;
                    if(pending.Count == 0) return true;
                    changed.Reset();
                }
                TimeSpan left = until - DateTime.UtcNow;
                if(left <= TimeSpan.Zero) {
                    lock(gate) Failure = "timed out waiting for: " + string.Join(", ", pending);
                    return false;
                }
                changed.WaitOne(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
            }
        }
    }
}
