using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;

namespace CullExpiredFix
{
    [HarmonyPatch(typeof(RegionFileManager), nameof(RegionFileManager.CullExpiredChunks))]
    public static class CullPatch
    {
        public const ChunkProtectionLevel SoftProtection =
            ChunkProtectionLevel.NearOfflinePlayer
            | ChunkProtectionLevel.NearQuestObjective
            | ChunkProtectionLevel.OfflinePlayer
            | ChunkProtectionLevel.QuestObjective
            | ChunkProtectionLevel.CurrentlySynced;

        public const int MaxChunksToCull = 10000;

        public static volatile bool Disabled;

        private static readonly HashSet<long> resetSet = new HashSet<long>();
        private static int forceNext;
        private static long lastRunTicks;
        private static long lastLogTicks;

        public static void RequestImmediate()
        {
            Interlocked.Exchange(ref forceNext, 1);
        }

        public static double SecondsSinceRun()
        {
            long t = Interlocked.Read(ref lastRunTicks);
            if (t == 0L)
            {
                return -1.0;
            }
            return Stats.Ms(Stats.Now() - t) / 1000.0;
        }

        [HarmonyPriority(Priority.Last)]
        public static bool Prefix(RegionFileManager __instance, out long __state)
        {
            __state = 0L;

            if (Disabled || __instance == null)
            {
                return true;
            }

            Interlocked.Increment(ref Stats.Entered);

            if (__instance.maxChunkAge < 0L && __instance.resetRequestedChunks.Count == 0)
            {
                MaybeLog();
                return false;
            }

            bool forced = Interlocked.Exchange(ref forceNext, 0) != 0;

            if (Settings.Throttle && !forced)
            {
                long last = Interlocked.Read(ref lastRunTicks);
                if (last != 0L && Stats.Ms(Stats.Now() - last) < Settings.IntervalSec * 1000.0)
                {
                    Interlocked.Increment(ref Stats.Skipped);
                    MaybeLog();
                    return false;
                }
            }

            Interlocked.Exchange(ref lastRunTicks, Stats.Now());

            if (!Settings.FastScan)
            {
                __state = Stats.Now();
                MaybeLog();
                return true;
            }

            try
            {
                Run(__instance);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref Stats.Fallbacks);
                Disabled = true;
                Log.Error("[CullExpiredFix] fast scan failed, reverting to vanilla CullExpiredChunks for the rest of this session: " + ex);
            }

            MaybeLog();
            return false;
        }

        public static void Postfix(RegionFileManager __instance, long __state)
        {
            if (__state == 0L || __instance == null)
            {
                return;
            }

            int keys = 0;
            try
            {
                Dictionary<long, uint> csd = __instance.chunksInSaveDir;
                if (csd != null)
                {
                    keys = csd.Count;
                }
            }
            catch
            {
            }

            Stats.AddScan(__state, keys, 0, true);
        }

        private static void Run(RegionFileManager rfm)
        {
            long t0 = Stats.Now();
            int keys = 0;
            int removed = 0;

            lock (rfm.saveLock)
            {
                lock (rfm.chunksInSaveDir)
                {
                    if (rfm.protectionLevelsDirty)
                    {
                        rfm.UpdateChunkProtectionLevels();
                    }
                    if (rfm.groupTimestampsDirty)
                    {
                        rfm.UpdateGroupTimestamps();
                    }

                    Dictionary<long, uint> csd = rfm.chunksInSaveDir;
                    Dictionary<long, ChunkProtectionLevel> prot = rfm.chunkProtectionLevels;
                    List<long> resetReq = rfm.resetRequestedChunks;
                    List<long> expired = rfm.expiredChunks;
                    LongSetGroups groups = rfm.chunkGroups;
                    Dictionary<LongSetGroups.Group, uint> groupTimestamps = rfm.groupTimestamps;
                    long maxAge = rfm.maxChunkAge;

                    keys = csd.Count;

                    if (maxAge >= 0L)
                    {
                        uint now = GameUtils.WorldTimeToTotalMinutes(GameManager.Instance.World.worldTime);

                        resetSet.Clear();
                        for (int i = 0; i < resetReq.Count; i++)
                        {
                            resetSet.Add(resetReq[i]);
                        }

                        bool anyGroups = groups != null && groupTimestamps != null && groups.GroupedLongsCount > 0;
                        bool anyReset = resetSet.Count > 0;

                        foreach (KeyValuePair<long, uint> kv in csd)
                        {
                            long key = kv.Key;

                            if (!anyReset || !resetSet.Contains(key))
                            {
                                uint ts = kv.Value;
                                if (anyGroups)
                                {
                                    LongSetGroups.Group group;
                                    uint groupTs;
                                    if (groups.TryGetGroup(key, out group) && groupTimestamps.TryGetValue(group, out groupTs))
                                    {
                                        ts = groupTs;
                                    }
                                }
                                if (now - ts <= maxAge)
                                {
                                    continue;
                                }
                            }

                            ChunkProtectionLevel level;
                            if (prot.TryGetValue(key, out level))
                            {
                                if ((level & ~SoftProtection) != ChunkProtectionLevel.None)
                                {
                                    resetReq.Remove(key);
                                }
                                continue;
                            }

                            expired.Add(key);
                            resetReq.Remove(key);
                            if (expired.Count < MaxChunksToCull)
                            {
                                continue;
                            }
                            break;
                        }
                    }
                    else
                    {
                        for (int i = resetReq.Count - 1; i >= 0; i--)
                        {
                            long key = resetReq[i];
                            ChunkProtectionLevel level;
                            if (prot.TryGetValue(key, out level))
                            {
                                if ((level & ~SoftProtection) != ChunkProtectionLevel.None)
                                {
                                    resetReq.Remove(key);
                                }
                            }
                            else
                            {
                                expired.Add(key);
                                resetReq.RemoveAt(i);
                                if (expired.Count >= MaxChunksToCull)
                                {
                                    break;
                                }
                            }
                        }
                    }

                    removed = expired.Count;
                    rfm.RemoveChunks(expired, true, false);
                    expired.Clear();
                }
            }

            Stats.AddScan(t0, keys, removed, false);
        }

        private static void MaybeLog()
        {
            int every = Settings.LogEverySec;
            if (every <= 0)
            {
                return;
            }

            long now = Stats.Now();
            long last = Interlocked.Read(ref lastLogTicks);
            if (last != 0L && Stats.Ms(now - last) < every * 1000.0)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref lastLogTicks, now, last) != last)
            {
                return;
            }
            if (last == 0L)
            {
                return;
            }

            Log.Out("[CullExpiredFix] " + Settings.Describe() + " " + Stats.Describe());
        }
    }

    [HarmonyPatch(typeof(RegionFileManager), nameof(RegionFileManager.RequestChunkReset))]
    public static class RequestResetPatch
    {
        public static void Postfix()
        {
            CullPatch.RequestImmediate();
        }
    }

    [HarmonyPatch(typeof(RegionFileManager), nameof(RegionFileManager.OnGamePrefChanged))]
    public static class GamePrefPatch
    {
        public static void Postfix(EnumGamePrefs pref)
        {
            if (pref == EnumGamePrefs.MaxChunkAge)
            {
                CullPatch.RequestImmediate();
            }
        }
    }
}
