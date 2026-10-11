using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using AMP.Network.Nat;

// Join codes, UPnP parsing, CGNAT rules and the port mapping flow against FakeIgd (docs/superpowers/plans/2026-10-10-easy-join-layer1.md)
internal static partial class Program {
    private static void NatTests() {
        JoinCodeTests();
        JoinTargetTests();
        NatRuleTests();
        SsdpTests();
        DescriptionTests();
        SoapTests();
        PortMapperTests();
    }

    private static IPAddress Ip(string s) { return IPAddress.Parse(s); }

    private static bool Decodes(string code, string ip, int port) {
        IPAddress a; int p; string e;
        return JoinCodes.TryDecode(code, out a, out p, out e) && a.ToString() == ip && p == port;
    }

    private static string DecodeError(string code) {
        IPAddress a; int p; string e;
        return JoinCodes.TryDecode(code, out a, out p, out e) ? null : e;
    }

    private static void JoinCodeTests() {
        string[] ips = { "0.0.0.0", "1.2.3.4", "127.0.0.1", "192.168.1.50", "203.0.113.7", "255.255.255.255", "100.64.0.1" };
        int[] ports = { 1, 80, 26950, 26951, 32767, 65535 };
        bool roundTrips = true, shapes = true;
        foreach(string ip in ips) foreach(int port in ports) {
            string code = JoinCodes.Encode(Ip(ip), port);
            roundTrips &= Decodes(code, ip, port);
            shapes &= port == JoinCodes.DefaultPort ? code.Length == 9 && code[4] == '-' : code.Length == 14 && code[4] == '-' && code[9] == '-';
            shapes &= code.Replace("-", "").All(c => JoinCodes.Alphabet.IndexOf(c) >= 0);
        }
        T("join codes round-trip every address and port", roundTrips);
        T("join codes are XXXX-XXXX for the default port, XXXX-XXXX-XXXX otherwise, alphabet only", shapes);
        T("the alphabet has 32 symbols and no 0, O, 1 or I", JoinCodes.Alphabet.Length == 32 && JoinCodes.Alphabet.Distinct().Count() == 32 && JoinCodes.Alphabet.IndexOfAny("0O1I".ToCharArray()) < 0);

        // Pinned: the format must never change (old and new AMP builds must understand each other's codes). The E2E join-code scenario uses the first.
        T("pinned code 127.0.0.1:26951", JoinCodes.Encode(Ip("127.0.0.1"), 26951) == "HW22-22DB-AWJ6");
        T("pinned code 203.0.113.7:26950", JoinCodes.Encode(Ip("203.0.113.7"), 26950) == "TE29-43W4");
        T("pinned code 192.168.1.50:26950", JoinCodes.Encode(Ip("192.168.1.50"), 26950) == "S4N2-4EKS");
        T("pinned code 203.0.113.7:26951 (E2E host-upnp-fake)", JoinCodes.Encode(Ip("203.0.113.7"), 26951) == "TE29-43VB-AX3L");
        T("pinned codes decode", Decodes("HW22-22DB-AWJ6", "127.0.0.1", 26951) && Decodes("TE29-43W4", "203.0.113.7", 26950));

        T("codes are case-insensitive and ignore dashes and spaces", Decodes("te2943w4", "203.0.113.7", 26950) && Decodes(" te29 - 43w4 ", "203.0.113.7", 26950) && Decodes("TE 29 43 W4", "203.0.113.7", 26950));

        // Every single wrong symbol is caught (CRC-8/CRC-12 catch every error burst up to their width; a symbol is 5 bits)
        bool allCaught = true;
        foreach(string sample in new[] { "TE29-43W4", "S4N2-4EKS", "HW22-22DB-AWJ6", JoinCodes.Encode(Ip("8.8.4.4"), 1234), JoinCodes.Encode(Ip("10.0.0.2"), 26950) }) {
            string clean = sample.Replace("-", "");
            for(int i = 0; i < clean.Length; i++) foreach(char c in JoinCodes.Alphabet) {
                if(c == clean[i]) continue;
                string typo = clean.Substring(0, i) + c + clean.Substring(i + 1);
                if(DecodeError(typo) == null) { allCaught = false; Console.WriteLine("  not caught: " + typo); }
            }
        }
        T("every single-symbol typo is caught", allCaught);

        // Swapped neighbours: always caught in long codes (a burst of <= 10 bits vs CRC-12); short codes nearly always (CRC-8)
        int swaps = 0, caught = 0;
        bool longAll = true;
        var random = new Random(7);
        for(int k = 0; k < 400; k++) {
            var ip = new IPAddress(new[] { (byte) random.Next(256), (byte) random.Next(256), (byte) random.Next(256), (byte) random.Next(256) });
            int port = k % 2 == 0 ? JoinCodes.DefaultPort : random.Next(1, 65536);
            string clean = JoinCodes.Encode(ip, port).Replace("-", "");
            for(int i = 0; i + 1 < clean.Length; i++) {
                if(clean[i] == clean[i + 1]) continue;
                string swapped = clean.Substring(0, i) + clean[i + 1] + clean[i] + clean.Substring(i + 2);
                bool hit = DecodeError(swapped) != null;
                swaps++; if(hit) caught++;
                if(!hit && clean.Length == JoinCodes.LongLength) longAll = false;
            }
        }
        T("swapped neighbours are always caught in long codes", longAll);
        T("swapped neighbours are caught in >= 99% of all codes (" + caught + "/" + swaps + ")", caught >= swaps * 0.99);

        string lookalike = DecodeError("TE2O-43W4");
        T("0/O/1/I get their own message", lookalike != null && lookalike.Contains("never contain 0, O, 1 or I") && DecodeError("1E29-43W4").Contains("0, O, 1 or I"));
        T("a wrong length is explained", DecodeError("TE29-43W").Contains("8 or 12") && DecodeError("TE29-43W4-AB").Contains("8 or 12"));
        T("other characters are not a code", DecodeError("hello!") != null && DecodeError("") != null && DecodeError(null) != null);
        T("a typo says so", DecodeError("TE29-43W5").Contains("typo"));
        bool threw = false;
        try { JoinCodes.Encode(IPAddress.IPv6Loopback, 26950); } catch(ArgumentException) { threw = true; }
        T("IPv6 can't be encoded", threw);
    }

