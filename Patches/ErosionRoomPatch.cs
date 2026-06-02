using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Logging;
using FogboundPaths.FogOfWar;

namespace FogboundPaths.Patches;

[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterMapCoord))]
public static class EnterMapCoordRevisitPatch
{
    internal static bool IsLoadingSave;

    [HarmonyPrefix]
    private static bool Prefix(RunManager __instance, MapCoord coord, ref Task __result)
    {
        var stateProp = typeof(RunManager).GetProperty("State",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        var state = stateProp?.GetValue(__instance) as RunState;
        if (state == null) return true;

        if (!state.VisitedMapCoords.Contains(coord))
            return true;

        var method = typeof(RunManager).GetMethod("EnterMapCoordInternal",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) return true;

        if (!IsLoadingSave)
        {
            var visitedField = typeof(RunState).GetField("_visitedMapCoords",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (visitedField?.GetValue(state) is List<MapCoord> list)
                list.Add(coord);
        }

        __result = (Task)method.Invoke(__instance, [coord, null, true])!;
        return false;
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterMapPointInternal))]
public static class ErosionAndRevisitPatch
{
    private const BindingFlags _nf = BindingFlags.NonPublic | BindingFlags.Instance;

    [HarmonyPrefix]
    private static bool Prefix(RunManager __instance, int actFloor, ref MapPointType pointType,
        AbstractRoom? preFinishedRoom, bool saveGame)
    {
        RevisitHelper.IsRevisit = false;
        if (preFinishedRoom != null) return true;
        if (EnterMapCoordRevisitPatch.IsLoadingSave) return true;

        var stateProp = typeof(RunManager).GetProperty("State", _nf | BindingFlags.Public);
        var state = stateProp?.GetValue(__instance) as RunState;
        if (state == null) return true;

        var coord = state.CurrentMapCoord;
        if (!coord.HasValue) return true;

        if (pointType == MapPointType.Ancient || pointType == MapPointType.Boss)
            return true;

        int visitCount = state.VisitedMapCoords.Count(v => v == coord.Value);

        if (visitCount > 1)
        {
            pointType = MapPointType.RestSite;
            RevisitHelper.IsRevisit = true;
            return true;
        }

        bool isCombat = pointType == MapPointType.Monster ||
                        pointType == MapPointType.Elite;
        bool isEroded = FogOfWarManager.IsPointEroded(coord.Value, state.CurrentActIndex);

        if (!isEroded) return true;

        if (isCombat) return true;

        RevisitHelper.IsErosionHandled = false;
        ErosionHelper.MarkErosionEmpty(coord.Value, pointType);
        return true;
    }
}

internal static class ErosionHelper
{
    internal static readonly Dictionary<MapCoord, MapPointType> ErosionEmptyCoords = [];

    internal static void MarkErosionEmpty(MapCoord coord, MapPointType originalType) => ErosionEmptyCoords[coord] = originalType;

    internal static void Clear()
    {
        ErosionEmptyCoords.Clear();
    }
}

internal static class RevisitHelper
{
    public static bool IsRevisit;
    public static bool IsErosionEmptyRoom;
    internal static bool IsErosionHandled;
}

[HarmonyPatch(typeof(RunManager), "EnterRoomInternal", typeof(AbstractRoom), typeof(bool))]
public static class ErosionEmptyRoomPatch
{
    [HarmonyPrefix]
    private static void Prefix(AbstractRoom room)
    {
        if (RevisitHelper.IsErosionHandled) return;
        RevisitHelper.IsErosionHandled = true;

        var runManager = RunManager.Instance;
        if (runManager == null) return;

        var stateProp = typeof(RunManager).GetProperty("State",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        var state = stateProp?.GetValue(runManager) as IRunState;
        if (state == null) return;

        var coord = state.CurrentMapCoord;
        if (!coord.HasValue) return;

        if (!ErosionHelper.ErosionEmptyCoords.TryGetValue(coord.Value, out _))
            return;

        RevisitHelper.IsErosionEmptyRoom = true;
    }
}

[HarmonyPatch(typeof(RestSiteSynchronizer), "BeginRestSite")]
public static class ClearOptionsOnRevisitPatch
{
    private const BindingFlags _nf = BindingFlags.NonPublic | BindingFlags.Instance;

    [HarmonyPostfix]
    private static void Postfix(RestSiteSynchronizer __instance)
    {
        if (!RevisitHelper.IsRevisit && !RevisitHelper.IsErosionEmptyRoom) return;

        var field = typeof(RestSiteSynchronizer).GetField("_restSites", _nf);
        if (field?.GetValue(__instance) is not IList list) return;

        foreach (var item in list)
        {
            var optField = item.GetType().GetField("options",
                BindingFlags.Public | _nf);
            if (optField?.GetValue(item) is IList opts)
                opts.Clear();
        }
    }
}

[HarmonyPatch(typeof(NRestSiteRoom), "UpdateRestSiteOptions")]
public static class EnableProceedOnRevisitPatch
{
    private const BindingFlags _nf = BindingFlags.NonPublic | BindingFlags.Instance;

    [HarmonyPostfix]
    private static void Postfix(NRestSiteRoom __instance)
    {
        if (!RevisitHelper.IsRevisit && !RevisitHelper.IsErosionEmptyRoom) return;

        var proceedField = typeof(NRestSiteRoom).GetField("_proceedButton", _nf);
        if (proceedField?.GetValue(__instance) is NProceedButton proceed)
        {
            proceed.Enable();
            NMapScreen.Instance?.SetTravelEnabled(true);
            RevisitHelper.IsRevisit = false;
            RevisitHelper.IsErosionEmptyRoom = false;
        }
    }
}

[HarmonyPatch(typeof(CombatRoom), "StartCombat")]
public static class ErosionCombatStrengthPatch
{
    [HarmonyPostfix]
    private static async void Postfix(CombatRoom __instance, Task __result)
    {
        await __result;

        try
        {
            var cs = __instance.CombatState;
            if (cs?.RunState is not IRunState rs) return;
            var c = rs.CurrentMapCoord;
            if (!c.HasValue) return;

            if (!FogOfWarManager.IsPointEroded(c.Value, rs.CurrentActIndex)) return;

            var state = FogOfWarManager.Current;
            int erosionRow = state?.ErosionRow ?? int.MaxValue;
            int depth = erosionRow - c.Value.row;
            int extra = Math.Max(0, depth - 1);
            var config = FogOfWarManager.Config;
            int strAmount = config.ErosionBaseStrength + config.ErosionExtraStrengthPerRow * extra;
            int plateAmount = config.ErosionBasePlating + config.ErosionExtraPlatingPerRow * extra;

            var ctx = new BlockingPlayerChoiceContext();

            foreach (var e in cs.Enemies)
            {
                if (e == null || e.IsDead) continue;

                await PowerCmd.Apply<StrengthPower>(ctx, e, strAmount, e, null);
                Log.Info($"[FogboundPaths] +{strAmount} Strength -> {e.LogName}");

                await PowerCmd.Apply<PlatingPower>(ctx, e, plateAmount, e, null);
                Log.Info($"[FogboundPaths] +{plateAmount} Plating -> {e.LogName}");
            }
        }
        catch (System.Exception ex)
        {
            Log.Error($"[FogboundPaths] Strength fail: {ex.Message}");
        }
    }
}
