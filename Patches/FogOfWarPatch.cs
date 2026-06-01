using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using FogboundPaths.FogOfWar;

namespace FogboundPaths.Patches;

// ============================================================================
// Patch 1: 地图打开时初始化迷雾
// ============================================================================

/// <summary>
/// 在 NMapScreen.SetMap() 执行前（Prefix）初始化迷雾系统。
/// 使用 Prefix 而非 Postfix 是为了确保迷雾状态在 SetMap 主体渲染各节点之前就绪。
/// </summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
public static class SetMapFogInitPatch
{
    private static readonly FieldInfo? _runStateField =
        typeof(NMapScreen).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance);

    [HarmonyPrefix]
    private static void Prefix(NMapScreen __instance, ActMap map)
    {
        if (_runStateField?.GetValue(__instance) is not RunState runState) return;

        FogOfWarManager.InitializeAct(
            runState.CurrentActIndex,
            map.GetRowCount(),
            map.StartingMapPoint.coord,
            runState.VisitedMapCoords,
            map);

        FogConfigSync.SetCurrentActIndex(runState.CurrentActIndex);
        FogConfigSync.TryBroadcastPendingConfig();
    }
}

// ============================================================================
// Patch 2: 移动时追踪步数
// ============================================================================

/// <summary>
/// 在 NMapScreen.TravelToMapCoord() 执行前（Prefix）通知迷雾系统记录一步移动。
/// </summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.TravelToMapCoord))]
public static class TravelStepTrackingPatch
{
    private static readonly FieldInfo? _mapField =
        typeof(NMapScreen).GetField("_map", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? _runStateField =
        typeof(NMapScreen).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance);

    [HarmonyPrefix]
    private static void Prefix(MapCoord coord, NMapScreen __instance)
    {
        if (_runStateField?.GetValue(__instance) is not IRunState runState) return;
        var map = _mapField?.GetValue(__instance) as ActMap;
        // 通知迷雾管理器：玩家走了一步到 coord，更新侵蚀边界和视野
        FogOfWarManager.OnStepTaken(coord, runState.CurrentActIndex, map);
    }
}

// ============================================================================
// Patch 3: 迷雾节点图标替换
// ============================================================================

/// <summary>
/// 在 NNormalMapPoint.UpdateIcon() 执行后（Postfix），
/// 将未揭示节点的图标和边框替换为 map_unknown（问号图标）。
/// </summary>
[HarmonyPatch(typeof(NNormalMapPoint), "UpdateIcon")]
public static class FogIconPatch
{
    private static readonly FieldInfo? _iconField =
        typeof(NNormalMapPoint).GetField("_icon", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? _outlineField =
        typeof(NNormalMapPoint).GetField("_outline", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? _runStateField =
        typeof(NMapPoint).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance);

    // 迷雾节点使用的图标路径（复用原版 Unknown 图标）
    private const string _fogIconPath =
        "res://images/atlases/ui_atlas.sprites/map/icons/map_unknown.tres";

    // 迷雾节点使用的边框路径
    private const string _fogOutlinePath =
        "res://images/atlases/compressed.sprites/map/map_unknown_outline.tres";

    [HarmonyPostfix]
    private static void Postfix(NNormalMapPoint __instance)
    {
        if (_runStateField?.GetValue(__instance) is not IRunState runState) return;
        var coord = __instance.Point?.coord;
        if (!coord.HasValue) return;

        // 未揭示节点 → 替换为问号图标和边框
        if (!FogOfWarManager.IsPointRevealed(coord.Value, runState.CurrentActIndex))
        {
            if (_iconField?.GetValue(__instance) is TextureRect icon)
            {
                icon.Texture = ResourceLoader.Load<Texture2D>(_fogIconPath, null, ResourceLoader.CacheMode.Reuse);
            }
            if (_outlineField?.GetValue(__instance) is TextureRect outline)
            {
                outline.Texture = ResourceLoader.Load<Texture2D>(_fogOutlinePath, null, ResourceLoader.CacheMode.Reuse);
            }
        }
    }
}

// ============================================================================
// Patch 4: 迷雾 / 侵蚀节点颜色覆盖
// ============================================================================

/// <summary>
/// 在 NNormalMapPoint.RefreshColorInstantly() 执行后（Postfix），
/// 根据迷雾/侵蚀状态覆盖节点颜色：
///   - 侵蚀节点 → 红色 (0.55, 0.15, 0.12)
///   - 迷雾节点 → 灰色 (0.35, 0.35, 0.38)
/// </summary>
[HarmonyPatch(typeof(NNormalMapPoint), "RefreshColorInstantly")]
public static class FogColorPatch
{
    private static readonly FieldInfo? _iconField =
        typeof(NNormalMapPoint).GetField("_icon", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? _runStateField =
        typeof(NMapPoint).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance);