    private static bool Target(string text, string portText, string host, int port) {
        string h; int p; string e;
        return JoinTarget.TryParse(text, portText, out h, out p, out e) && h == host && p == port;
    }

    private static string TargetError(string text, string portText) {
        string h; int p; string e;
        return JoinTarget.TryParse(text, portText, out h, out p, out e) ? null : e;
    }

    private static void JoinTargetTests() {
        T("an IP uses the port field", Target("192.168.1.5", "26950", "192.168.1.5", 26950) && Target(" 10.0.0.2 ", " 27000 ", "10.0.0.2", 27000));
        T("IP:port overrides the port field", Target("192.168.1.5:27001", "garbage", "192.168.1.5", 27001));
        T("host names and localhost work", Target("my.host.example:1234", "", "my.host.example", 1234) && Target("localhost", "26950", "localhost", 26950) && Target("LOCALHOST:5", "", "LOCALHOST", 5));
        T("a code brings its own port", Target("HW22-22DB-AWJ6", "1", "127.0.0.1", 26951) && Target("te29-43w4", "", "203.0.113.7", 26950));
        T("ports above 32767 are refused", TargetError("1.2.3.4:40000", "") != null && TargetError("1.2.3.4", "0") != null && TargetError("1.2.3.4", "") != null);
        T("a code for a port above 32767 is refused", TargetError(JoinCodes.Encode(Ip("1.2.3.4"), 40000), "") != null);
        T("empty input asks for a code or IP", TargetError("  ", "26950").Contains("join code or an IP"));
        T("a bad code explains the code rules", TargetError("TE2O-43W4", "26950").Contains("0, O, 1 or I"));
        T("broken addresses are refused", TargetError(":26950", "") != null && TargetError("1.2.3.4:", "") != null && TargetError("1.2 3.4", "26950") != null && TargetError("a_b!", "26950") != null);
        T("bare host names (PC names, Tailscale names) work", Target("gamingpc", "26950", "gamingpc", 26950) && Target("desktop-ab12", "26950", "desktop-ab12", 26950)
            && Target("gamerpc7", "26950", "gamerpc7", 26950) && Target("DESKTOP1:27000", "", "DESKTOP1", 27000) && Target("mybox", "26950", "mybox", 26950));
        T("a trailing dot is ignored (codes and host names)", Target("TE29-43W4.", "", "203.0.113.7", 26950) && Target("host.example.:5", "", "host.example", 5));
        T("IPv6 gets a clear message", TargetError("fe80::1", "26950").Contains("IPv6") && TargetError("[::1]:26950", "").Contains("IPv6"));
        T("code-shaped input keeps the code errors", TargetError("TE29-43W5", "26950").Contains("typo") && TargetError("TE29 43W5", "26950").Contains("typo")
            && TargetError("TE2I-43W4", "26950").Contains("0, O, 1 or I"));
    }

