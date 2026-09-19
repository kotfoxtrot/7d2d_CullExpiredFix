using System;
using System.Reflection;
using HarmonyLib;

namespace CullExpiredFix
{
    public class Init : IModApi
    {
        public const string HarmonyId = "com.sdtdtest.cullexpiredfix";

        public void InitMod(Mod _modInstance)
        {
            try
            {
                Settings.Load(_modInstance.Path);

                string missing = Verify();
                if (missing != null)
                {
                    Log.Error("[CullExpiredFix] game code changed, patch skipped: " + missing);
                    CullPatch.Disabled = true;
                    return;
                }

                Harmony harmony = new Harmony(HarmonyId);
                harmony.CreateClassProcessor(typeof(CullPatch)).Patch();
                harmony.CreateClassProcessor(typeof(RequestResetPatch)).Patch();
                harmony.CreateClassProcessor(typeof(GamePrefPatch)).Patch();

                Settings.StartWatch();
                ModEvents.GameShutdown.RegisterHandler(OnGameShutdown);

                Log.Out("[CullExpiredFix] active, " + Settings.Describe() + ", config: " + Settings.Path);
            }
            catch (Exception ex)
            {
                CullPatch.Disabled = true;
                Log.Error("[CullExpiredFix] init failed, vanilla behaviour kept: " + ex);
            }
        }

        private void OnGameShutdown(ref ModEvents.SGameShutdownData data)
        {
            try
            {
                Settings.StopWatch();
            }
            catch
            {
            }
        }

        private static string Verify()
        {
            Type rfm = typeof(RegionFileManager);

            string[] fields =
            {
                "chunksInSaveDir", "chunkProtectionLevels", "resetRequestedChunks",
                "expiredChunks", "chunkGroups", "groupTimestamps",
                "protectionLevelsDirty", "groupTimestampsDirty", "maxChunkAge", "saveLock"
            };
            for (int i = 0; i < fields.Length; i++)
            {
                if (AccessTools.Field(rfm, fields[i]) == null)
                {
                    return "RegionFileManager." + fields[i] + " not found";
                }
            }

            string[] methods =
            {
                "CullExpiredChunks", "UpdateChunkProtectionLevels", "UpdateGroupTimestamps",
                "RemoveChunks", "RequestChunkReset", "OnGamePrefChanged"
            };
            for (int i = 0; i < methods.Length; i++)
            {
                if (AccessTools.Method(rfm, methods[i]) == null)
                {
                    return "RegionFileManager." + methods[i] + " not found";
                }
            }

            MethodInfo tryGetGroup = AccessTools.Method(typeof(LongSetGroups), "TryGetGroup");
            if (tryGetGroup == null)
            {
                return "LongSetGroups.TryGetGroup not found";
            }
            if (AccessTools.Property(typeof(LongSetGroups), "GroupedLongsCount") == null)
            {
                return "LongSetGroups.GroupedLongsCount not found";
            }
            if (AccessTools.Method(typeof(GameUtils), "WorldTimeToTotalMinutes") == null)
            {
                return "GameUtils.WorldTimeToTotalMinutes not found";
            }

            return null;
        }
    }
}
