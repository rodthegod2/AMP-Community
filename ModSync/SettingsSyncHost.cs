using AMP.Data;
using AMP.Logging;
using AMP.Network.Data;
using AMP.Network.Packets.Implementation;
using AMP.Threading;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using ThunderRoad;

namespace AMP.ModSync {
    // Host side of settings sync: sends the host's server-side Mod Options to greeted joiners and broadcasts changes.
    internal static class SettingsSyncHost {
        private static readonly List<KeyValuePair<ModOption, Action<object>>> subscriptions = new List<KeyValuePair<ModOption, Action<object>>>();

        private static readonly SentSettings sent = new SentSettings(); // what joiners were last told, per option

        private static bool Enabled { get { return ModManager.safeFile.hostingSettings.syncModSettings; } }

        // Main thread, when hosting starts
        internal static void Start() {
            Stop();
            if(!Enabled) return;
            foreach(ModOption option in ServerSideOptions()) {
                sent.Remember(ModOptionBridge.Read(option));
                ModOption captured = option;
                Action<object> handler = _ => OnChanged(captured);
                option.ValueChanged += handler;
                subscriptions.Add(new KeyValuePair<ModOption, Action<object>>(option, handler));
            }
            Log.Info(Defines.SERVER, $"Settings sync: {subscriptions.Count} server-side options.");
        }

        // Main thread, when hosting stops
        internal static void Stop() {
            foreach(var subscription in subscriptions) subscription.Key.ValueChanged -= subscription.Value;
            subscriptions.Clear();
            sent.Clear();
        }

        // Any thread: called at the end of Server.GreetPlayer
        internal static void SendSnapshot(ClientData client) {
            if(!Enabled) return;
            Dispatcher.Enqueue(() => {
                var message = new SettingsMessage { values = ServerSideOptions().Select(ModOptionBridge.Read).ToList() };
                Send(new[] { client }, ModSyncPacket.SETTINGS_SNAPSHOT, message);
            });
        }

        private static void OnChanged(ModOption option) {
            if(ModManager.serverInstance == null) return;
            OptionValue value = ModOptionBridge.Read(option);
            if(!sent.IsChange(value)) return; // applied again with the same value: joiners already have it
            var message = new SettingsMessage();
            message.values.Add(value);
            Send(ModManager.serverInstance.Clients.Where(ModSyncHost.IsPlaying), ModSyncPacket.SETTING_CHANGED, message);
        }

        private static void Send(IEnumerable<ClientData> clients, byte kind, SettingsMessage message) {
            if(ModManager.serverInstance == null || message.values.Count == 0) return;
            List<ModSyncPacket> packets = ModSyncPacket.Create(kind, JsonConvert.SerializeObject(message));
            foreach(ClientData client in clients) {
                foreach(ModSyncPacket packet in packets) ModManager.serverInstance.netamiteServer.SendTo(client, packet);
            }
        }

        private static IEnumerable<ModOption> ServerSideOptions() {
            var settings = ModManager.safeFile.hostingSettings;
            foreach(ThunderRoad.ModManager.ModData mod in ModOptionBridge.LoadedMods()) {
                bool isAmp = ModOptionBridge.IsAmp(mod);
                foreach(ModOption option in mod.modOptions) {
                    if(option == null) continue;
                    if(SettingsRules.IsServerSide(mod.folderName, option.name, option.category, isAmp, settings.personalMods, settings.settingOverrides)) yield return option;
                }
            }
        }
    }
}
