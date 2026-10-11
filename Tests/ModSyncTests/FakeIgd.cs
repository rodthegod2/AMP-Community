using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Xml;

// A fake UPnP router for tests (unit tests and AmpBot --fake-igd): an SSDP responder (unicast, 127.0.0.1) and a tiny HTTP server with the
// device description and a WANIPConnection control endpoint. Written independently of AMP's client so it can catch its mistakes.
public class FakeIgd : IDisposable {
    public class Mapping { public string Client = "", Description = ""; public int Lease; }

    public string ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1";
    public string ExternalIp = "203.0.113.7";
    public bool RejectLeases;        // 725 OnlyPermanentLeasesSupported for any lease > 0 (of RejectLeasesFor only, when set)
    public string RejectLeasesFor;   // "TCP" / "UDP" / null = both
    public readonly Dictionary<string, int> AddFault = new Dictionary<string, int>(); // protocol -> UPnP error for every AddPortMapping
    public int DeleteDelayMs;        // a slow router: each DeletePortMapping is answered (and done) this much later
    public bool NoGetSpecific;       // 401 Invalid Action for GetSpecificPortMappingEntry
    public bool Silent;              // doesn't answer SSDP at all
    public readonly Dictionary<string, Mapping> Mappings = new Dictionary<string, Mapping>(); // "TCP 26950"
    public readonly List<string> Calls = new List<string>();  // "AddPortMapping TCP 26950 127.0.0.1 7200 AMP Community"

    public int SsdpPort { get { return ((IPEndPoint) ssdp.Client.LocalEndPoint).Port; } }
    public IPEndPoint SsdpEndpoint { get { return new IPEndPoint(IPAddress.Loopback, SsdpPort); } }
    private int HttpPort { get { return ((IPEndPoint) http.LocalEndpoint).Port; } }

    private readonly UdpClient ssdp;
    private readonly TcpListener http;
    private volatile bool stopped;
    public readonly object Gate = new object();

