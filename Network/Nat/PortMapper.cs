using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;

namespace AMP.Network.Nat {
    public enum MapState {
        Off,        // turned off (config, or a test run without a fake router)
        Searching,
        NoRouter,   // nothing answered the UPnP search
        Open,       // the port is open on the router (by AMP, or already forwarded to this PC by hand)
        Refused,    // the router answered but didn't open the port
        Shared,     // the router's internet address is private/CGNAT: nobody on the internet can reach it
        NoInternet, // the router has no internet address
        Failed,     // the router stopped answering or answered nonsense
    }

    // What the UI shows; replaced as a whole (never changed in place), so the main thread can read it any time
    public class PortMapStatus {
        public MapState State = MapState.Searching;
        public int Port;
        public IPAddress LocalIp, ExternalIp;
        public string Router = "", ServiceType = "", Detail = "";
        public int LeaseSeconds;       // of the mappings AMP made (0 = until AMP removes them)
        public bool ForwardedByHand;   // the port was already forwarded to this PC (AMP leaves that mapping alone)

        public PortMapStatus With(Action<PortMapStatus> change) {
            var copy = (PortMapStatus) MemberwiseClone();
            change(copy);
            return copy;
        }

        // A code friends on the internet can use: only when the port is open and the router has a public address
        public string InternetCode {
            get { return ExternalIp != null && NatRules.Classify(ExternalIp) == AddressClass.Public && State == MapState.Open ? JoinCodes.Encode(ExternalIp, Port) : null; }
        }

        // Plain words for the host, one line each
        public List<string> Describe() {
            var lines = new List<string>();
            switch(State) {
                case MapState.Off:
                    lines.Add("Automatic port opening is off" + (Detail.Length > 0 ? " (" + Detail + ")" : "") + ". Internet friends need port " + Port + " forwarded by hand.");
                    break;
                case MapState.Searching:
                    lines.Add("Opening port " + Port + " on your router...");
                    break;
                case MapState.NoRouter:
                    lines.Add("No router answered the automatic port opening (UPnP is off or not supported).");
                    lines.Add("Internet joining: turn on UPnP in your router, or forward port " + Port + " (TCP+UDP) to this PC by hand.");
                    break;
                case MapState.Open:
                    lines.Add(ForwardedByHand ? "Port " + Port + " is already forwarded to this PC on your router." : "Port " + Port + " was opened on your router automatically.");
                    break;
                case MapState.Refused:
                    lines.Add("Your router refused to open port " + Port + (Detail.Length > 0 ? ": " + Detail : "") + ".");
                    lines.Add("Forward port " + Port + " (TCP+UDP) to this PC by hand, then host again to get an Internet code.");
                    break;
                case MapState.Shared:
                    lines.Add("Your internet provider (or a second router in front of yours) shares your IP: internet joining needs a relay (same Wi-Fi still works).");
                    break;
                case MapState.NoInternet:
                    lines.Add("Your router reports no usable internet address: only same Wi-Fi joining works.");
                    break;
                case MapState.Failed:
                    lines.Add("Automatic port opening failed" + (Detail.Length > 0 ? ": " + Detail : "") + ".");
                    break;
            }
            lines.Add("Windows may ask to allow Blade & Sorcery through the firewall: allow it.");
            return lines;
        }
    }

    // One hosting session's port mappings, on its own background thread:
    //   discover -> external IP -> classify -> map TCP and UDP (lease, fallback 0) -> renew every lease/2 -> on Stop delete what it added.
    // A mapping that already exists for this PC with another description (forwarded by hand) is used as it is and never deleted;
    // a mapping to another device is reported, never replaced.
    public class PortMapper {
        public const string Description = "AMP Community";
        public static readonly string[] Protocols = { "TCP", "UDP" };

        public readonly int Port;
        public int LeaseSeconds = 7200;
        public int DiscoveryTimeoutMs = 3000;
        public int CallTimeoutMs = 5000;  // per request to the router
        public const int MaxDevices = 8;  // device descriptions read per search (a busy network can't keep the thread busy)
        public IPEndPoint SearchTarget = Ssdp.Multicast;

        private volatile PortMapStatus status;
        public PortMapStatus Status { get { return status; } }

        private readonly Action<string> log;
        private readonly ManualResetEvent stopRequested = new ManualResetEvent(false);
        private readonly ManualResetEvent done = new ManualResetEvent(false);
        private readonly Dictionary<string, int> added = new Dictionary<string, int>(); // protocol -> lease of the mappings this session made (only these get deleted)
        private string offReason;
        private Thread thread;
        private IgdClient igd;
        public int Renewals { get; private set; }

