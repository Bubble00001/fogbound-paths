using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;

namespace FogboundPaths.Patches;

// ============================================================================
// Patch 1: 每阶段房间数 -1
// ============================================================================

/// <summary>
/// 拦截 ActModel.GetNumberOfRooms()，将返回值减 1。
/// 效果：每个阶段（Act）的房间层数比原版少一层，玩家推进更快，侵蚀压力更大。
/// </summary>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.GetNumberOfRooms))]
public static class ReduceRoomCountPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref int __result)
    {
        __result--;
    }
}

// ============================================================================
// Patch 2: 地图生成后处理 —— 横向连接 + 宝箱行改造 + Unassigned 节点清理
// ============================================================================

/// <summary>
/// 在 StandardActMap 构造完成后立即执行：
/// 1. 添加同行双向连接（左↔右）；
/// 2. 添加反向纵向连接（下→上），让原版单向路径可逆行；
/// 3. 重建宝箱行：最左和最右节点设为宝箱，中间节点用种子随机为 Monster/Unknown/Elite；
/// 4. 兜底清理所有 Unassigned 节点（避免进入时崩溃）。
/// </summary>
[HarmonyPatch(typeof(StandardActMap), MethodType.Constructor,
    typeof(Rng),
    typeof(ActModel),
    typeof(bool),
    typeof(bool),
    typeof(bool),
    typeof(MapPointTypeCounts),
    typeof(bool))]
public static class StandardActMapPostProcessPatch
{
    // 反射获取 StandardActMap 的私有属性 Grid
    private static readonly PropertyInfo? _gridProperty =
        typeof(StandardActMap).GetProperty("Grid", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// 捕获地图生成使用的 Rng 实例（基于种子），用于后续宝箱行随机化。
    /// 必须在 Prefix 中捕获，因为 Postfix 中无法直接获取构造函数参数。
    /// </summary>
    [HarmonyPrefix]
    private static void Prefix(StandardActMap __instance, Rng mapRng)
    {
        _mapRng = mapRng;
    }

    private static Rng? _mapRng;

    [HarmonyPostfix]
    private static void Postfix(StandardActMap __instance)
    {
        if (_gridProperty?.GetValue(__instance) is not MapPoint[,] grid) return;

        int colCount = grid.GetLength(0);
        int rowCount = grid.GetLength(1);

        // 顺序依次执行四个后处理步骤
        AddBidirectionalRowConnections(grid, colCount, rowCount, __instance);
        AddReverseVerticalConnections(grid, colCount, rowCount, __instance);
        RestructureTreasureRow(grid, colCount, __instance);
        CleanupUnassignedNodes(grid, colCount, rowCount);
    }

    // -------------------------------------------------------------------------
    // 2a. 同行双向连接
    // -------------------------------------------------------------------------

    /// <summary>
    /// 为每一行内的相邻节点添加双向连接（左→右 且 右→左）。
    /// 跳过 Boss 前的火堆行（row = bossRestRow），避免火堆横向可走。
    /// 
    /// 设计意图：玩家在同层内可以左右自由探索，但走过的路不可回头（由 NoBacktrackPatch 保证）。
    /// </summary>
    private static void AddBidirectionalRowConnections(MapPoint?[,] grid, int colCount, int rowCount, ActMap map)
    {
        // Boss 前一行是火堆行，不添加横向连接
        int bossRestRow = map.GetRowCount() - 1;

        // 从 row 1 开始（row 0 是起始点）
        for (int r = 1; r < bossRestRow; r++)
        {
            // 收集该行所有非空节点
            List<MapPoint> nodesInRow = new List<MapPoint>();
            for (int c = 0; c < colCount; c++)
            {
                MapPoint? node = grid[c, r];
                if (node != null) nodesInRow.Add(node);
            }

            // 按列号排序，确保连接是相邻节点之间
            var sorted = nodesInRow.OrderBy(n => n.coord.col).ToList();

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var left = sorted[i];
                var right = sorted[i + 1];
                // 仅在不重复时添加
                AddChildIfAbsent(left, right);
                AddChildIfAbsent(right, left);
            }
        }
    }

    // -------------------------------------------------------------------------
    // 2b. 反向纵向连接（下→上）
    // -------------------------------------------------------------------------

