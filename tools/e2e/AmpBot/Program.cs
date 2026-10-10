using AMP.ModSync;
using Netamite.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AmpBot {
    // Usage:
    //   AmpBot --host <port> --mods <folder> [--cache <folder>] [--expect <file.json>]
    //   AmpBot --join <address> <port> --mods <folder> [--cache <folder>] [--expect <file.json>]
    //   AmpBot --selftest [<AMP repo folder>]   packet layouts vs AMP's sources + a host/joiner loopback
    // Prints BOT INFO/OK/FAIL/DONE lines; exit code 0 = every expectation was met, 1 = failure, 2 = bad usage.
    public static class Program {
        private static int noImplementation;

        public static int Main(string[] args) {
            // Netamite's console logger needs a real console window; packets the bot doesn't know (positions, items...) are only counted
            Log.loggerType = Log.LoggerType.EVENT_ONLY;
            Log.onLogMessage += (type, message) => {
                if(message != null && message.Contains("No implementation for packet id")) { Interlocked.Increment(ref noImplementation); return; }
                if(type >= Log.Type.WARNING) Out.Info("netamite " + type + ": " + message);
            };

            try {
                int code = Run(args);
                Environment.Exit(code); // background network threads must not keep the process alive
                return code;
            } catch(Exception e) {
                Out.Line("FAIL", "crashed: " + e);
                Environment.Exit(1);
                return 1;
            }
        }

        private static int Run(string[] args) {
            var a = new List<string>(args);
            if(a.Count >= 1 && a[0] == "--selftest") return SelfTest.Run(a.Count > 1 ? a[1] : null);

            string mods = Value(a, "--mods", 1), cache = Value(a, "--cache", 1), expect = Value(a, "--expect", 1);
            string host = Value(a, "--host", 1);
            string join = Value(a, "--join", 1), joinPort = Value(a, "--join", 2);
            int port;
            if(mods == null || (host == null) == (join == null) || !int.TryParse(host ?? joinPort, out port) || port < 1 || port > 32767) {
                Console.Error.WriteLine("usage: AmpBot --host <port> | --join <address> <port>  --mods <folder> [--cache <folder>] [--expect <file.json>]");
                return 2;
            }
            if(!Directory.Exists(mods)) { Out.Line("FAIL", "mods folder not found: " + mods); return 1; }
            cache = cache ?? Path.Combine(Path.GetTempPath(), "AmpBot-cache-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(cache);

            Expectations x = Expectations.Load(expect);
            var list = new Checklist();
            DateTime deadline = DateTime.UtcNow.AddSeconds(x.timeoutSec);
            bool ok;
            if(host != null) {
                var hoster = new Hoster(port, mods, cache, x, list);
                hoster.Start();
                ok = list.Wait(deadline - DateTime.UtcNow);
                if(ok) Thread.Sleep(x.lingerSec * 1000);
                hoster.Stop();
            } else {
                var joiner = new Joiner(join, port, mods, cache, x, list);
                if(!joiner.Connect(deadline)) list.Fail(list.Failure ?? "couldn't connect to " + join + ":" + port);
                ok = list.Wait(deadline - DateTime.UtcNow);
                if(ok) { joiner.Done(); Thread.Sleep(x.lingerSec * 1000); }
                joiner.StopPlaying();
                joiner.Disconnect();
            }
            return Finish(ok, list);
        }

        internal static int Finish(bool ok, Checklist list) {
            Out.Info(noImplementation + " packets of types the bot doesn't handle were ignored");
            if(ok) { Out.Line("DONE", "all expectations met"); return 0; }
            Out.Line("FAIL", list.Failure);
            return 1;
        }

        // The value n places after a flag, or null
        private static string Value(List<string> args, string flag, int n) {
            int i = args.IndexOf(flag);
            return i >= 0 && i + n < args.Count ? args[i + n] : null;
        }
    }
}