    private static void NatRuleTests() {
        Func<string, AddressClass> c = s => NatRules.Classify(Ip(s));
        T("10/8 is shared", c("10.0.0.0") == AddressClass.Shared && c("10.255.255.255") == AddressClass.Shared && c("9.255.255.255") == AddressClass.Public && c("11.0.0.0") == AddressClass.Public);
        T("172.16/12 is shared", c("172.16.0.0") == AddressClass.Shared && c("172.31.255.255") == AddressClass.Shared && c("172.15.255.255") == AddressClass.Public && c("172.32.0.0") == AddressClass.Public);
        T("192.168/16 is shared", c("192.168.0.0") == AddressClass.Shared && c("192.168.255.255") == AddressClass.Shared && c("192.167.255.255") == AddressClass.Public && c("192.169.0.0") == AddressClass.Public);
        T("100.64/10 (CGNAT) is shared", c("100.64.0.0") == AddressClass.Shared && c("100.127.255.255") == AddressClass.Shared && c("100.63.255.255") == AddressClass.Public && c("100.128.0.0") == AddressClass.Public);
        T("public and missing addresses", c("8.8.8.8") == AddressClass.Public && c("0.0.0.0") == AddressClass.None && NatRules.Classify(null) == AddressClass.None && NatRules.Classify(IPAddress.IPv6Loopback) == AddressClass.None);
        T("unusable addresses are not public (0/8, 127/8, 169.254/16, 198.18/15, 224+)", c("0.1.2.3") == AddressClass.None && c("127.0.0.1") == AddressClass.None && c("169.254.9.9") == AddressClass.None
            && c("198.18.0.1") == AddressClass.None && c("198.19.255.255") == AddressClass.None && c("198.20.0.0") == AddressClass.Public && c("224.0.0.1") == AddressClass.None
            && c("255.255.255.255") == AddressClass.None && c("223.255.255.255") == AddressClass.Public && c("169.253.0.1") == AddressClass.Public && c("128.0.0.1") == AddressClass.Public);
    }

    private static void SsdpTests() {
        string search = Ssdp.BuildSearch("urn:schemas-upnp-org:device:InternetGatewayDevice:1", 2);
        T("M-SEARCH has the required lines", search.StartsWith("M-SEARCH * HTTP/1.1\r\n") && search.Contains("HOST: 239.255.255.250:1900\r\n") && search.Contains("MAN: \"ssdp:discover\"\r\n")
                                             && search.Contains("MX: 2\r\n") && search.Contains("ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n") && search.EndsWith("\r\n\r\n"));
        string response = "HTTP/1.1 200 OK\r\nCache-Control: max-age=1800\r\nlocation: http://192.168.1.1:5000/rootDesc.xml\r\nSt: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
        Dictionary<string, string> h = Ssdp.ParseResponse(response);
        T("SSDP headers are case-insensitive", h != null && h["LOCATION"] == "http://192.168.1.1:5000/rootDesc.xml" && h["ST"].EndsWith("Device:1"));
        T("SSDP non-200 and NOTIFY are ignored", Ssdp.ParseResponse("HTTP/1.1 404 Not Found\r\n\r\n") == null && Ssdp.ParseResponse("NOTIFY * HTTP/1.1\r\nLOCATION: http://x/\r\n\r\n") == null);
        T("location is taken from the device that answered", Ssdp.Location(response, Ip("192.168.1.1")).ToString() == "http://192.168.1.1:5000/rootDesc.xml");
        T("a location on another host is refused", Ssdp.Location(response, Ip("192.168.1.66")) == null);
        T("https and relative locations are refused", Ssdp.Location(response.Replace("http://", "https://"), Ip("192.168.1.1")) == null && Ssdp.Location("HTTP/1.1 200 OK\r\nLOCATION: /desc.xml\r\n\r\n", Ip("192.168.1.1")) == null);
        T("LF-only responses parse", Ssdp.ParseResponse("HTTP/1.1 200 OK\nLOCATION: http://10.0.0.1/d.xml\n\n")["LOCATION"] == "http://10.0.0.1/d.xml");
    }