    // 迷雾遮罩颜色：灰蓝色调，半透明压低视觉
    private static readonly Color _fogColor = new Color(0.35f, 0.35f, 0.38f, 1f);
    // 侵蚀遮罩颜色：暗红色调，警示"已无法访问"
    private static readonly Color _erosionColor = new Color(0.55f, 0.15f, 0.12f, 1f);

    [HarmonyPostfix]
    private static void Postfix(NNormalMapPoint __instance)
    {
        if (_runStateField?.GetValue(__instance) is not IRunState runState) return;
        var coord = __instance.Point?.coord;
        if (!coord.HasValue) return;

        if (_iconField?.GetValue(__instance) is not TextureRect icon) return;

        // 侵蚀优先于迷雾：已侵蚀节点即使已被揭示也要覆盖为红色
        if (FogOfWarManager.IsPointEroded(coord.Value, runState.CurrentActIndex))
            icon.SelfModulate = _erosionColor;
        else if (!FogOfWarManager.IsPointRevealed(coord.Value, runState.CurrentActIndex))
            icon.SelfModulate = _fogColor;
    }
}

// ============================================================================
// Patch 5: 走过的不再走
// ============================================================================

/// <summary>
/// 在 NMapScreen.RecalculateTravelability() 执行后（Postfix），
/// 将所有已走访但被误设为 Travelable 的节点强制改回 Traveled（不可再点击）。
///
/// 之前只锁同行，现在扩展为所有行：因为反向纵向连接的存在，
/// 下方或上方未访问节点可通过原版 Children 正确标记 Travelable，
/// 但反向边会把已走访节点也拉进 Children → Travelable → 被本 Patch 修正。
/// </summary>
[HarmonyPatch(typeof(NMapScreen), "RecalculateTravelability")]
public static class NoBacktrackPatch
{
    private static readonly FieldInfo? _runStateField =
        typeof(NMapScreen).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? _dictField =
        typeof(NMapScreen).GetField("_mapPointDictionary", BindingFlags.NonPublic | BindingFlags.Instance);

    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance)
    {
        if (FogOfWarManager.Config.AllowBacktrack) return;

        if (_runStateField?.GetValue(__instance) is not RunState runState) return;
        if (_dictField?.GetValue(__instance) is not
            Dictionary<MapCoord, NMapPoint> dict) return;

        var visited = runState.VisitedMapCoords;
        if (visited.Count == 0) return;

        foreach (var vc in visited)
        {
            if (dict.TryGetValue(vc, out var node) && node.State == MapPointState.Travelable)
                node.State = MapPointState.Traveled;
        }
    }
}

// ============================================================================
// Patch 6: 地图打开时重算可走性 + 刷新颜色和视野
// ============================================================================

/// <summary>
/// 在 NMapScreen.Open() 执行后（Postfix），强制重算可走性、兜底刷新 BFS 视野、刷新颜色。
/// 这是刷新视野最可靠的时机——Open 代表地图界面真正显示，所有节点和坐标数据已就绪。
/// 额外调用 RefreshCurrentReveal 解决多人联机时序不确定导致的概率性节点未揭示问题。
/// </summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
public static class MapOpenRecalcPatch
{
    private static readonly FieldInfo? _runStateField =
        typeof(NMapScreen).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? _mapField =
        typeof(NMapScreen).GetField("_map", BindingFlags.NonPublic | BindingFlags.Instance);

    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance)
    {
        if (_runStateField?.GetValue(__instance) is not IRunState runState) return;
        var map = _mapField?.GetValue(__instance) as ActMap;

        // 地图打开时强制从最新位置重新 BFS，兜底覆盖多人联机时序差异
        FogOfWarManager.RefreshCurrentReveal(runState.CurrentActIndex, map);

        typeof(NMapScreen).GetMethod("RecalculateTravelability",
            BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(__instance, null);
        __instance.RefreshAllPointVisuals();
    }
}

// ============================================================================
// Patch 7: 迷雾/侵蚀节点悬停动画 —— 只缩放、不变色
// ============================================================================

