using System.Collections.Generic;
using MegaCrit.Sts2.Core.Map;

namespace FogboundPaths.FogOfWar;

/// <summary>
/// 单幕迷雾与侵蚀状态的数据容器。
/// 每个 Act 拥有独立实例，由 FogOfWarManager 管理。
/// </summary>
public class FogOfWarState
{
    /// <summary>已被玩家视野揭示的坐标（不包含已走过的）</summary>
    public HashSet<MapCoord> RevealedCoords { get; } = new HashSet<MapCoord>();

    /// <summary>玩家已踩过的坐标</summary>
    public HashSet<MapCoord> VisitedCoords { get; } = new HashSet<MapCoord>();

    /// <summary>
    /// 侵蚀边界行号。所有 row &lt; ErosionRow 的节点判定为"已被侵蚀"。
    /// 初始值 -3 确保开局没有行被侵蚀。
    /// 每步更新公式：ErosionRow = StepCount - 2（即每次移动侵蚀边界上升一行，玩家有 2 步缓冲）。
    /// </summary>
    public int ErosionRow { get; set; } = -3;

    /// <summary>当前 Act 中已行走的步数</summary>
    public int StepCount { get; set; }

    /// <summary>当前玩家所在坐标</summary>
    public MapCoord? CurrentPosition { get; set; }

    /// <summary>当前地图的总行数</summary>
    public int MapRowCount { get; set; }

    /// <summary>
    /// 判定某坐标是否已被玩家探开（已走访或在视野范围内）。
    /// </summary>
    public bool IsRevealed(MapCoord coord)
    {
        return RevealedCoords.Contains(coord) || VisitedCoords.Contains(coord);
    }

    /// <summary>
    /// 判定某坐标是否已被侵蚀覆盖。
    /// </summary>
    public bool IsEroded(MapCoord coord)
    {
        return coord.row < ErosionRow;
    }
}
