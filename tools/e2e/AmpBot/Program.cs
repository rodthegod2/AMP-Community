using AMP.ModSync;
using Netamite.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AmpBot {
    // Usage:
    //   AmpBot --host <port> --mods <folder> [--cache <folder>] [--expect <file.json>]
    //   AmpBot --join <address> <port> --mods <folder> [--cache <folder>] [--expect <file.json>]
    //   AmpBot --join-code-file <file> ...   joins with the join code the game writes into file (autopilot writeJoinCode)
    //   ... --fake-igd <ssdp port>           also runs a fake UPnP router (Tests/ModSyncTests/FakeIgd.cs) and checks the game's mappings
    //   ... --fake-igd-lease <s>             the lease the game must ask for (default 7200); with a short one, at least 3 renewals are required
    //   ... --proxy-port <p> [--latency-ms 75] [--loss 0.02] [--stall-every 60] [--stall-ms 5000]
    //                                        a bad line (Proxy.cs) on 127.0.0.1:<p>: host mode puts it in front of the bot's port (the game
    //                                        joins <p>), join mode in front of the game's port (the bot connects through it)
    //   ... --fuzz                           join: first a hostile joiner (Fuzz.cs), then the normal join with --expect
    //   ... --owner-race                     join as two players that grab the same item at once, many times (OwnerRace.cs)
    //   ... --soak <minutes> [--rejoin-every <s>]   join: stay for that long, playing, and disconnect + rejoin every <s> (default 600);
    //                                        every round must meet the gameplay expectations again
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
            string codeFile = Value(a, "--join-code-file", 1), fakeIgdPort = Value(a, "--fake-igd", 1);
            int soakMin = int.Parse(Value(a, "--soak", 1) ?? "0"), rejoinSec = int.Parse(Value(a, "--rejoin-every", 1) ?? "600");
            int igdLease = int.Parse(Value(a, "--fake-igd-lease", 1) ?? "7200");
            if(codeFile != null) { join = "(code)"; joinPort = "1"; }
            int port, ssdpPort = 0;
            if(mods == null || (host == null) == (join == null) || !int.TryParse(host ?? joinPort, out port) || port < 1 || port > 32767
               || (fakeIgdPort != null && (!int.TryParse(fakeIgdPort, out ssdpPort) || ssdpPort < 1 || ssdpPort > 65535))) {
                Console.Error.WriteLine("usage: AmpBot --host <port> | --join <address> <port> | --join-code-file <file>  --mods <folder> [--cache <folder>] [--expect <file.json>] [--fake-igd <ssdp port>]");
                return 2;
            }
            if(!Directory.Exists(mods)) { Out.Line("FAIL", "mods folder not found: " + mods); return 1; }
            Proxy proxy = null;
            if(Value(a, "--proxy-port", 1) != null) {
                int proxyPort = int.Parse(Value(a, "--proxy-port", 1));
                proxy = new Proxy(proxyPort, "127.0.0.1", port, int.Parse(Value(a, "--latency-ms", 1) ?? "75"), double.Parse(Value(a, "--loss", 1) ?? "0.02", System.Globalization.CultureInfo.InvariantCulture),
                                  int.Parse(Value(a, "--stall-every", 1) ?? "60"), int.Parse(Value(a, "--stall-ms", 1) ?? "5000"));
                new Thread(() => proxy.Run(TimeSpan.FromHours(3))) { IsBackground = true, Name = "proxy" }.Start();
                if(host == null) port = proxyPort; // the bot joins through the proxy
            }
            cache = cache ?? Path.Combine(Path.GetTempPath(), "AmpBot-cache-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(cache);

            Expectations x = Expectations.Load(expect);
            var list = new Checklist();
            DateTime deadline = DateTime.UtcNow.AddSeconds(x.timeoutSec);
            // A fake UPnP router on 127.0.0.1; its internet address is 203.0.113.7 (TEST-NET-3, public for AMP)
            FakeIgd igd = fakeIgdPort == null ? null : new FakeIgd(ssdpPort);
            if(igd != null) {
                Out.Info("fake UPnP router answering SSDP on 127.0.0.1:" + ssdpPort);
                igd.Trace = call => Out.Info("fake router call: " + call);
            }
            if(codeFile != null && !JoinByCode(codeFile, deadline, out join, out port)) return Finish(false, list);
            bool ok;
            if(host != null) {
                var hoster = new Hoster(port, mods, cache, x, list);
                hoster.Start();
                ok = list.Wait(deadline - DateTime.UtcNow);
                if(ok) Thread.Sleep(x.lingerSec * 1000);
                hoster.Stop();
            } else if(a.Contains("--fuzz") && !new Fuzzer(join, port, mods, cache, x).Run(list)) {
                ok = false;
            } else if(a.Contains("--owner-race")) {
                ok = new OwnerRace(join, port).Run(list);
            } else if(soakMin > 0) {
                ok = Soak(join, port, mods, cache, x, list, soakMin, rejoinSec);
            } else {
                var joiner = new Joiner(join, port, mods, cache, x, list);
                if(!joiner.Connect(deadline)) list.Fail(list.Failure ?? "couldn't connect to " + join + ":" + port);
                ok = list.Wait(deadline - DateTime.UtcNow);
                if(ok) { joiner.Done(); Thread.Sleep(x.lingerSec * 1000); }
                joiner.StopPlaying();
                joiner.Disconnect();
            }
            if(ok && igd != null) ok = CheckFakeIgd(igd, port, list, igdLease, soakMin > 0 ? 900 : 120);
            igd?.Dispose();
            if(proxy != null) Out.Info("proxy totals: " + proxy.Stats());
            return Finish(ok, list);
        }

        // The game writes its join code into the file once it hosts; the bot reads it like a friend would type it
        private static bool JoinByCode(string file, DateTime deadline, out string host, out int port) {
            host = null; port = 0;
            Out.Info("waiting for the join code in " + file);
            while(!File.Exists(file)) {
                if(DateTime.UtcNow > deadline) { Out.Line("FAIL", "no join code appeared in " + file); return false; }
                Thread.Sleep(250);
            }
            string code = File.ReadAllText(file).Trim(), error;
            if(!AMP.Network.Nat.JoinTarget.TryParse(code, "", out host, out port, out error)) { Out.Line("FAIL", "join code " + code + ": " + error); return false; }
            Out.Info("join code " + code + " -> " + host + ":" + port);
            return true;
        }

        // Soak: rounds of join, play, check, leave, until the time is up. Each round gets its own checklist (the same expectations every time).
        private static bool Soak(string address, int port, string mods, string cache, Expectations x, Checklist overall, int minutes, int rejoinSec) {
            DateTime end = DateTime.UtcNow.AddMinutes(minutes);
            int round = 0;
            while(DateTime.UtcNow < end) {
                round++;
                var list = new Checklist();
                var joiner = new Joiner(address, port, mods, cache, x, list);
                Out.Info("SOAK round " + round + ": joining");
                if(!joiner.Connect(DateTime.UtcNow.AddMinutes(3))) { overall.Fail("soak round " + round + ": couldn't connect: " + list.Failure); return false; }
                if(!list.Wait(TimeSpan.FromMinutes(5))) { overall.Fail("soak round " + round + ": " + list.Failure); joiner.Disconnect(); return false; }
                DateTime leave = DateTime.UtcNow.AddSeconds(rejoinSec);
                if(leave > end) leave = end;
                while(DateTime.UtcNow < leave && list.Failure == null) Thread.Sleep(1000);
                if(list.Failure != null) { overall.Fail("soak round " + round + " (while playing): " + list.Failure); joiner.Disconnect(); return false; }
                joiner.Done();
                Out.Info("SOAK round " + round + " ok: " + joiner.Summary());
                joiner.StopPlaying();
                joiner.Disconnect();
                Thread.Sleep(3000);
            }
            Out.Line("OK", "soak: " + round + " rounds in " + minutes + " min, every round met the expectations");
            return true;
        }

        // The game mapped TCP and UDP for its port to this PC through the fake router, and removed both when it stopped hosting.
        // A short lease must have been renewed at least 3 times (every renewal is in the log as a "fake router call" line with its time).
        private static bool CheckFakeIgd(FakeIgd igd, int port, Checklist list, int lease, int waitSec) {
            string[] wanted = { "AddPortMapping TCP " + port + " 127.0.0.1 " + lease + " AMP Community", "AddPortMapping UDP " + port + " 127.0.0.1 " + lease + " AMP Community" };
            DateTime until = DateTime.UtcNow.AddSeconds(waitSec); // the game removes them when it quits
            while(DateTime.UtcNow < until && !(igd.MappingCount() == 0 && igd.CallList().Contains("DeletePortMapping UDP " + port))) Thread.Sleep(250);
            string[] calls = igd.CallList();
            Out.Info("fake router calls: " + string.Join("; ", calls));
            string missing = wanted.Concat(new[] { "DeletePortMapping TCP " + port, "DeletePortMapping UDP " + port }).FirstOrDefault(c => !calls.Contains(c));
            if(missing != null || igd.MappingCount() != 0) { list.Fail("fake router: " + (missing != null ? "never got \"" + missing + "\"" : igd.MappingCount() + " mapping(s) left")); return false; }
            int renewTcp = calls.Count(c => c == wanted[0]) - 1, renewUdp = calls.Count(c => c == wanted[1]) - 1;
            if(lease < 7200 && (renewTcp < 3 || renewUdp < 3)) { list.Fail("fake router: only " + renewTcp + " TCP / " + renewUdp + " UDP renewals of the " + lease + " s lease"); return false; }
            Out.Line("OK", "fake router: TCP+UDP " + port + " mapped with lease " + lease + ", renewed " + renewTcp + " (TCP) / " + renewUdp + " (UDP) times, removed again");
            return true;
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