        public PortMapper(int port, Action<string> log) {
            Port = port;
            this.log = log ?? (s => { });
            status = new PortMapStatus { State = MapState.Searching, Port = port };
        }

        // previous: a session that is still cleaning up. A new host start waits for all of its deletes (each bounded by the request
        // timeout), so an old delete can never remove the new session's mapping on the same port.
        public void Start(PortMapper previous = null) {
            thread = new Thread(() => {
                try {
                    previous?.WaitDone(Timeout.Infinite);
                    IPAddress lan = LocalAddress.Lan(); // reads the adapter list: on this thread, fresh for every host start
                    status = status.With(s => s.LocalIp = lan);
                    if(offReason != null) { status = status.With(s => { s.State = MapState.Off; s.Detail = offReason; }); return; }
                    Run();
                } catch(Exception e) {
                    log("UPnP: " + e);
                    status = status.With(s => { s.State = MapState.Failed; s.Detail = e.Message; });
                } finally {
                    Cleanup();
                    done.Set();
                }
            }) { IsBackground = true, Name = "AMP UPnP" };
            thread.Start();
        }

        // Turned off: no router contact, only the LAN address for the Same Wi-Fi code; the UI says why
        public void TurnOff(string reason, PortMapper previous = null) {
            offReason = reason ?? "";
            Start(previous);
        }

        public void Stop() { stopRequested.Set(); }

        public bool WaitDone(int timeoutMs) { return thread == null || done.WaitOne(timeoutMs); }

        private bool Stopping { get { return stopRequested.WaitOne(0); } }

        // Renew halfway through the lease; a permanent mapping (lease 0) is never renewed
        public static int RenewAfterSeconds(int leaseSeconds) {
            return leaseSeconds > 0 ? Math.Max(1, leaseSeconds / 2) : -1;
        }

        private void Run() {
            List<Uri> locations = Ssdp.Discover(SearchTarget, DiscoveryTimeoutMs, log);
            if(Stopping) return;
            log("UPnP: " + locations.Count + " device(s) answered");

            igd = null;
            IPAddress external = null;
            foreach(Uri location in locations.Take(MaxDevices)) {
                if(Stopping) return;
                IgdDescription device;
                try {
                    device = IgdDescription.Parse(Http.Get(location, CallTimeoutMs), location);
                } catch(Exception e) {
                    log("UPnP: can't read " + location + ": " + e.Message);
                    continue;
                }
                foreach(IgdService service in device.Services) {
                    var client = new IgdClient(device, service) { TimeoutMs = CallTimeoutMs };
                    IPAddress address;
                    try {
                        IPAddress.TryParse(client.GetExternalIPAddress(), out address);
                    } catch(UpnpException e) {
                        log("UPnP: " + device.Name + " " + service.ServiceType + ": " + e.Message);
                        continue;
                    }
                    // Prefer a connection that is online (a router may list an unused PPP connection next to the real one)
                    if(igd == null || (NatRules.Classify(external) == AddressClass.None && NatRules.Classify(address) != AddressClass.None)) { igd = client; external = address; }
                }
                if(igd != null && NatRules.Classify(external) != AddressClass.None) break;
            }
            if(Stopping) return;
            if(igd == null) {
                status = status.With(s => { s.State = locations.Count == 0 ? MapState.NoRouter : MapState.Failed; s.Detail = locations.Count == 0 ? "" : "the router didn't answer"; });
                log("UPnP: no usable router (" + status.State + ")");
                return;
            }

            IPAddress local = LocalAddress.Towards(IPAddress.Parse(igd.Service.ControlUrl.Host)) ?? LocalAddress.Lan();
            AddressClass kind = NatRules.Classify(external);
            log("UPnP: router " + igd.Device.Name + " (" + igd.Service.ServiceType + "), internet address is " + kind + ", this PC is " + local);
            status = status.With(s => { s.Router = igd.Device.Name; s.ServiceType = igd.Service.ServiceType; s.ExternalIp = external; s.LocalIp = local; });
            if(kind == AddressClass.None) { status = status.With(s => s.State = MapState.NoInternet); return; }
            if(kind == AddressClass.Shared) { status = status.With(s => s.State = MapState.Shared); return; }

            bool byHand = false;
            foreach(string protocol in Protocols) {
                if(Stopping) return;
                string problem = Map(protocol, local.ToString(), ref byHand);
                if(problem != null) {
                    log("UPnP: " + protocol + " " + Port + " refused: " + problem);
                    status = status.With(s => { s.State = MapState.Refused; s.Detail = problem; });
                    return;
                }
            }
            int shortest = added.Count == 0 ? 0 : added.Values.Min();
            status = status.With(s => { s.State = MapState.Open; s.LeaseSeconds = shortest; s.ForwardedByHand = byHand && added.Count == 0; });
            log("UPnP: port " + Port + " open (" + (added.Count == 0 ? "not changed by AMP" : string.Join(", ", added.Select(a => a.Key + " lease " + a.Value + " s").ToArray())) + ")");

            // Hold the mappings until Stop (Cleanup removes them); every leased one is renewed halfway through the shortest lease
            int[] leased = added.Values.Where(l => l > 0).ToArray();
            int renew = leased.Length == 0 ? -1 : RenewAfterSeconds(leased.Min());
            while(added.Count > 0 && !stopRequested.WaitOne(renew > 0 ? renew * 1000 : Timeout.Infinite)) {
                foreach(KeyValuePair<string, int> mapping in added.Where(a => a.Value > 0).ToList()) {
                    try {
                        igd.AddPortMapping(Port, mapping.Key, local.ToString(), Description, mapping.Value);
                    } catch(UpnpException e) {
                        log("UPnP: renewing " + mapping.Key + " failed: " + e.Message);
                        status = status.With(s => { s.State = MapState.Failed; s.Detail = "the router stopped renewing the port (" + e.Message + ")"; });
                    }
                }
                Renewals++;
            }
        }