    private const string V2Description = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><URLBase>http://192.168.1.1:49000/</URLBase><device>" +
        "<deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:2</deviceType><friendlyName>Box</friendlyName><manufacturer>ACME</manufacturer><modelName>Router 9</modelName>" +
        "<deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:2</deviceType><deviceList><device><serviceList>" +
        "<service><serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType><controlURL>ctl/ppp</controlURL></service>" +
        "<service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:2</serviceType><controlURL>/ctl/ip</controlURL></service>" +
        "<service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>http://8.8.8.8/evil</controlURL></service>" +
        "</serviceList></device></deviceList></device></deviceList></device></root>";

    private static void DescriptionTests() {
        IgdDescription d = IgdDescription.Parse(V2Description, new Uri("http://192.168.1.1:49000/igd.xml"));
        T("description: router name", d.Name == "ACME Router 9" && d.FriendlyName == "Box");
        T("description: WANIPConnection:2 first, then PPP; URLBase resolves relative URLs", d.Services.Count == 2 && d.Services[0].ServiceType.EndsWith("WANIPConnection:2")
            && d.Services[0].ControlUrl.ToString() == "http://192.168.1.1:49000/ctl/ip" && d.Services[1].ControlUrl.ToString() == "http://192.168.1.1:49000/ctl/ppp");
        T("description: a control URL on another host is dropped", d.Services.All(s => s.ControlUrl.Host == "192.168.1.1"));
        IgdDescription noBase = IgdDescription.Parse(V2Description.Replace("<URLBase>http://192.168.1.1:49000/</URLBase>", ""), new Uri("http://192.168.1.1:5000/a/igd.xml"));
        T("description: without URLBase, URLs are relative to the description", noBase.Services[0].ControlUrl.ToString() == "http://192.168.1.1:5000/ctl/ip" && noBase.Services[1].ControlUrl.ToString() == "http://192.168.1.1:5000/a/ctl/ppp");
        bool refused = false;
        try { IgdDescription.Parse("<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><root>&x;</root>", new Uri("http://192.168.1.1/")); }
        catch(System.Xml.XmlException) { refused = true; }
        T("description: DTDs are refused (no external entities)", refused);
        T("description: no WAN service, no services", IgdDescription.Parse("<root><device><friendlyName>Printer</friendlyName></device></root>", new Uri("http://192.168.1.9/")).Services.Count == 0);
    }

    private static void SoapTests() {
        string request = Soap.BuildRequest("urn:schemas-upnp-org:service:WANIPConnection:1", "AddPortMapping",
            new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("NewPortMappingDescription", "A<&>B"), new KeyValuePair<string, string>("NewRemoteHost", "") });
        T("SOAP request: envelope, action element and escaped values", request.Contains("<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">")
            && request.Contains("<NewPortMappingDescription>A&lt;&amp;&gt;B</NewPortMappingDescription>") && request.Contains("<NewRemoteHost></NewRemoteHost>") && request.Contains("</s:Envelope>"));
        string ok = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:GetExternalIPAddressResponse xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
                    "<NewExternalIPAddress> 203.0.113.7 </NewExternalIPAddress></u:GetExternalIPAddressResponse></s:Body></s:Envelope>";
        Dictionary<string, string> r = Soap.ParseResponse(ok, "GetExternalIPAddress");
        T("SOAP response: out arguments", r != null && r["NewExternalIPAddress"] == "203.0.113.7");
        T("SOAP response: another action's answer is not this one", Soap.ParseResponse(ok, "AddPortMapping") == null);
        string fault = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>" +
                       "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>718</errorCode><errorDescription>ConflictInMappingEntry</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
        int code; string description;
        T("SOAP fault: code and description", Soap.TryParseFault(fault, out code, out description) && code == 718 && description == "ConflictInMappingEntry");
        T("SOAP fault: not XML is not a fault", !Soap.TryParseFault("<html>500</html", out code, out description) && !Soap.TryParseFault("<html/>", out code, out description));
    }

