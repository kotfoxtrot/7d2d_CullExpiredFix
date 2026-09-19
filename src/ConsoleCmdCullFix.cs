using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Scripting;

namespace CullExpiredFix
{
    [Preserve]
    public class ConsoleCmdCullFix : ConsoleCmdAbstract
    {
        public override bool IsExecuteOnClient => false;

        public override int DefaultPermissionLevel => 0;

        public override string[] getCommands()
        {
            return new string[1] { "cullfix" };
        }

        public override string getDescription()
        {
            return "CullExpiredChunks throttle and fast scan";
        }

        public override string getHelp()
        {
            return "cullfix                  show status and counters\n"
                + "cullfix stats            same as above\n"
                + "cullfix reset            zero the counters\n"
                + "cullfix interval <sec>   minimum seconds between scans, 0 disables the wait\n"
                + "cullfix throttle on|off  skip scans that are too close together\n"
                + "cullfix fastscan on|off  use the mod scan loop instead of the vanilla one\n"
                + "cullfix log <sec>        counter line in the server log every n seconds, 0 disables\n"
                + "cullfix run              run one scan on the next save cycle\n"
                + "cullfix stuck [n]        list reset requests that can never complete\n"
                + "cullfix save             write Config.xml\n"
                + "cullfix reload           re-read Config.xml now";
        }

        private static void Out(string s)
        {
            SingletonMonoBehaviour<SdtdConsole>.Instance.Output(s);
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            try
            {
                string a = _params.Count > 0 ? _params[0].ToLowerInvariant() : "stats";

                if (a == "stats" || a == "status")
                {
                    Out("[CullExpiredFix] " + (CullPatch.Disabled ? "DISABLED" : "active") + " " + Settings.Describe());
                    Out("config: " + Settings.Path + (Settings.Watching ? " (watched" : " (not watched")
                        + ", reloads=" + Settings.Reloads + ")");
                    Out(Stats.Describe());
                    double since = CullPatch.SecondsSinceRun();
                    Out(since < 0.0 ? "no scan yet" : string.Format("last scan {0:F1}s ago", since));
                    return;
                }

                if (a == "reset")
                {
                    Stats.Reset();
                    Out("counters zeroed");
                    return;
                }

                if (a == "interval")
                {
                    int v;
                    if (_params.Count < 2 || !int.TryParse(_params[1], out v))
                    {
                        Out("usage: cullfix interval <seconds>");
                        return;
                    }
                    Settings.IntervalSec = Settings.Clamp(v, Settings.MinIntervalSec, Settings.MaxIntervalSec);
                    Settings.Save();
                    Out("interval = " + Settings.IntervalSec + "s");
                    return;
                }

                if (a == "log")
                {
                    int v;
                    if (_params.Count < 2 || !int.TryParse(_params[1], out v))
                    {
                        Out("usage: cullfix log <seconds>");
                        return;
                    }
                    Settings.LogEverySec = Settings.Clamp(v, 0, 86400);
                    Settings.Save();
                    Out("log every " + Settings.LogEverySec + "s");
                    return;
                }

                if (a == "throttle" || a == "fastscan")
                {
                    bool on;
                    if (_params.Count < 2 || !ParseOnOff(_params[1], out on))
                    {
                        Out("usage: cullfix " + a + " on|off");
                        return;
                    }
                    if (a == "throttle")
                    {
                        Settings.Throttle = on;
                    }
                    else
                    {
                        Settings.FastScan = on;
                    }
                    Settings.Save();
                    Out(Settings.Describe());
                    return;
                }

                if (a == "run")
                {
                    CullPatch.RequestImmediate();
                    Out("next save cycle will scan");
                    return;
                }

                if (a == "save")
                {
                    Settings.Save();
                    Out("Config.xml written: " + Settings.Path);
                    return;
                }

                if (a == "reload")
                {
                    Out(Settings.Reload()
                        ? "reloaded: " + Settings.Describe()
                        : "reload failed, current values kept: " + Settings.LastError);
                    return;
                }

                if (a == "stuck")
                {
                    int limit = 40;
                    if (_params.Count > 1)
                    {
                        int v;
                        if (int.TryParse(_params[1], out v) && v > 0)
                        {
                            limit = v > 2000 ? 2000 : v;
                        }
                    }
                    Stuck(limit);
                    return;
                }

                Out("unknown subcommand '" + _params[0] + "', see 'help cullfix'");
            }
            catch (Exception ex)
            {
                Out("cullfix error: " + ex.Message);
            }
        }

        private static bool ParseOnOff(string s, out bool on)
        {
            s = s.ToLowerInvariant();
            if (s == "on" || s == "true" || s == "1")
            {
                on = true;
                return true;
            }
            if (s == "off" || s == "false" || s == "0")
            {
                on = false;
                return true;
            }
            on = false;
            return false;
        }

        private static void Stuck(int limit)
        {
            RegionFileManager rfm = FindManager();
            if (rfm == null)
            {
                Out("RegionFileManager not reachable, is the world loaded?");
                return;
            }

            int pending = 0;
            int willReset = 0;
            int willDrop = 0;
            List<string> stuck = new List<string>();
            int stuckCount = 0;

            lock (rfm.saveLock)
            {
                lock (rfm.chunksInSaveDir)
                {
                    List<long> req = rfm.resetRequestedChunks;
                    pending = req.Count;
                    for (int i = 0; i < req.Count; i++)
                    {
                        long key = req[i];
                        ChunkProtectionLevel level;
                        if (!rfm.chunkProtectionLevels.TryGetValue(key, out level))
                        {
                            willReset++;
                            continue;
                        }
                        if ((level & ~CullPatch.SoftProtection) != ChunkProtectionLevel.None)
                        {
                            willDrop++;
                            continue;
                        }
                        stuckCount++;
                        if (stuck.Count < limit)
                        {
                            StringBuilder sb = new StringBuilder(64);
                            sb.Append("  (").Append(WorldChunkCache.extractX(key) << 4).Append(',')
                              .Append(WorldChunkCache.extractZ(key) << 4).Append(')')
                              .Append("  key=").Append(key)
                              .Append("  protection=").Append(level);
                            stuck.Add(sb.ToString());
                        }
                    }
                }
            }

            Out(string.Format("reset requests: {0} pending, {1} will reset, {2} will drop from the list, {3} stuck", pending, willReset, willDrop, stuckCount));
            Out("stuck = reset requested, but held by soft protection only, so the entry is never cleared and never acted on");
            for (int i = 0; i < stuck.Count; i++)
            {
                Out(stuck[i]);
            }
            if (stuckCount > stuck.Count)
            {
                Out("  +" + (stuckCount - stuck.Count) + " more, raise the limit: cullfix stuck " + stuckCount);
            }
            Out("each stuck entry costs one extra comparison per saved chunk per scan");
        }

        private static RegionFileManager FindManager()
        {
            try
            {
                GameManager gm = GameManager.Instance;
                if (gm == null || gm.World == null || gm.World.ChunkCache == null)
                {
                    return null;
                }
                ChunkProviderAbstract provider = gm.World.ChunkCache.ChunkProvider as ChunkProviderAbstract;
                if (provider == null)
                {
                    return null;
                }
                System.Reflection.FieldInfo f = HarmonyLib.AccessTools.Field(provider.GetType(), "m_RegionFileManager");
                if (f == null)
                {
                    return null;
                }
                return f.GetValue(provider) as RegionFileManager;
            }
            catch
            {
                return null;
            }
        }
    }
}