    public FakeIgd(int ssdpPort = 0) {
        ssdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, ssdpPort));
        http = new TcpListener(IPAddress.Loopback, 0);
        http.Start();
        new Thread(SsdpLoop) { IsBackground = true }.Start();
        new Thread(HttpLoop) { IsBackground = true }.Start();
    }

    public void Dispose() {
        stopped = true;
        ssdp.Close();
        http.Stop();
    }

    public string[] CallList() { lock(Gate) return Calls.ToArray(); }
    public int MappingCount() { lock(Gate) return Mappings.Count; }

    private void SsdpLoop() {
        while(!stopped) {
            try {
                IPEndPoint from = null;
                string request = Encoding.ASCII.GetString(ssdp.Receive(ref from));
                if(Silent || !request.StartsWith("M-SEARCH")) continue;
                string reply = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
                               "USN: uuid:fake-igd::urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\nEXT:\r\nSERVER: FakeIgd/1.0 UPnP/1.0\r\n" +
                               "location: http://127.0.0.1:" + HttpPort + "/desc.xml\r\n\r\n";
                byte[] data = Encoding.ASCII.GetBytes(reply);
                ssdp.Send(data, data.Length, from);
            } catch(Exception) { if(stopped) return; }
        }
    }

    private void HttpLoop() {
        while(!stopped) {
            TcpClient c;
            try { c = http.AcceptTcpClient(); } catch(Exception) { return; }
            ThreadPool.QueueUserWorkItem(_ => { try { Serve(c); } catch(Exception) { } finally { c.Close(); } });
        }
    }

    private void Serve(TcpClient c) {
        NetworkStream s = c.GetStream();
        var head = new StringBuilder();
        while(!head.ToString().EndsWith("\r\n\r\n")) { int b = s.ReadByte(); if(b < 0) return; head.Append((char) b); }
        string[] lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
        var headers = lines.Skip(1).Where(l => l.Contains(":")).ToDictionary(l => l.Substring(0, l.IndexOf(':')).Trim().ToLowerInvariant(), l => l.Substring(l.IndexOf(':') + 1).Trim());
        string length;
        var body = new byte[headers.TryGetValue("content-length", out length) ? int.Parse(length) : 0];
        for(int read = 0; read < body.Length;) { int n = s.Read(body, read, body.Length - read); if(n <= 0) return; read += n; }

        string path = lines[0].Split(' ')[1];
        int status = 200;
        string reply;
        if(lines[0].StartsWith("GET") && path == "/desc.xml") {
            reply = Description();
        } else if(lines[0].StartsWith("POST") && path == "/ctl") {
            string action = headers["soapaction"].Trim('"');
            action = action.Substring(action.IndexOf('#') + 1);
            reply = Control(action, Encoding.UTF8.GetString(body), out status);
        } else {
            status = 404; reply = "";
        }
        byte[] data = Encoding.UTF8.GetBytes(reply);
        string response = "HTTP/1.1 " + status + (status == 200 ? " OK" : " Error") + "\r\nContent-Type: text/xml; charset=\"utf-8\"\r\nContent-Length: " + data.Length + "\r\nConnection: close\r\n\r\n";
        byte[] h = Encoding.ASCII.GetBytes(response);
        s.Write(h, 0, h.Length);
        s.Write(data, 0, data.Length);
    }

    private string Description() {
        // An IGD v1 tree like real routers have: root -> WANDevice -> WANConnectionDevice -> the connection service, plus a relative controlURL
        return "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><specVersion><major>1</major><minor>0</minor></specVersion>" +
               "<device><deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType><friendlyName>Fake Router</friendlyName>" +
               "<manufacturer>AMP Tests</manufacturer><modelName>FakeIgd</modelName><serviceList><service><serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>" +
               "<controlURL>/l3f</controlURL></service></serviceList><deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>" +
               "<deviceList><device><deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType><serviceList><service>" +
               "<serviceType>" + ServiceType + "</serviceType><serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId><controlURL>/ctl</controlURL>" +
               "<eventSubURL>/evt</eventSubURL><SCPDURL>/scpd.xml</SCPDURL></service></serviceList></device></deviceList></device></deviceList></device></root>";
    }

    private string Control(string action, string body, out int status) {
        var doc = new XmlDocument();
        doc.LoadXml(body);
        Func<string, string> arg = name => { XmlNode n = doc.SelectSingleNode("//*[local-name()='" + name + "']"); return n == null ? "" : n.InnerText; };
        string key = arg("NewProtocol") + " " + arg("NewExternalPort");
        status = 200;
        if(action == "DeletePortMapping" && DeleteDelayMs > 0) Thread.Sleep(DeleteDelayMs);
        lock(Gate) {
            switch(action) {
                case "GetExternalIPAddress":
                    Calls.Add(action);
                    return Ok(action, "<NewExternalIPAddress>" + ExternalIp + "</NewExternalIPAddress>");
                case "AddPortMapping": {
                    int lease = int.Parse(arg("NewLeaseDuration"));
                    Calls.Add(action + " " + key + " " + arg("NewInternalClient") + " " + lease + " " + arg("NewPortMappingDescription"));
                    int fault;
                    if(AddFault.TryGetValue(arg("NewProtocol"), out fault)) return Fault(fault, "Refused for this test", out status);
                    if(RejectLeases && lease > 0 && (RejectLeasesFor == null || RejectLeasesFor == arg("NewProtocol"))) return Fault(725, "OnlyPermanentLeasesSupported", out status);
                    Mapping existing;
                    if(Mappings.TryGetValue(key, out existing) && existing.Client != arg("NewInternalClient")) return Fault(718, "ConflictInMappingEntry", out status);
                    Mappings[key] = new Mapping { Client = arg("NewInternalClient"), Description = arg("NewPortMappingDescription"), Lease = lease };
                    return Ok(action, "");
                }
                case "GetSpecificPortMappingEntry": {
                    Calls.Add(action + " " + key);
                    if(NoGetSpecific) return Fault(401, "Invalid Action", out status);
                    Mapping m;
                    if(!Mappings.TryGetValue(key, out m)) return Fault(714, "NoSuchEntryInArray", out status);
                    return Ok(action, "<NewInternalPort>" + arg("NewExternalPort") + "</NewInternalPort><NewInternalClient>" + m.Client + "</NewInternalClient><NewEnabled>1</NewEnabled>" +
                                      "<NewPortMappingDescription>" + m.Description + "</NewPortMappingDescription><NewLeaseDuration>" + m.Lease + "</NewLeaseDuration>");
                }
                case "DeletePortMapping":
                    Calls.Add(action + " " + key);
                    if(!Mappings.Remove(key)) return Fault(714, "NoSuchEntryInArray", out status);
                    return Ok(action, "");
                default:
                    Calls.Add(action);
                    return Fault(401, "Invalid Action", out status);
            }
        }
    }

    private string Ok(string action, string inner) {
        return "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
               "<u:" + action + "Response xmlns:u=\"" + ServiceType + "\">" + inner + "</u:" + action + "Response></s:Body></s:Envelope>";
    }

    private static string Fault(int code, string description, out int status) {
        status = 500;
        return "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
               "<s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\">" +
               "<errorCode>" + code + "</errorCode><errorDescription>" + description + "</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
    }
}