    // Waits until the mapper left Searching
    private static PortMapStatus Settle(PortMapper m) {
        for(int i = 0; i < 200 && m.Status.State == MapState.Searching; i++) Thread.Sleep(50);
        return m.Status;
    }

    private static PortMapper Mapper(FakeIgd igd, int port, List<string> log = null) {
        return new PortMapper(port, s => { lock(log ?? new List<string>()) log?.Add(s); }) { SearchTarget = igd.SsdpEndpoint, DiscoveryTimeoutMs = 1500 };
    }

    private static void PortMapperTests() {
        T("renew halfway through the lease, never for permanent mappings", PortMapper.RenewAfterSeconds(7200) == 3600 && PortMapper.RenewAfterSeconds(600) == 300
                                                                           && PortMapper.RenewAfterSeconds(1) == 1 && PortMapper.RenewAfterSeconds(0) < 0);

        const int port = 26950;
        using(var igd = new FakeIgd()) {
            var log = new List<string>();
            PortMapper m = Mapper(igd, port, log);
            m.Start();
            PortMapStatus s = Settle(m);
            string[] calls = igd.CallList();
            T("fake IGD: the port is open", s.State == MapState.Open && !s.ForwardedByHand && s.LeaseSeconds == 7200);
            T("fake IGD: router, service, addresses", s.Router == "AMP Tests FakeIgd" && s.ServiceType.EndsWith("WANIPConnection:1") && s.ExternalIp.ToString() == "203.0.113.7" && s.LocalIp.ToString() == "127.0.0.1");
            T("fake IGD: TCP and UDP mapped to this PC, lease 7200, described", calls.Contains("AddPortMapping TCP 26950 127.0.0.1 7200 AMP Community") && calls.Contains("AddPortMapping UDP 26950 127.0.0.1 7200 AMP Community"));
            T("fake IGD: the internet code is the router's address", s.InternetCode == JoinCodes.Encode(Ip("203.0.113.7"), port));
            T("fake IGD: the host is told the port was opened and about the firewall", s.Describe().Any(l => l.Contains("opened on your router automatically")) && s.Describe().Any(l => l.Contains("firewall")));
            m.Stop();
            T("fake IGD: stopping finishes", m.WaitDone(10000));
            T("fake IGD: both mappings removed on stop", igd.MappingCount() == 0 && igd.CallList().Contains("DeletePortMapping TCP 26950") && igd.CallList().Contains("DeletePortMapping UDP 26950"));
        }

        using(var igd = new FakeIgd { RejectLeases = true }) {
            PortMapper m = Mapper(igd, port);
            m.Start();
            PortMapStatus s = Settle(m);
            T("lease refused (725): a permanent mapping instead", s.State == MapState.Open && s.LeaseSeconds == 0 && igd.CallList().Contains("AddPortMapping UDP 26950 127.0.0.1 0 AMP Community"));
            Thread.Sleep(1500);
            T("permanent mappings are not renewed", igd.CallList().Count(c => c.StartsWith("AddPortMapping")) == 4);
            m.Stop(); m.WaitDone(10000);
            T("permanent mappings are removed on stop", igd.MappingCount() == 0);
        }

        using(var igd = new FakeIgd()) {
            PortMapper m = Mapper(igd, port);
            m.LeaseSeconds = 2;
            m.Start();
            Settle(m);
            Thread.Sleep(3600);
            int adds = igd.CallList().Count(c => c.StartsWith("AddPortMapping") && c.Contains(" 2 AMP Community"));
            T("short lease: renewed every lease/2 (" + m.Renewals + " renewals, " + adds + " adds)", m.Renewals >= 2 && adds >= 2 + 2 * m.Renewals - 2);
            m.Stop(); m.WaitDone(10000);
            T("short lease: removed on stop", igd.MappingCount() == 0);
        }

        foreach(var shared in new[] { "100.64.3.4", "192.168.0.2", "10.1.1.1" }) {
            using(var igd = new FakeIgd { ExternalIp = shared }) {
                PortMapper m = Mapper(igd, port);
                m.Start();
                PortMapStatus s = Settle(m);
                m.Stop(); m.WaitDone(10000);
                T("shared internet address " + shared + ": no mapping, relay message, no internet code", s.State == MapState.Shared && !igd.CallList().Any(c => c.StartsWith("AddPortMapping"))
                    && s.InternetCode == null && s.Describe()[0].Contains("shares your IP") && s.Describe()[0].Contains("second router") && s.Describe()[0].Contains("same Wi-Fi still works"));
            }
        }

        using(var igd = new FakeIgd { ExternalIp = "0.0.0.0" }) {
            PortMapper m = Mapper(igd, port);
            m.Start();
            PortMapStatus s = Settle(m);
            m.Stop(); m.WaitDone(10000);
            T("router without internet address: NoInternet, nothing mapped", s.State == MapState.NoInternet && igd.MappingCount() == 0 && s.InternetCode == null);
        }

        using(var igd = new FakeIgd()) {
            igd.Mappings["TCP 26950"] = new FakeIgd.Mapping { Client = "192.168.1.99", Description = "Someone's game", Lease = 0 };
            PortMapper m = Mapper(igd, port);
            m.Start();
            PortMapStatus s = Settle(m);
            m.Stop(); m.WaitDone(10000);
            T("port forwarded to another device: refused, explained, no internet code", s.State == MapState.Refused && s.Detail.Contains("another device (192.168.1.99)")
                && s.Describe().Any(l => l.Contains("Forward port 26950 (TCP+UDP) to this PC by hand")) && s.InternetCode == null);
            T("port forwarded to another device: that mapping is untouched", igd.MappingCount() == 1 && igd.Mappings["TCP 26950"].Client == "192.168.1.99"
                && !igd.CallList().Any(c => c.StartsWith("AddPortMapping") || c.StartsWith("DeletePortMapping")));
        }

        using(var igd = new FakeIgd()) {
            igd.Mappings["TCP 26950"] = new FakeIgd.Mapping { Client = "127.0.0.1", Description = "my forward", Lease = 0 };
            igd.Mappings["UDP 26950"] = new FakeIgd.Mapping { Client = "127.0.0.1", Description = "my forward", Lease = 0 };
            PortMapper m = Mapper(igd, port);
            m.Start();
            PortMapStatus s = Settle(m);
            m.Stop(); m.WaitDone(10000);
            T("port forwarded to this PC by hand: used as it is", s.State == MapState.Open && s.ForwardedByHand && s.Describe()[0].Contains("already forwarded to this PC"));
            T("port forwarded to this PC by hand: never changed or removed", igd.MappingCount() == 2 && !igd.CallList().Any(c => c.StartsWith("AddPortMapping") || c.StartsWith("DeletePortMapping")));
        }

        using(var igd = new FakeIgd()) {
            igd.Mappings["TCP 26950"] = new FakeIgd.Mapping { Client = "127.0.0.1", Description = PortMapper.Description, Lease = 7200 };
            PortMapper m = Mapper(igd, port);
            m.Start();
            PortMapStatus s = Settle(m);
            m.Stop(); m.WaitDone(10000);
            T("a left-over AMP mapping is taken over and removed on stop", s.State == MapState.Open && !s.ForwardedByHand && igd.MappingCount() == 0);
        }

        using(var igd = new FakeIgd { NoGetSpecific = true, ServiceType = "urn:schemas-upnp-org:service:WANPPPConnection:1" }) {
            PortMapper m = Mapper(igd, 27000);
            m.Start();
            PortMapStatus s = Settle(m);
            T("PPP router without GetSpecificPortMappingEntry: still mapped", s.State == MapState.Open && s.ServiceType.EndsWith("WANPPPConnection:1") && igd.MappingCount() == 2
                && s.InternetCode == JoinCodes.Encode(Ip("203.0.113.7"), 27000));
            m.Stop(); m.WaitDone(10000);
            T("PPP router: removed on stop", igd.MappingCount() == 0);
        }

        using(var igd = new FakeIgd { Silent = true }) {
            PortMapper m = Mapper(igd, port);
            m.Start();
            PortMapStatus s = Settle(m);
            m.Stop(); m.WaitDone(10000);
            T("no router: NoRouter, explained", s.State == MapState.NoRouter && s.Describe()[0].Contains("No router answered") && s.Describe()[1].Contains("forward port 26950 (TCP+UDP)") && s.InternetCode == null);
        }

        using(var igd = new FakeIgd()) {
            PortMapper m = Mapper(igd, port);
            m.Start();
            m.Stop();
            T("stopped while searching: ends without mapping", m.WaitDone(10000) && igd.MappingCount() == 0 && !igd.CallList().Any(c => c.StartsWith("AddPortMapping")));
        }

        using(var igd = new FakeIgd()) {
            PortMapper first = Mapper(igd, port);
            first.Start();
            Settle(first);
            PortMapper second = Mapper(igd, port);
            first.Stop();
            second.Start(first); // hosting again: the old session's deletes come first
            PortMapStatus s = Settle(second);
            T("hosting again: the new session maps after the old one removed its mappings", s.State == MapState.Open && igd.MappingCount() == 2
                && Array.LastIndexOf(igd.CallList(), "DeletePortMapping UDP 26950") < Array.LastIndexOf(igd.CallList(), "AddPortMapping UDP 26950 127.0.0.1 7200 AMP Community"));
            second.Stop(); second.WaitDone(10000);
            T("hosting again: removed on the last stop", igd.MappingCount() == 0);
        }

        // I-1: leases are per protocol; only 725/402 mean "permanent only"
        using(var igd = new FakeIgd { RejectLeases = true, RejectLeasesFor = "UDP" }) {
            PortMapper m = Mapper(igd, port);
            m.LeaseSeconds = 2;
            m.Start();
            PortMapStatus s = Settle(m);
            Thread.Sleep(3600);
            string[] calls = igd.CallList();
            T("mixed leases: TCP keeps its lease, only UDP is permanent", s.State == MapState.Open && igd.Mappings["TCP 26950"].Lease == 2 && igd.Mappings["UDP 26950"].Lease == 0);
            T("mixed leases: the leased TCP mapping is still renewed (" + m.Renewals + ")", m.Renewals >= 2 && calls.Count(c => c == "AddPortMapping TCP 26950 127.0.0.1 2 AMP Community") >= 3
                && calls.Count(c => c.StartsWith("AddPortMapping UDP")) == 2);
            m.Stop(); m.WaitDone(10000);
            T("mixed leases: both removed on stop", igd.MappingCount() == 0);
        }

        using(var igd = new FakeIgd()) {
            igd.AddFault["UDP"] = 501;
            PortMapper m = Mapper(igd, port);
            m.Start();
            PortMapStatus s = Settle(m);
            T("another error is not a lease problem: no permanent retry, refused", s.State == MapState.Refused && !igd.CallList().Any(c => c.Contains(" 0 AMP Community")));
            m.Stop(); m.WaitDone(10000);
            T("refused half way: the TCP mapping already made is removed", igd.MappingCount() == 0 && igd.CallList().Contains("DeletePortMapping TCP 26950"));
        }

        // SHOULD 3: a router that can't be read: a leased mapping is managed, a permanent one is never removed (it could be a forward by hand)
        using(var igd = new FakeIgd { NoGetSpecific = true, RejectLeases = true }) {
            PortMapper m = Mapper(igd, port);
            m.Start();
            PortMapStatus s = Settle(m);
            m.Stop(); m.WaitDone(10000);
            T("unreadable router + permanent mapping: open, never deleted", s.State == MapState.Open && igd.MappingCount() == 2 && !igd.CallList().Any(c => c.StartsWith("DeletePortMapping")));
        }

        // I-2: a slow router: the old session's deletes take longer than any fixed wait; the new session must not start before them
        using(var igd = new FakeIgd { DeleteDelayMs = 6000 }) {
            PortMapper first = Mapper(igd, port);
            first.CallTimeoutMs = 10000;
            first.Start();
            Settle(first);
            PortMapper second = Mapper(igd, port);
            first.Stop();
            second.Start(first);
            for(int i = 0; i < 400 && !(second.Status.State == MapState.Open && igd.MappingCount() == 2); i++) Thread.Sleep(50);
            Thread.Sleep(500);
            string[] calls = igd.CallList();
            T("slow router, hosting again: the new mappings survive the old session's slow deletes", second.Status.State == MapState.Open && igd.MappingCount() == 2
                && Array.LastIndexOf(calls, "DeletePortMapping UDP 26950") < Array.IndexOf(calls, "AddPortMapping TCP 26950 127.0.0.1 7200 AMP Community", Array.LastIndexOf(calls, "DeletePortMapping TCP 26950")));
            igd.DeleteDelayMs = 0;
            second.Stop(); second.WaitDone(10000);
            T("slow router: removed on the last stop", igd.MappingCount() == 0);
        }

        // Turned off: no router traffic at all, but the Same Wi-Fi address is known
        using(var igd = new FakeIgd()) {
            PortMapper m = Mapper(igd, port);
            m.TurnOff("test run");
            PortMapStatus s = Settle(m);
            m.Stop();
            T("turned off: Off, LAN address known, nothing sent to the router", m.WaitDone(10000) && s.State == MapState.Off && s.LocalIp != null && igd.CallList().Length == 0 && s.InternetCode == null
                && s.Describe()[0].Contains("off (test run)"));
        }
    }

