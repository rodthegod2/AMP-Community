using AMP.Data;
using AMP.Logging;
using System;
using System.Net;

namespace AMP.Network.Nat {
    // Opens the hosting port on the router while hosting by IP (Steam hosting doesn't use it). Called on the main thread;
    // the router work runs on PortMapper's own thread and never blocks or fails hosting.
    // Test runs (AppDomain data AMP_TEST_ISOLATED) never talk to the real router: UPnP is off unless "-amp-test-ssdp <ip:port>" points
    // it at a fake router.
    internal static class HostPortMapping {
        internal const string TestSsdpFlag = "-amp-test-ssdp";

        private static PortMapper current; // kept after Stop so quitting can wait for its deletes
        private static bool hosting;
        private static bool cleanupWaited; // quitting runs Exit twice (OnApplicationQuit, OnDestroy): block at most once

        // null when not hosting by IP
        internal static PortMapStatus Status { get { return hosting ? current?.Status : null; } }

        internal static string InternetCode { get { return Status?.InternetCode; } }

        internal static string LanCode {
            get {
                PortMapStatus s = Status;
                return s?.LocalIp == null ? null : JoinCodes.Encode(s.LocalIp, s.Port);
            }
        }

        internal static void Start(int port) {
            PortMapper previous = current;
            previous?.Stop();
            current = new PortMapper(port, message => Log.Info(Defines.SERVER, message));
            hosting = true;
            cleanupWaited = false;

            bool testRun = AppDomain.CurrentDomain.GetData("AMP_TEST_ISOLATED") != null;
            string testTarget = testRun ? ModSync.AutopilotScript.FindArg(Environment.GetCommandLineArgs(), TestSsdpFlag) : null;
            if(testTarget != null) {
                IPEndPoint target = ParseEndpoint(testTarget);
                if(target == null) { current.TurnOff("bad " + TestSsdpFlag + " value", previous); return; }
                current.SearchTarget = target;
                Log.Info(Defines.SERVER, "UPnP: test run, using the fake router at " + target);
            } else if(testRun) {
                current.TurnOff("test run", previous);
                return;
            } else if(!ModManager.safeFile.hostingSettings.autoPortForward) {
                current.TurnOff("autoPortForward is false in config.json", previous);
                return;
            }
            current.Start(previous);
        }

        internal static void Stop() {
            hosting = false;
            current?.Stop();
        }

        // Quitting: give the router a moment to remove the mappings (a lease ends them anyway)
        internal static void WaitForCleanup(int timeoutMs) {
            if(cleanupWaited) return;
            cleanupWaited = true;
            current?.WaitDone(timeoutMs);
        }

        private static IPEndPoint ParseEndpoint(string text) {
            int colon = text.LastIndexOf(':');
            IPAddress address;
            int port;
            if(colon <= 0 || !IPAddress.TryParse(text.Substring(0, colon), out address) || !int.TryParse(text.Substring(colon + 1), out port) || port < 1 || port > 65535) return null;
            return new IPEndPoint(address, port);
        }
    }
}
