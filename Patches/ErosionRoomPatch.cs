using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
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

// ============================================================================
// Patch 1: 重复进入房间的处理
// ============================================================================

/// <summary>
/// 拦截 EnterMapCoord，处理玩家重复进入已访问坐标的情况。
///
/// 原版逻辑：AddVisitedMapCoord 返回 false → EnterMapCoord 直接返回 Task.CompletedTask
/// → 不进任何房间 → 黑屏卡死。
///
/// 修复：检测到已访问坐标时，手动向 _visitedMapCoords 再添加一次（visitCount 变为 2），
/// 然后反射调用 EnterMapCoordInternal，让下游 EnterMapPointInternal 的 Prefix 处理为
/// RestSite（空房间）。
/// </summary>
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

// ============================================================================
// Patch 2: 侵蚀判定 + 重复访问判定（不再修改 pointType）
// ============================================================================

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

// ============================================================================
// Patch 2.5: 侵蚀非战斗房间 → 动态替换为 RestSite
// ============================================================================

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

// ============================================================================
// Patch 3: 重访时清空火堆选项
// ============================================================================

[HarmonyPatch(typeof(RestSiteSynchronizer), "BeginRestSite")]
public static class ClearOptionsOnRevisitPatch
{
    private const BindingFlags _nf = BindingFlags.NonPublic | BindingFlags.Instance;

    [HarmonyPostfix]
    private static void Postfix(RestSiteSynchronizer __instance)
    {
        if (!RevisitHelper.IsRevisit && !RevisitHelper.IsErosionEmptyRoom) return;

        // 反射获取 _restSites 列表
        var field = typeof(RestSiteSynchronizer).GetField("_restSites", _nf);
        if (field?.GetValue(__instance) is not IList list) return;

        // 遍历每个 PlayerRestSite，清空 options
        foreach (var item in list)
        {
            var optField = item.GetType().GetField("options",
                BindingFlags.Public | _nf);
            if (optField?.GetValue(item) is IList opts)
                opts.Clear();
        }
    }
}

// ============================================================================
// Patch 4: 重访空房间时启用 Proceed 按钮
// ============================================================================

/// <summary>
/// 拦截 NRestSiteRoom.UpdateRestSiteOptions()，在重访空房间时：
/// 1. 启用 Proceed 按钮（否则因 0 选项没人 pick → Proceed 永不启用 → 卡死）
/// 2. 调用 SetTravelEnabled(true)（否则回地图后无法移动）
/// 3. 重置 IsRevisit 标记
/// </summary>
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

// ============================================================================
// Patch 5: 侵蚀战斗——给全敌 +3 力量 + 10 覆甲
// ============================================================================

/// <summary>
/// 拦截 CombatRoom.StartCombat()，在战斗初始化完成后检测当前坐标是否被侵蚀。
/// 如果是，给每个敌方生物同时施加三项增益：
///   - +3 StrengthPower（力量）
///   - +10 PlatingPower（覆甲）
///
/// 使用 async void Postfix + await __result 保证在原方法完成后才施加
/// （此时怪物已生成，Enemies 列表已填充）。
/// </summary>
[HarmonyPatch(typeof(CombatRoom), "StartCombat")]
public static class ErosionCombatStrengthPatch
{
    [HarmonyPostfix]
    private static async void Postfix(CombatRoom __instance, Task __result)
    {
        // 等待原 StartCombat 完成（怪物已生成）
        await __result;

        try
        {
            var cs = __instance.CombatState;
            if (cs?.RunState is not IRunState rs) return;
            var c = rs.CurrentMapCoord;
            if (!c.HasValue) return;

            // 检查当前坐标是否被侵蚀
            if (!FogOfWarManager.IsPointEroded(c.Value, rs.CurrentActIndex)) return;

            var state = FogOfWarManager.Current;
            int erosionRow = state?.ErosionRow ?? int.MaxValue;
            int depth = erosionRow - c.Value.row;
            int extra = Math.Max(0, depth - 1);
            var config = FogOfWarManager.Config;
            int strAmount = config.ErosionBaseStrength + config.ErosionExtraStrengthPerRow * extra;
            int plateAmount = config.ErosionBasePlating + config.ErosionExtraPlatingPerRow * extra;

            var strModel = ModelDb.Power<StrengthPower>();
            var plateModel = ModelDb.Power<PlatingPower>();

            foreach (var e in cs.Enemies)
            {
                if (e == null || e.IsDead) continue;

                if (strModel != null)
                {
                    var p = strModel.ToMutable();
                    await PowerCmd.Apply(p, e, strAmount, e, null);
                    Log.Info($"[FogboundPaths] +{strAmount} Strength -> {e.LogName}");
                }

                if (plateModel != null)
                {
                    var pp = plateModel.ToMutable();
                    await PowerCmd.Apply(pp, e, plateAmount, e, null);
                    Log.Info($"[FogboundPaths] +{plateAmount} Plating -> {e.LogName}");
                }
            }
        }
        catch (System.Exception ex)
        {
            Log.Error($"[FogboundPaths] Strength fail: {ex.Message}");
        }
    }
}