        // null when the port is open for this protocol, otherwise why not. Records the mapping in added only when it is AMP's own.
        private string Map(string protocol, string local, ref bool byHand) {
            PortMappingEntry existing = null;
            bool checkedFirst = true;
            try {
                existing = igd.GetSpecificPortMappingEntry(Port, protocol);
            } catch(UpnpException e) {
                checkedFirst = false;
                log("UPnP: can't read the router's entry for " + protocol + " " + Port + " (" + e.Message + "), adding anyway");
            }
            if(existing != null) {
                if(existing.InternalClient != local) return "port " + Port + " is already forwarded to another device (" + existing.InternalClient + ")";
                if(existing.Description != Description) { byHand = true; return null; } // the host's own port forward: use it, leave it alone
            }

            int lease = LeaseSeconds;
            try {
                igd.AddPortMapping(Port, protocol, local, Description, lease);
            } catch(UpnpException e) when(lease > 0 && (e.Code == UpnpException.OnlyPermanentLeases || e.Code == UpnpException.InvalidArgs)) {
                // 725 OnlyPermanentLeasesSupported (402 on some older routers): this protocol gets a permanent mapping, removed on stop
                log("UPnP: lease " + lease + " s refused for " + protocol + " (" + e.Message + "), asking for a permanent mapping");
                lease = 0;
                try { igd.AddPortMapping(Port, protocol, local, Description, 0); } catch(UpnpException again) { return Reason(again); }
            } catch(UpnpException e) {
                return Reason(e);
            }

            if(!checkedFirst) {
                // Not known before adding: only a mapping that now reads as AMP's own for this PC may be renewed and removed later
                PortMappingEntry now = null;
                bool readable = true;
                try { now = igd.GetSpecificPortMappingEntry(Port, protocol); } catch(UpnpException) { readable = false; }
                if(readable) {
                    if(now == null) return "the router didn't keep the mapping";
                    if(now.InternalClient != local) return "port " + Port + " is already forwarded to another device (" + now.InternalClient + ")";
                    if(now.Description != Description) { byHand = true; return null; }
                } else if(lease == 0) {
                    // Can't be told apart from a permanent forward made by hand: leave it on the router rather than risk removing that
                    log("UPnP: can't check " + protocol + " " + Port + " on this router; the permanent mapping is left in place");
                    return null;
                }
                // Unreadable but leased: it ends by itself anyway, so renewing and removing it changes nothing for a mapping made by hand
            }
            added[protocol] = lease;
            return null;
        }

        private string Reason(UpnpException e) {
            if(e.Code == UpnpException.Conflict) return "port " + Port + " is already forwarded to another device";
            if(e.Code == UpnpException.NotAuthorized) return "the router doesn't allow programs to open ports";
            return e.Message;
        }

        private void Cleanup() {
            foreach(string protocol in added.Keys) {
                try {
                    igd.DeletePortMapping(Port, protocol);
                    log("UPnP: removed " + protocol + " " + Port + " from the router");
                } catch(UpnpException e) {
                    log("UPnP: removing " + protocol + " " + Port + " failed: " + e.Message + (status.LeaseSeconds > 0 ? " (it expires by itself)" : ""));
                }
            }
            added.Clear();
        }
    }
}
