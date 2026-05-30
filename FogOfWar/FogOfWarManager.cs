using System.Collections.Generic;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Logging;

namespace FogboundPaths.FogOfWar;

/// <summary>
/// 迷雾与侵蚀系统的全局管理器（静态单例模式）。
/// 负责：
/// - 维护每个 Act 的 FogOfWarState；
/// - 追踪玩家移动步数并更新侵蚀边界；
/// - 基于图 BFS 计算视野揭示范围；
/// - 对外提供"是否揭示""是否侵蚀"的查询接口。
/// </summary>
public static class FogOfWarManager
{
    /// <summary>所有 Act 的迷雾状态，key = ActIndex</summary>
    private static readonly Dictionary<int, FogOfWarState> _actStates = new Dictionary<int, FogOfWarState>();

    /// <summary>当前 Act 的状态快照</summary>
    private static FogOfWarState? _currentState;

    /// <summary>地图列数（固定 7 列）</summary>
    private const int ColCount = 7;

    /// <summary>由 Entry.cs 在 Init 时注入，供 BFS 使用</summary>
    public static FogConfig Config = new();

    public static FogOfWarState? Current => _currentState;

    // =========================================================================
    // 初始化
    // =========================================================================

    /// <summary>
    /// 为一个新的 Act 初始化迷雾状态。
    /// 支持从存档恢复：传入 visitedCoords 时，根据已走访坐标重建侵蚀进度和视野。
    /// </summary>
    public static void InitializeAct(int actIndex, int mapRowCount, MapCoord startCoord,
        IReadOnlyList<MapCoord>? visitedCoords = null, ActMap? map = null)
    {
        if (_actStates.TryGetValue(actIndex, out var existing))
        {
            _currentState = existing;
            _currentState.MapRowCount = mapRowCount;
            // Always re-run BFS from current position so RevealDepth config
            // takes effect on the initial view (e.g. reopening the map).
            MapCoord pos = existing.CurrentPosition ?? startCoord;
            RevealBfs(existing, pos, map);
            return;
        }

        var state = new FogOfWarState { MapRowCount = mapRowCount };
        _actStates[actIndex] = state;
        _currentState = state;

        state.ErosionRow = -Config.ErosionBuffer - 1;
        state.StepCount = 0;

        if (visitedCoords != null && visitedCoords.Count > 0)
        {
            foreach (var coord in visitedCoords)
                state.VisitedCoords.Add(coord);

            state.CurrentPosition = visitedCoords[visitedCoords.Count - 1];
            state.StepCount = visitedCoords.Count - 1;
            state.ErosionRow = state.StepCount - Config.ErosionBuffer;

            foreach (var coord in visitedCoords)
                RevealBfs(state, coord, map);

            return;
        }

        state.CurrentPosition = startCoord;
        state.VisitedCoords.Add(startCoord);
        RevealBfs(state, startCoord, map);
    }

    // =========================================================================
    // 步数追踪
    // =========================================================================

    /// <summary>
    /// 玩家移动一步时调用。更新侵蚀边界、记录新坐标、揭示新视野。
    /// BFS 揭示先于 dedup 执行，保证多人联机时重复调用也能刷新视野。
    /// </summary>
    public static void OnStepTaken(MapCoord newCoord, int actIndex, ActMap? map = null)
    {
        if (!_actStates.TryGetValue(actIndex, out var state))
            return;

        _currentState = state;

        state.CurrentPosition = newCoord;

        RevealBfs(state, newCoord, map);

        if (state.VisitedCoords.Contains(newCoord))
        {
            if (Config.AllowBacktrack)
            {
                state.StepCount++;
                state.ErosionRow = state.StepCount - Config.ErosionBuffer;
            }
            return;
        }

        state.VisitedCoords.Add(newCoord);
        state.StepCount++;
        state.ErosionRow = state.StepCount - Config.ErosionBuffer;
    }

    /// <summary>
    /// 强制从当前坐标重新执行 BFS 揭示（地图打开时兜底调用）。
    /// 解决多人联机时序不确定导致的概率性节点未揭示问题。
    /// </summary>
    public static void RefreshCurrentReveal(int actIndex, ActMap? map = null)
    {
        if (!_actStates.TryGetValue(actIndex, out var state))
            return;
        if (state.CurrentPosition is not { } pos)
            return;

        _currentState = state;
        RevealBfs(state, pos, map);
    }

    // =========================================================================
    // 查询接口
    // =========================================================================

    /// <summary>查询指定坐标是否已被揭示（已在视野或已走访）。
    /// 如果 Config.EnableFog 为 true，启用迷雾判定；为 false 则全局揭示。</summary>
    public static bool IsPointRevealed(MapCoord coord, int actIndex)
    {
        if (Config.EnableFog)
        {
            if (_actStates.TryGetValue(actIndex, out var state))
                return state.IsRevealed(coord);
            return true;
        }
        return true;
    }

    /// <summary>查询指定坐标是否已被侵蚀覆盖</summary>
    public static bool IsPointEroded(MapCoord coord, int actIndex)
    {
        if (!_actStates.TryGetValue(actIndex, out var state))
            return false;
        return state.IsEroded(coord);
    }

    /// <summary>查询指定坐标是否处于迷雾或侵蚀状态（用于悬停动画拦截）。</summary>
    public static bool IsPointFoggedOrEroded(MapCoord coord, int actIndex)
    {
        return IsPointEroded(coord, actIndex) || !IsPointRevealed(coord, actIndex);
    }

    /// <summary>清除指定 Act 的状态（跨 Act 切换时调用）</summary>
    public static void ClearAct(int actIndex)
    {
        _actStates.Remove(actIndex);
        if (_currentState != null && !_actStates.ContainsValue(_currentState))
            _currentState = null;
    }

    /// <summary>清除所有 Act 的状态（新跑局开始时调用，防止旧局数据泄漏）</summary>
    public static void ClearAllActs()
    {
        _actStates.Clear();
        _currentState = null;
    }

    // =========================================================================
    // 视野揭示：BFS 沿图边行走
    // =========================================================================

    /// <summary>
    /// 从 start 出发，沿 Children 边做 BFS，深度 ≤ Config.RevealDepth 的节点加入 RevealedCoords。
    /// 如果 map 不可用则降级为菱形邻域探测。
    /// </summary>
    private static void RevealBfs(FogOfWarState state, MapCoord start, ActMap? map)
    {
        int maxDepth = Config.RevealDepth;
        var queue = new Queue<(MapCoord coord, int depth)>();
        var visited = new HashSet<MapCoord>();

        queue.Enqueue((start, 0));
        visited.Add(start);

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            state.RevealedCoords.Add(current);

            if (depth >= maxDepth)
                continue;

            MapPoint? point = map?.GetPoint(current);
            if (point == null)
            {
                for (int dr = -1; dr <= 1; dr++)
                {
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int nr = current.row + dr;
                        int nc = current.col + dc;
                        if (nr < 0 || nr >= state.MapRowCount || nc < 0 || nc >= ColCount)
                            continue;
                        var next = new MapCoord(nc, nr);
                        if (!visited.Contains(next))
                        {
                            visited.Add(next);
                            queue.Enqueue((next, depth + 1));
                        }
                    }
                }
                continue;
            }

            foreach (var child in point.Children)
            {
                var next = child.coord;
                if (!visited.Contains(next))
                {
                    visited.Add(next);
                    queue.Enqueue((next, depth + 1));
                }
            }
        }
    }
}