    // ModSyncTests.exe --real-router <port> <lease>: the owner-approved check against the real router. Only this port, only this PC,
    // a short lease (expires by itself if anything goes wrong); refuses to touch a port that already has a mapping. Never prints the public IP.
    private static int RealRouter(string[] args) {
        int port = args.Length > 1 ? int.Parse(args[1]) : JoinCodes.DefaultPort;
        int lease = args.Length > 2 ? int.Parse(args[2]) : 600;
        Action<string> say = s => Console.WriteLine("ROUTER " + s);
        say("port " + port + ", lease " + lease + " s");

        List<Uri> locations = Ssdp.Discover(Ssdp.Multicast, 3000, say);
        say(locations.Count + " UPnP device(s) answered");
        IgdClient igd = null;
        foreach(Uri location in locations) {
            try {
                IgdDescription d = IgdDescription.Parse(Http_Get(location), location);
                say("device: " + d.Name + " (" + d.FriendlyName + "), WAN services: " + string.Join(", ", d.Services.Select(s => s.ServiceType.Replace("urn:schemas-upnp-org:service:", ""))));
                if(igd == null && d.Services.Count > 0) igd = new IgdClient(d, d.Services[0]);
            } catch(Exception e) { say("can't read a device description: " + e.Message); }
        }
        if(igd == null) { say("RESULT no UPnP router"); return 1; }

        IPAddress external;
        IPAddress.TryParse(igd.GetExternalIPAddress(), out external);
        say("external address class: " + NatRules.Classify(external));
        foreach(string protocol in PortMapper.Protocols) {
            PortMappingEntry existing = igd.GetSpecificPortMappingEntry(port, protocol);
            if(existing != null) { say("RESULT aborted: " + protocol + " " + port + " already has a mapping (" + existing.Description + "), not touching it"); return 1; }
        }
        say("no existing mapping on port " + port);

        var mapper = new PortMapper(port, s => say("  " + s)) { LeaseSeconds = lease };
        bool ok = true;
        try {
            mapper.Start();
            PortMapStatus s = Settle(mapper);
            say("mapper state: " + s.State + (s.Detail.Length > 0 ? " (" + s.Detail + ")" : "") + ", lease " + s.LeaseSeconds + ", service " + s.ServiceType);
            if(s.State == MapState.Open) {
                foreach(string protocol in PortMapper.Protocols) {
                    PortMappingEntry e = igd.GetSpecificPortMappingEntry(port, protocol);
                    bool good = e != null && e.InternalClient == s.LocalIp.ToString() && e.InternalPort == port && e.Description == PortMapper.Description && e.LeaseSeconds <= lease;
                    say("verify " + protocol + ": " + (e == null ? "MISSING" : "client is this PC=" + (e.InternalClient == s.LocalIp.ToString()) + ", port " + e.InternalPort + ", '" + e.Description + "', lease left " + e.LeaseSeconds + ", enabled " + e.Enabled) + " -> " + (good ? "OK" : "BAD"));
                    ok &= good;
                }
            } else ok = s.State == MapState.Shared; // nothing to map behind CGNAT
        } finally {
            mapper.Stop();
            say("stopped: " + (mapper.WaitDone(15000) ? "done" : "TIMEOUT"));
            foreach(string protocol in PortMapper.Protocols) {
                PortMappingEntry e = igd.GetSpecificPortMappingEntry(port, protocol);
                say("after delete " + protocol + ": " + (e == null ? "gone" : "STILL THERE"));
                if(e != null && e.Description == PortMapper.Description) { igd.DeletePortMapping(port, protocol); ok = false; say("removed it directly"); }
                ok &= e == null;
            }
        }
        say("RESULT " + (ok ? "OK" : "FAILED"));
        return ok ? 0 : 1;
    }

    private static string Http_Get(Uri url) {
        using(var web = new WebClient { Proxy = null }) return web.DownloadString(url);
    }
}
