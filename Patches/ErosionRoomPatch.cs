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
    [HarmonyPrefix]
    private static bool Prefix(RunManager __instance, MapCoord coord, ref Task __result)
    {
        var stateProp = typeof(RunManager).GetProperty("State",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        var state = stateProp?.GetValue(__instance) as RunState;
        if (state == null) return true;

        if (!state.VisitedMapCoords.Contains(coord))
            return true;

        var visitedField = typeof(RunState).GetField("_visitedMapCoords",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (visitedField?.GetValue(state) is List<MapCoord> list)
            list.Add(coord);

        var method = typeof(RunManager).GetMethod("EnterMapCoordInternal",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) return true;

        __result = (Task)method.Invoke(__instance, [coord, null, true])!;
        return false;
    }
}

// ============================================================================
// Patch 2: 侵蚀判定 + 重复访问判定
// ============================================================================

/// <summary>
/// 拦截 EnterMapPointInternal，根据侵蚀和重访状态修改 roomType。
///
/// 优先级：
/// 1. Ancient/Boss → 不处理（系统特殊节点）
/// 2. visitCount > 1（重访）→ 强制 RestSite（空房间），设 IsRevisit 标记
/// 3. 侵蚀但非战斗（Shop/Unknown/Rest）→ 强制 RestSite
/// 4. 侵蚀战斗 → 维持原类型（由 ErosionCombatStrengthPatch 加力量）
/// 5. 其他 → 原样不动
///
/// 注意：使用 CurrentMapCoord（上一个走完的坐标）而非本次参数，
/// 因为 EnterMapPointInternal 的 pointType 就是基于 CurrentMapCoord 计算的。
/// </summary>
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
        if (!FogOfWarManager.IsPointEroded(coord.Value, state.CurrentActIndex))
            return true;

        if (isCombat) return true;

        pointType = MapPointType.RestSite;
        return true;
    }
}

/// <summary>
/// Patch 2 和 Patch 3/4 之间的状态传递标记。
/// </summary>
internal static class RevisitHelper
{
    /// <summary>当前进入的是否为重访空房间</summary>
    public static bool IsRevisit;
}

// ============================================================================
// Patch 3: 重访时清空火堆选项
// ============================================================================

/// <summary>
/// 拦截 RestSiteSynchronizer.BeginRestSite()，在重访空房间时清空 rest 选项列表。
///
/// BeginRestSite 正常执行（保证 _restSites 结构完整，避免 GetOptionsForPlayer 越界崩溃），
/// 然后在此 Postfix 中反射清空所有玩家选项 → 0 个选项 → 空火堆。
/// </summary>
[HarmonyPatch(typeof(RestSiteSynchronizer), "BeginRestSite")]
public static class ClearOptionsOnRevisitPatch
{
    private const BindingFlags _nf = BindingFlags.NonPublic | BindingFlags.Instance;

    [HarmonyPostfix]
    private static void Postfix(RestSiteSynchronizer __instance)
    {
        if (!RevisitHelper.IsRevisit) return;

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
        if (!RevisitHelper.IsRevisit) return;

        var proceedField = typeof(NRestSiteRoom).GetField("_proceedButton", _nf);
        if (proceedField?.GetValue(__instance) is NProceedButton proceed)
        {
            proceed.Enable();
            // 关键：原版 ShowProceedButton() 也调了这行，保证地图可移动
            NMapScreen.Instance?.SetTravelEnabled(true);
            RevisitHelper.IsRevisit = false;
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

            var ctx = new BlockingPlayerChoiceContext();

            foreach (var e in cs.Enemies)
            {
                if (e == null || e.IsDead) continue;

                await PowerCmd.Apply<StrengthPower>(ctx, e, 3m, e, null);
                Log.Info($"[FogboundPaths] +3 Strength -> {e.LogName}");

                await PowerCmd.Apply<PlatingPower>(ctx, e, 10m, e, null);
                Log.Info($"[FogboundPaths] +10 Plating -> {e.LogName}");
            }
        }
        catch (System.Exception ex)
        {
            Log.Error($"[FogboundPaths] Strength fail: {ex.Message}");
        }
    }
}