/// <summary>
/// 拦截迷雾和侵蚀节点的 AnimHover()，跳过原版的颜色 Tween（会把灰色/红色拉到高亮白），
/// 只执行 scale 缩放动画的替代版。
/// </summary>
[HarmonyPatch(typeof(NNormalMapPoint), "AnimHover")]
public static class FogAnimHoverPatch
{
    private static readonly FieldInfo? _iconField =
        typeof(NNormalMapPoint).GetField("_icon", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? _questIconField =
        typeof(NNormalMapPoint).GetField("_questIcon", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? _runStateField =
        typeof(NMapPoint).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly PropertyInfo? _hoverScaleProp =
        typeof(NNormalMapPoint).GetProperty("HoverScale", BindingFlags.NonPublic | BindingFlags.Instance);

    private static bool Prefix(NNormalMapPoint __instance)
    {
        if (_runStateField?.GetValue(__instance) is not IRunState runState) return true;
        var coord = __instance.Point?.coord;
        if (!coord.HasValue) return true;

        // 只对迷雾/侵蚀节点做拦截
        if (!FogOfWarManager.IsPointFoggedOrEroded(coord.Value, runState.CurrentActIndex))
            return true;

        // 仅执行缩放动画，不触碰颜色
        var hoverScale = _hoverScaleProp?.GetValue(__instance) as Vector2? ?? Vector2.One * 1.45f;
        var icon = _iconField?.GetValue(__instance) as TextureRect;
        var questIcon = _questIconField?.GetValue(__instance) as TextureRect;

        var tween = __instance.CreateTween().SetParallel();
        if (icon != null)
            tween.TweenProperty(icon, "scale", hoverScale, 0.05);
        if (questIcon != null)
            tween.TweenProperty(questIcon, "scale", hoverScale, 0.05);

        // 跳过原始方法
        return false;
    }
}

// ============================================================================
// Patch 8: 迷雾/侵蚀节点取消悬停动画 —— 只缩放、不变色
// ============================================================================

/// <summary>
/// 拦截迷雾和侵蚀节点的 AnimUnhover()，跳过原版的颜色 Tween，
/// 只执行 scale 归位动画的替代版。
/// </summary>
[HarmonyPatch(typeof(NNormalMapPoint), "AnimUnhover")]
public static class FogAnimUnhoverPatch
{
    private static readonly FieldInfo? _iconField =
        typeof(NNormalMapPoint).GetField("_icon", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? _questIconField =
        typeof(NNormalMapPoint).GetField("_questIcon", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? _runStateField =
        typeof(NMapPoint).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance);

    private static bool Prefix(NNormalMapPoint __instance)
    {
        if (_runStateField?.GetValue(__instance) is not IRunState runState) return true;
        var coord = __instance.Point?.coord;
        if (!coord.HasValue) return true;

        if (!FogOfWarManager.IsPointFoggedOrEroded(coord.Value, runState.CurrentActIndex))
            return true;

        // 仅执行缩放归位动画
        var icon = _iconField?.GetValue(__instance) as TextureRect;
        var questIcon = _questIconField?.GetValue(__instance) as TextureRect;

        var tween = __instance.CreateTween().SetParallel();
        if (icon != null)
            tween.TweenProperty(icon, "scale", Vector2.One, 0.5)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        if (questIcon != null)
            tween.TweenProperty(questIcon, "scale", Vector2.One, 0.5)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);

        return false;
    }
}

// ============================================================================
// Patch 9: RefreshVisualsInstantly 后兜底覆盖颜色
// ============================================================================

/// <summary>
/// 在 NMapPoint.RefreshVisualsInstantly() 执行后（Postfix），
/// 再次根据迷雾/侵蚀状态覆盖节点颜色。
/// 这是额外的安全网——RefreshVisualsInstantly 内部流程复杂，
/// 可能在多个步骤后覆盖掉我们的颜色，这个 Postfix 做最终修正。
/// </summary>
[HarmonyPatch(typeof(NMapPoint), "RefreshVisualsInstantly")]
public static class FogVisualsRefreshPatch
{
    private static readonly FieldInfo? _iconField =
        typeof(NNormalMapPoint).GetField("_icon", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? _runStateField =
        typeof(NMapPoint).GetField("_runState", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly Color _fogColor = new Color(0.35f, 0.35f, 0.38f, 1f);
    private static readonly Color _erosionColor = new Color(0.55f, 0.15f, 0.12f, 1f);

    [HarmonyPostfix]
    private static void Postfix(NMapPoint __instance)
    {
        // 只处理 NNormalMapPoint（普通地图节点），跳过 Boss/Ancient 等特殊节点
        if (__instance is not NNormalMapPoint normalPoint) return;
        if (_runStateField?.GetValue(__instance) is not IRunState runState) return;
        var coord = __instance.Point?.coord;
        if (!coord.HasValue) return;

        if (_iconField?.GetValue(normalPoint) is not TextureRect icon) return;

        // 侵蚀 → 红色，迷雾 → 灰色
        if (FogOfWarManager.IsPointEroded(coord.Value, runState.CurrentActIndex))
            icon.SelfModulate = _erosionColor;
        else if (!FogOfWarManager.IsPointRevealed(coord.Value, runState.CurrentActIndex))
            icon.SelfModulate = _fogColor;
    }
}
