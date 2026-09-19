using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Xml;

namespace CullExpiredFix
{
    public static class Settings
    {
        public const int MinIntervalSec = 0;
        public const int MaxIntervalSec = 3600;
        public const int MaxLogEverySec = 86400;

        public static volatile bool FastScan = true;
        public static volatile bool Throttle = true;
        public static volatile int IntervalSec = 30;
        public static volatile int LogEverySec = 600;

        public static long Reloads;
        public static string LastError = "";

        private static string path = "";
        private static FileSystemWatcher watcher;
        private static Thread poller;
        private static readonly AutoResetEvent poke = new AutoResetEvent(false);
        private static volatile bool stopping;
        private static long lastStampTicks;
        private static long lastLength = -1L;
        private static readonly object ioLock = new object();

        public static string Path
        {
            get { return path; }
        }

        public static bool Watching
        {
            get { return poller != null && poller.IsAlive; }
        }

        public static void Load(string modPath)
        {
            path = System.IO.Path.Combine(modPath ?? ".", "Config.xml");
            if (!File.Exists(path))
            {
                Save();
                Stamp();
                return;
            }
            Apply(false);
            Stamp();
        }

        public static bool Reload()
        {
            bool ok = Apply(true);
            Stamp();
            return ok;
        }

        private static bool Apply(bool countReload)
        {
            lock (ioLock)
            {
                try
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(path);
                    XmlElement root = doc.DocumentElement;
                    if (root == null)
                    {
                        LastError = "no root element";
                        return false;
                    }

                    bool fastScan = FastScan;
                    bool throttle = Throttle;
                    int interval = IntervalSec;
                    int logEvery = LogEverySec;

                    foreach (XmlNode node in root.ChildNodes)
                    {
                        XmlElement e = node as XmlElement;
                        if (e == null || e.Name != "property")
                        {
                            continue;
                        }
                        string name = e.GetAttribute("name");
                        string value = e.GetAttribute("value");
                        switch (name)
                        {
                            case "FastScan":
                                fastScan = ParseBool(value, fastScan);
                                break;
                            case "Throttle":
                                throttle = ParseBool(value, throttle);
                                break;
                            case "IntervalSeconds":
                                interval = Clamp(ParseInt(value, interval), MinIntervalSec, MaxIntervalSec);
                                break;
                            case "LogEverySeconds":
                                logEvery = Clamp(ParseInt(value, logEvery), 0, MaxLogEverySec);
                                break;
                        }
                    }

                    FastScan = fastScan;
                    Throttle = throttle;
                    IntervalSec = interval;
                    LogEverySec = logEvery;
                    LastError = "";
                    if (countReload)
                    {
                        Reloads++;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return false;
                }
            }
        }

        public static void Save()
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            lock (ioLock)
            {
                try
                {
                    XmlDocument doc = new XmlDocument();
                    doc.AppendChild(doc.CreateXmlDeclaration("1.0", "UTF-8", null));
                    XmlElement root = doc.CreateElement("CullExpiredFix");
                    doc.AppendChild(root);
                    Put(doc, root, "FastScan", FastScan ? "true" : "false");
                    Put(doc, root, "Throttle", Throttle ? "true" : "false");
                    Put(doc, root, "IntervalSeconds", IntervalSec.ToString(CultureInfo.InvariantCulture));
                    Put(doc, root, "LogEverySeconds", LogEverySec.ToString(CultureInfo.InvariantCulture));
                    doc.Save(path);
                    Stamp();
                    LastError = "";
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Log.Warning("[CullExpiredFix] could not write Config.xml: " + ex.Message);
                }
            }
        }

        private static void Put(XmlDocument doc, XmlElement root, string name, string value)
        {
            XmlElement e = doc.CreateElement("property");
            e.SetAttribute("name", name);
            e.SetAttribute("value", value);
            root.AppendChild(e);
        }

        public static void StartWatch()
        {
            if (string.IsNullOrEmpty(path) || poller != null)
            {
                return;
            }
            stopping = false;
            poller = new Thread(Loop);
            poller.IsBackground = true;
            poller.Name = "CullFixConfig";
            poller.Priority = ThreadPriority.BelowNormal;
            poller.Start();

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                string file = System.IO.Path.GetFileName(path);
                watcher = new FileSystemWatcher(dir, file);
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime;
                watcher.Changed += OnFileEvent;
                watcher.Created += OnFileEvent;
                watcher.Renamed += OnFileEvent;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                watcher = null;
                Log.Warning("[CullExpiredFix] FileSystemWatcher unavailable, polling every second: " + ex.Message);
            }
        }

        public static void StopWatch()
        {
            stopping = true;
            try
            {
                if (watcher != null)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                    watcher = null;
                }
            }
            catch
            {
            }
            try
            {
                poke.Set();
            }
            catch
            {
            }
            poller = null;
        }

        private static void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            try
            {
                poke.Set();
            }
            catch
            {
            }
        }

        private static void Loop()
        {
            while (!stopping)
            {
                poke.WaitOne(1000);
                if (stopping)
                {
                    return;
                }
                try
                {
                    if (!Changed())
                    {
                        continue;
                    }
                    Thread.Sleep(120);
                    if (!File.Exists(path))
                    {
                        continue;
                    }
                    if (!Changed())
                    {
                        continue;
                    }
                    if (Reload())
                    {
                        Log.Out("[CullExpiredFix] Config.xml reloaded #" + Reloads + ": " + Describe());
                    }
                    else
                    {
                        Log.Warning("[CullExpiredFix] Config.xml reload failed, keeping current values: " + LastError);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[CullExpiredFix] config watcher error: " + ex.Message);
                }
            }
        }

        private static bool Changed()
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    return false;
                }
                return fi.LastWriteTimeUtc.Ticks != Interlocked.Read(ref lastStampTicks)
                    || fi.Length != Interlocked.Read(ref lastLength);
            }
            catch
            {
                return false;
            }
        }

        private static void Stamp()
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    return;
                }
                Interlocked.Exchange(ref lastStampTicks, fi.LastWriteTimeUtc.Ticks);
                Interlocked.Exchange(ref lastLength, fi.Length);
            }
            catch
            {
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

        private static bool ParseBool(string s, bool fallback)
        {
            bool v;
            if (bool.TryParse(s, out v))
            {
                return v;
            }
            if (s == "1" || s == "on" || s == "yes")
            {
                return true;
            }
            if (s == "0" || s == "off" || s == "no")
            {
                return false;
            }
            return fallback;
        }

        private static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
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
