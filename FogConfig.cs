using System.Runtime.Serialization;

namespace FogboundPaths;

/// <summary>
/// FogboundPaths configurable parameters.
/// Editable in-game via RitsuLib settings panel, takes effect on next run.
/// </summary>
[DataContract]
public class FogConfig
{
    /// <summary>
    /// Enable fog-of-war (hide unrevealed nodes behind fog).
    /// When disabled, all nodes are directly visible; only erosion remains.
    /// </summary>
    [DataMember] public bool EnableFog { get; set; } = true;

    /// <summary>侵蚀缓冲步数：当前位置之后多少步侵蚀会追上（默认 4，范围 0-20）</summary>
    [DataMember] public int ErosionBuffer { get; set; } = 4;

    /// <summary>
    /// BFS reveal depth: how many steps along graph edges from current position are revealed.
    /// 0 = only current node visible.
    /// </summary>
    [DataMember] public int RevealDepth { get; set; } = 2;
}