    /// <summary>
    /// 为原版的纵向路径（上→下）添加反向连接（下→上）。
    ///
    /// 原版地图中，节点只有从 row N 指向 row N+1 的 Children。
    /// 本方法遍历所有节点，找到其子节点中位于 row+1 的，
    /// 再从该子节点反向添加回父节点的连接。
    ///
    /// 跳过第 0 行（起始行）和 bossRestRow（火堆行到 Boss 单向）。
    /// </summary>
    private static void AddReverseVerticalConnections(MapPoint?[,] grid, int colCount, int rowCount, ActMap map)
    {
        int bossRestRow = map.GetRowCount() - 1;

        for (int r = 1; r < bossRestRow; r++)
        {
            for (int c = 0; c < colCount; c++)
            {
                MapPoint? node = grid[c, r];
                if (node == null) continue;

                // 遍历当前节点的所有子节点
                foreach (var child in node.Children.ToArray())
                {
                    // 只处理向下一行的原始纵向连接（跳过横向连接的同行节点）
                    if (child.coord.row != r + 1) continue;

                    // 从子节点反向添加回父节点的路径
                    AddChildIfAbsent(child, node);
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // 2c. 宝箱行重构
    // -------------------------------------------------------------------------

    /// <summary>
    /// 宝箱行（row = GetRowCount() - 7）的特殊处理：
    /// - 扫描找出该行实际有节点的最左和最右列；
    /// - 最左和最右节点强制设为 Treause（两个宝箱）；
    /// - 中间所有节点用游戏种子 RNG 随机分配 Monster|Unknown|Elite。
    ///
    /// 注意：只有 map.RowCount > 8 的 Act 才有宝箱行（部分 Act 层数不够则跳过）。
    /// </summary>
    private static void RestructureTreasureRow(MapPoint?[,] grid, int colCount, ActMap map)
    {
        int treasureRow = map.GetRowCount() - 7;
        // 层数不够的 Act 没有宝箱行
        if (treasureRow <= 1 || _mapRng == null) return;

        // 第一步：找出该行有节点的最小列和最大列
        int? minCol = null;
        int? maxCol = null;
        for (int c = 0; c < colCount; c++)
        {
            if (grid[c, treasureRow] != null)
            {
                if (minCol == null || c < minCol) minCol = c;
                if (maxCol == null || c > maxCol) maxCol = c;
            }
        }

        if (minCol == null || maxCol == null) return;

        // 第二步：设置节点类型
        for (int c = 0; c < colCount; c++)
        {
            MapPoint? node = grid[c, treasureRow];
            if (node == null) continue;
            node.CanBeModified = true;

            // 最左和最右 → 宝箱
            if (c == minCol.Value || c == maxCol.Value)
            {
                node.PointType = MapPointType.Treasure;
                continue;
            }

            // 中间节点 → 用游戏 RNG 随机分配（保证相同种子相同结果）
            node.PointType = RollRandomType(_mapRng);
        }
    }

    /// <summary>
    /// 使用游戏内置 RNG（基于地图种子）随机生成节点类型。
    /// 概率分布：50% Monster | 30% Unknown | 20% Elite
    /// </summary>
    private static MapPointType RollRandomType(Rng rng)
    {
        float roll = rng.NextFloat();
        if (roll < 0.50f) return MapPointType.Monster;
        if (roll < 0.80f) return MapPointType.Unknown;
        return MapPointType.Elite;
    }

    // -------------------------------------------------------------------------
    // 2d. Unassigned 节点兜底清理
    // -------------------------------------------------------------------------

    /// <summary>
    /// 遍历全图所有节点，将残留的 Unassigned 类型强制改为 Monster。
    /// 这是安全网——正常情况下不会出现 Unassigned，但如有遗漏则避免游戏崩溃。
    /// </summary>
    private static void CleanupUnassignedNodes(MapPoint?[,] grid, int colCount, int rowCount)
    {
        for (int c = 0; c < colCount; c++)
        {
            for (int r = 0; r < rowCount; r++)
            {
                MapPoint? node = grid[c, r];
                if (node != null && node.PointType == MapPointType.Unassigned)
                {
                    node.PointType = MapPointType.Monster;
                    node.CanBeModified = true;
                }
            }
        }
    }

    /// <summary>
    /// 仅当 parent.Children 中不包含 target 时，才添加 target 为子节点。
    /// 避免重复添加边导致地图连接冗余。
    /// </summary>
    private static void AddChildIfAbsent(MapPoint parent, MapPoint target)
    {
        if (!parent.Children.Contains(target))
            parent.AddChildPoint(target);
    }
}
