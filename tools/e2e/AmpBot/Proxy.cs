using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace AmpBot {
    // --proxy <listen port> <target host> <target port> [--latency-ms 75] [--loss 0.02] [--stall-every 60] [--stall-ms 5000] [--minutes 60]
    // A bad internet line between the game and the bot, TCP and UDP on the same port like Netamite:
    //  - every packet/chunk is delayed by latency-ms in each direction (75 ms each way = 150 ms round trip), order kept
    //  - UDP datagrams are dropped with probability loss; TCP can't lose bytes, so a "lost" TCP chunk arrives 3 x latency later
    //    (what a retransmission costs) and everything behind it waits, like real TCP head-of-line blocking
    //  - every stall-every seconds nothing passes for stall-ms (TCP held back, UDP dropped)
    public sealed class Proxy {
        private readonly int listenPort, targetPort, latencyMs, stallEverySec, stallMs;
        private readonly string targetHost;
        private readonly double loss;
        private readonly Random random = new Random(1234);
        private readonly DateTime started = DateTime.UtcNow;
        private long tcpChunks, tcpDelayed, udpPackets, udpDropped, stalls;
        private volatile bool stopped;

        public Proxy(int listenPort, string targetHost, int targetPort, int latencyMs, double loss, int stallEverySec, int stallMs) {
            this.listenPort = listenPort; this.targetHost = targetHost; this.targetPort = targetPort;
            this.latencyMs = latencyMs; this.loss = loss; this.stallEverySec = stallEverySec; this.stallMs = stallMs;
        }

        public string Stats() {
            return "tcp chunks " + Interlocked.Read(ref tcpChunks) + " (" + Interlocked.Read(ref tcpDelayed) + " delayed as lost), udp " + Interlocked.Read(ref udpPackets)
                 + " (" + Interlocked.Read(ref udpDropped) + " dropped), " + Interlocked.Read(ref stalls) + " stalls";
        }

        // When the current stall ends (MinValue = not stalled)
        private DateTime StallEnds(DateTime now) {
            if(stallEverySec <= 0 || stallMs <= 0) return DateTime.MinValue;
            double t = (now - started).TotalMilliseconds % (stallEverySec * 1000.0);
            double from = stallEverySec * 1000.0 - stallMs; // the last stall-ms of every period
            return t >= from ? now.AddMilliseconds(stallEverySec * 1000.0 - t) : DateTime.MinValue;
        }

        private bool Lost() { lock(random) return random.NextDouble() < loss; }

        public void Run(TimeSpan duration) {
            var tcp = new TcpListener(IPAddress.Loopback, listenPort);
            tcp.Start();
            var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, listenPort));
            new Thread(() => AcceptLoop(tcp)) { IsBackground = true, Name = "proxy accept" }.Start();
            new Thread(() => UdpLoop(udp)) { IsBackground = true, Name = "proxy udp" }.Start();
            new Thread(StallCounter) { IsBackground = true }.Start();
            Out.Info("proxy 127.0.0.1:" + listenPort + " -> " + targetHost + ":" + targetPort + ", latency " + latencyMs + " ms each way, loss " + loss + ", stall " + stallMs + " ms every " + stallEverySec + " s");
            DateTime until = DateTime.UtcNow + duration;
            while(DateTime.UtcNow < until) { Thread.Sleep(10000); Out.Info("proxy: " + Stats()); }
            stopped = true;
            try { tcp.Stop(); } catch(Exception) { }
            try { udp.Close(); } catch(Exception) { }
        }

        private void StallCounter() {
            bool was = false;
            while(!stopped) {
                bool now = StallEnds(DateTime.UtcNow) != DateTime.MinValue;
                if(now && !was) { Interlocked.Increment(ref stalls); Out.Info("proxy: stall for " + stallMs + " ms"); }
                was = now;
                Thread.Sleep(50);
            }
        }

        // ---- TCP: one delay line per direction ----

        private void AcceptLoop(TcpListener listener) {
            while(!stopped) {
                TcpClient inbound;
                try { inbound = listener.AcceptTcpClient(); } catch(Exception) { return; }
                try {
                    var outbound = new TcpClient();
                    outbound.Connect(targetHost, targetPort);
                    inbound.NoDelay = true; outbound.NoDelay = true;
                    Out.Info("proxy: tcp connection " + inbound.Client.RemoteEndPoint + " -> " + targetHost + ":" + targetPort);
                    Pump(inbound, outbound, "up");
                    Pump(outbound, inbound, "down");
                } catch(Exception e) {
                    Out.Info("proxy: can't reach the target: " + e.Message);
                    inbound.Close();
                }
            }
        }

        private void Pump(TcpClient from, TcpClient to, string name) {
            var line = new BlockingCollection<KeyValuePair<DateTime, byte[]>>();
            new Thread(() => {
                var buffer = new byte[64 * 1024];
                DateTime lastDue = DateTime.MinValue;
                try {
                    NetworkStream s = from.GetStream();
                    while(true) {
                        int n = s.Read(buffer, 0, buffer.Length);
                        if(n <= 0) break;
                        var chunk = new byte[n];
                        Array.Copy(buffer, chunk, n);
                        Interlocked.Increment(ref tcpChunks);
                        DateTime due = DateTime.UtcNow.AddMilliseconds(latencyMs);
                        if(Lost()) { due = due.AddMilliseconds(3 * latencyMs); Interlocked.Increment(ref tcpDelayed); }
                        if(due < lastDue) due = lastDue; // a stream keeps its order
                        lastDue = due;
                        line.Add(new KeyValuePair<DateTime, byte[]>(due, chunk));
                    }
                } catch(Exception) { }
                line.CompleteAdding();
            }) { IsBackground = true, Name = "proxy read " + name }.Start();
            new Thread(() => {
                try {
                    NetworkStream s = to.GetStream();
                    foreach(KeyValuePair<DateTime, byte[]> item in line.GetConsumingEnumerable()) {
                        WaitUntil(item.Key);
                        s.Write(item.Value, 0, item.Value.Length);
                    }
                } catch(Exception) { }
                try { to.Close(); } catch(Exception) { }
                try { from.Close(); } catch(Exception) { }
            }) { IsBackground = true, Name = "proxy write " + name }.Start();
        }

        // Waits until due, and through any stall that is going on then
        private void WaitUntil(DateTime due) {
            while(true) {
                DateTime now = DateTime.UtcNow;
                if(now < due) { Thread.Sleep(Math.Max(1, (int) Math.Min(50, (due - now).TotalMilliseconds))); continue; }
                DateTime stallEnd = StallEnds(now);
                if(stallEnd == DateTime.MinValue) return;
                Thread.Sleep(Math.Max(1, (int) Math.Min(50, (stallEnd - now).TotalMilliseconds)));
            }
        }

        // ---- UDP: one upstream socket per client endpoint (Netamite links UDP to a client by the id inside the packets) ----

        private readonly ConcurrentDictionary<string, UdpClient> upstreams = new ConcurrentDictionary<string, UdpClient>();

        private void UdpLoop(UdpClient listen) {
            while(!stopped) {
                IPEndPoint client = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try { data = listen.Receive(ref client); } catch(SocketException) { if(stopped) return; continue; } catch(ObjectDisposedException) { return; }
                IPEndPoint from = client;
                UdpClient up = upstreams.GetOrAdd(from.ToString(), _ => {
                    var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    u.Connect(targetHost, targetPort);
                    new Thread(() => {
                        while(!stopped) {
                            IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
                            byte[] back;
                            try { back = u.Receive(ref any); } catch(Exception) { return; }
                            Delayed(back, b => { try { listen.Send(b, b.Length, from); } catch(Exception) { } });
                        }
                    }) { IsBackground = true, Name = "proxy udp down" }.Start();
                    return u;
                });
                Delayed(data, b => { try { up.Send(b, b.Length); } catch(Exception) { } });
            }
        }

        private void Delayed(byte[] data, Action<byte[]> send) {
            Interlocked.Increment(ref udpPackets);
            if(Lost() || StallEnds(DateTime.UtcNow) != DateTime.MinValue) { Interlocked.Increment(ref udpDropped); return; }
            if(latencyMs <= 0) { send(data); return; }
            var timer = new Timer(_ => send(data), null, latencyMs, Timeout.Infinite);
            GC.KeepAlive(timer);
            lock(timers) { timers.Add(timer); if(timers.Count > 4096) timers.RemoveRange(0, 2048); }
        }

        private readonly List<Timer> timers = new List<Timer>(); // keeps pending UDP timers from being collected before they fire
    }
}
