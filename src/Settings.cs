using System;
using System.IO;
using System.Xml;

namespace CullExpiredFix
{
    public static class Settings
    {
        public const int MinIntervalSec = 0;
        public const int MaxIntervalSec = 3600;

        public static volatile bool FastScan = true;
        public static volatile bool Throttle = true;
        public static volatile int IntervalSec = 30;
        public static volatile int LogEverySec = 600;

        private static string path;

        public static void Load(string modPath)
        {
            path = Path.Combine(modPath, "Config.xml");
            if (!File.Exists(path))
            {
                Save();
                return;
            }

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(path);
                XmlElement root = doc.DocumentElement;
                if (root == null)
                {
                    return;
                }

                FastScan = ReadBool(root, "fastScan", FastScan);
                Throttle = ReadBool(root, "throttle", Throttle);
                IntervalSec = Clamp(ReadInt(root, "intervalSeconds", IntervalSec), MinIntervalSec, MaxIntervalSec);
                LogEverySec = Clamp(ReadInt(root, "logEverySeconds", LogEverySec), 0, 86400);
            }
            catch (Exception ex)
            {
                Log.Warning("[CullExpiredFix] Config.xml unreadable, using defaults: " + ex.Message);
            }
        }

        public static void Save()
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                XmlDocument doc = new XmlDocument();
                XmlElement root = doc.CreateElement("CullExpiredFix");
                doc.AppendChild(root);
                root.SetAttribute("fastScan", FastScan ? "true" : "false");
                root.SetAttribute("throttle", Throttle ? "true" : "false");
                root.SetAttribute("intervalSeconds", IntervalSec.ToString());
                root.SetAttribute("logEverySeconds", LogEverySec.ToString());
                doc.Save(path);
            }
            catch (Exception ex)
            {
                Log.Warning("[CullExpiredFix] could not write Config.xml: " + ex.Message);
            }
        }

        public static int Clamp(int v, int lo, int hi)
        {
            if (v < lo)
            {
                return lo;
            }
            if (v > hi)
            {
                return hi;
            }
            return v;
        }

        private static bool ReadBool(XmlElement e, string name, bool fallback)
        {
            string s = e.GetAttribute(name);
            bool v;
            return bool.TryParse(s, out v) ? v : fallback;
        }

        private static int ReadInt(XmlElement e, string name, int fallback)
        {
            string s = e.GetAttribute(name);
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }

        public static string Describe()
        {
            return "fastScan=" + (FastScan ? "on" : "off")
                + " throttle=" + (Throttle ? "on" : "off")
                + " interval=" + IntervalSec + "s"
                + " logEvery=" + LogEverySec + "s";
        }
    }
}
