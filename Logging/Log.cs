using System;
using System.Collections.Concurrent;
using System.Collections.Generic;


namespace AMP.Logging {
    public static class Log {

        public enum Type {
            DEBUG   = 0,
            INFO    = 1,
            WARNING = 2,
            ERROR   = 3
        }

        public enum LoggerType {
            UNITY,
            CONSOLE
        }

        public static LoggerType loggerType = LoggerType.CONSOLE;

        public static Action<Type, string> onLogMessage;

        #region Debug
        public static void Debug(object obj) {
            if(obj == null) Debug("null");
            else Debug(obj.ToString());
        }

        public static void Debug(string message) {
            Msg(Type.DEBUG, message);
        }

        public static void Debug(string source, object obj) {
            if(obj == null) Debug(source, "null");
            else Debug(source, obj.ToString());
        }

        public static void Debug(string source, string message) {
            Msg(Type.DEBUG, $"[{source}] " + message);
        }
        #endregion

        #region Info
        public static void Info(object obj) {
            if(obj == null) Info("null");
            else Info(obj.ToString());
        }

        public static void Info(string message) {
            Msg(Type.INFO, message);
        }

        public static void Info(string source, object obj) {
            if(obj == null) Info(source, "null");
            else Info(source, obj.ToString());
        }

        public static void Info(string source, string message) {
            Msg(Type.INFO, $"[{source}] " + message);
        }
        #endregion

        #region Warn
        public static void Warn(object obj) {
            if(obj == null) Warn("null");
            else Warn(obj.ToString());
        }

        public static void Warn(string message) {
            Msg(Type.WARNING, message);
        }

        public static void Warn(string source, object obj) {
            if(obj == null) Warn(source, "null");
            else Warn(source, obj.ToString());
        }

        public static void Warn(string source, string message) {
            Msg(Type.WARNING, $"[{source}] " + message);
        }
        #endregion

        #region Error
        public static void Err(object obj) {
            if(obj == null) Err("null");
            else Err(obj.ToString());
        }

        public static void Err(string message) {
            Msg(Type.ERROR, message);
        }

        public static void Err(string source, object obj) {
            if(obj == null) Err(source, "null");
            else Err(source, obj.ToString());
        }

        public static void Err(string source, string message) {
            Msg(Type.ERROR, $"[{source}] " + message);
        }
        #endregion


        public static void Line(char character) {
            int cnt = 50;
            if(loggerType == LoggerType.CONSOLE) cnt = Console.WindowWidth;
            Msg(Type.INFO, "".PadLeft(cnt, character));
        }

        public static void Line(char character, string title) {
            int cnt = 50;
            if(loggerType == LoggerType.CONSOLE) cnt = Console.WindowWidth;
            Msg(Type.INFO, $" {title} ".PadLeft(cnt / 2, character).PadRight(cnt, character));
        }

        public static void Msg(Type type, string message) {
            try { if(onLogMessage != null) onLogMessage.Invoke(type, message); } catch { }
            switch(type) {
                case Type.DEBUG:
                    #if DEBUG_MESSAGES
                    if(loggerType == LoggerType.UNITY) UnityEngine.Debug.Log(message);
                    else if(loggerType == LoggerType.CONSOLE) ConsoleLine(message);
                    #endif

                    break;

                case Type.INFO:
                    if(loggerType == LoggerType.UNITY) UnityEngine.Debug.Log(message);
                    else if(loggerType == LoggerType.CONSOLE) ConsoleLine(message);

                    break;

                case Type.WARNING:
                    if(loggerType == LoggerType.UNITY) UnityEngine.Debug.LogWarning(message);
                    else if(loggerType == LoggerType.CONSOLE) ConsoleLine($"<color=#FFFF00>{message}</color>");

                    break;

                case Type.ERROR:
                    if(loggerType == LoggerType.UNITY) UnityEngine.Debug.LogError(message);
                    else if(loggerType == LoggerType.CONSOLE) ConsoleLine($"<color=#FF0000>{message}</color>");

                    break;
            
                default: break;
            }
        }

        private static readonly ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();

        private static void ConsoleLine(string message) {
            messageQueue.Enqueue(message);

            ProcessQueue();
        }

        private static void ProcessQueue() {
            string message;
            lock(messageQueue) {
                while(messageQueue.TryDequeue(out message)) {
                    List<ConsoleColor> colors = new List<ConsoleColor>();
                    char[] chars = message.ToCharArray();

                    for(int i = 0; i < chars.Length; ) {
                        if(message.Substring(i).StartsWith("<color=")) {
                            int rgb;
                            if(!int.TryParse(message.Substring(i + 8, 6), System.Globalization.NumberStyles.HexNumber, null, out rgb)) rgb = 0xFFFFFF;
                            colors.Add(ClosestConsoleColor((byte) (rgb >> 16), (byte) (rgb >> 8), (byte) rgb));
                            Console.ForegroundColor = colors[colors.Count - 1];
                            i += 15;
                        } else if(message.Substring(i).StartsWith("</color>")) {
                            if(colors.Count > 0) colors.RemoveAt(colors.Count - 1);
                            if(colors.Count > 0) {
                                Console.ForegroundColor = colors[colors.Count - 1];
                            } else {
                                Console.ResetColor();
                            }
                            i += 8;
                        } else {
                            if(i >= chars.Length) break;
                            Console.Write(chars[i]);
                            i++;
                        }
                    }
                    Console.WriteLine();
                    Console.ResetColor();
                }
            }
        }

        // The named colours System.Drawing gave each ConsoleColor (DarkYellow as Orange), without referencing System.Drawing
        // (not part of the Quest build)
        private static readonly int[] ConsoleRgb = {
            0x000000, 0x00008B, 0x006400, 0x008B8B, 0x8B0000, 0x8B008B, 0xFFA500, 0x808080,
            0xA9A9A9, 0x0000FF, 0x008000, 0x00FFFF, 0xFF0000, 0xFF00FF, 0xFFFF00, 0xFFFFFF,
        };

        private static ConsoleColor ClosestConsoleColor(byte r, byte g, byte b) {
            ConsoleColor ret = 0;
            double rr = r, gg = g, bb = b, delta = double.MaxValue;

            foreach(ConsoleColor cc in Enum.GetValues(typeof(ConsoleColor))) {
                int rgb = ConsoleRgb[(int) cc];
                var t = Math.Pow((rgb >> 16) - rr, 2.0) + Math.Pow(((rgb >> 8) & 0xFF) - gg, 2.0) + Math.Pow((rgb & 0xFF) - bb, 2.0);
                if(t == 0.0)
                    return cc;
                if(t < delta) {
                    delta = t;
                    ret = cc;
                }
            }
            return ret;
        }
    }
}
