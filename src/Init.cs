using System;
using System.Collections.Generic;
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

                Log.Out("[CullExpiredFix] active, " + Settings.Describe());
            }
            catch (Exception ex)
            {
                CullPatch.Disabled = true;
                Log.Error("[CullExpiredFix] init failed, vanilla behaviour kept: " + ex);
            }
        }

        private static string Verify()
        {
            Type rfm = typeof(RegionFileManager);

            string[] fields =
            {
                "chunksInSaveDir", "chunkProtectionLevels", "resetRequestedChunks",
                "expiredChunks", "groupsByChunkKey", "groupTimestamps",
                "protectionLevelsDirty", "maxChunkAge", "saveLock"
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
                "CullExpiredChunks", "UpdateChunkProtectionLevels",
                "RemoveChunks", "RequestChunkReset", "OnGamePrefChanged"
            };
            for (int i = 0; i < methods.Length; i++)
            {
                if (AccessTools.Method(rfm, methods[i]) == null)
                {
                    return "RegionFileManager." + methods[i] + " not found";
                }
            }

            MethodInfo removeChunks = AccessTools.Method(rfm, "RemoveChunks",
                new Type[] { typeof(ICollection<long>), typeof(bool) });
            if (removeChunks == null)
            {
                return "RegionFileManager.RemoveChunks(ICollection<long>, bool) not found";
            }
            if (AccessTools.Method(typeof(GameUtils), "WorldTimeToTotalMinutes") == null)
            {
                return "GameUtils.WorldTimeToTotalMinutes not found";
            }

            return null;
        }
    }
}
