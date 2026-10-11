using AMP.Data;
using AMP.Logging;
using Discord;
using System;
using System.IO;
using ThunderRoad;
using UnityEngine;

namespace AMP.Discord {
    internal class DiscordIntegration {

        public static DiscordIntegration currentInstance;

        ActivityManager activityManager;
        UserManager userManager;

        internal User currentUser;

        public bool isInitialized = false;

        // Never throws: callers run every FixedUpdate. Without Discord (Quest, or the SDK can't be set up) it stays an inactive instance.
        public static DiscordIntegration Instance { 
            get { 
                if(currentInstance == null) {
                    try {
                        currentInstance = new DiscordIntegration();
                    } catch(Exception e) {
                        Log.Warn("Discord integration is off: " + e.Message);
                        currentInstance = new DiscordIntegration(false);
                    }
                }
                return currentInstance;
            }
        }

        private DiscordIntegration(bool unused) { } // inactive: discord stays null, every method returns early

        private string discordSdkFile = Path.Combine(Application.dataPath, "..", "discord_game_sdk.dll");

        private static global::Discord.Discord discord;
        internal DiscordIntegration() {
            // Quest: there is no Discord SDK for Android, and the SDK file would be written into the apk's path
            if(Application.platform == RuntimePlatform.Android) {
                Log.Info("Discord integration isn't available on this platform.");
                return;
            }

            try {
                CheckForDiscordSDK();
                if(discord == null) discord = new global::Discord.Discord(Config.DISCORD_APP_ID, (UInt64) CreateFlags.NoRequireDiscord);
                isInitialized = true;
            } catch(Exception) {
                discord = null;
                Log.Warn("Couldn't initialize discord integration, discord status won't update.");
                return;
            }

            activityManager = discord.GetActivityManager();
            userManager = discord.GetUserManager();

            // Get yourself
            //currentUser = userManager.GetCurrentUser();
            userManager.OnCurrentUserUpdate += () => {
                currentUser = userManager.GetCurrentUser();
            };

            activityManager.OnActivityJoin += ActivityManager_OnActivityJoin;

            RegisterEvents();
        }


        private void CheckForDiscordSDK() {
            if(!File.Exists(discordSdkFile)) {
                Log.Warn("Couldn't find discord_game_sdk.dll, extracting it now.");
                using(var file = new FileStream(discordSdkFile, FileMode.Create, FileAccess.Write)) {
                    file.Write(Properties.Resources.discord_game_sdk, 0, Properties.Resources.discord_game_sdk.Length);
                }
            }
        }

        private void ActivityManager_OnActivityJoin(string secret) {
            Log.Debug(Defines.DISCORD_SDK, $"Joining through discord: {secret}");
            //NetworkHandler.UseJoinSecret(secret); // TODO
        }

        internal void RunCallbacks() {
            if(discord == null) return;
            if(!isInitialized) return;

            try {
                discord.RunCallbacks();
            }catch(ResultException e) {
                if(e.Result == Result.NotRunning) {
                    isInitialized = false;
                }
            }
        }

        internal void RegisterEvents() {

        }

        private long millis = 0;
        internal void UpdateActivity() {
            if(discord == null) return;
            if(millis > DateTimeOffset.Now.ToUnixTimeMilliseconds() - 1000) return;

            if(millis <= 0) {
                millis = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            }

            Activity activity;

            string details = "Blade & Sorcery";
            string join_key = null;
            string large_image_key = "default";

            if(ModManager.clientInstance != null && ModManager.clientSync != null) {
                //join_key = ModManager.clientInstance.nw.GetJoinSecret(); // Not really working, need to find a better way
            }

            if(Level.current != null) {
                details         = Level.current.data.id;
                large_image_key = Level.current.data.id.ToLower();
            }

            if(join_key != null && join_key.Length > 0) {
                activity = new global::Discord.Activity {
                    State = "Playing Multiplayer (" + Defines.MOD_NAME + ")",
                    Details = details,
                    Instance = true,
                    Party = {
                        Id = currentUser.Id.ToString() + ":" + millis,
                        Size = {
                            CurrentSize = ModManager.clientSync.syncData.players.Count
                            #if !DEBUG_SELF
                                            + 1
                            #endif
                                            ,
                            MaxSize     = ModManager.clientInstance.serverInfo.max_players
                        }
                    },
                    Secrets = {
                        Join = join_key
                    },
                    Assets = {
                        LargeImage = large_image_key,
                        LargeText  = details
                    }
                };
            } else {
                activity = new global::Discord.Activity {
                    State = "Playing Solo (" + Defines.MOD_NAME + ")",
                    Details = details,
                    Instance = true,
                    Assets = {
                        LargeImage = large_image_key,
                        LargeText  = details
                    }
                };
            }

            activityManager.UpdateActivity(activity, (result) => {
                //Log.Debug($"Updated Activity {result}");
            });
        }
    }
}
