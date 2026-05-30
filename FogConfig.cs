using System.Runtime.Serialization;

namespace FogboundPaths;

[DataContract]
public class FogConfig
{
    [DataMember] public bool EnableFog { get; set; } = true;

    [DataMember] public int ErosionBuffer { get; set; } = 4;

    [DataMember] public int RevealDepth { get; set; } = 2;

    [DataMember] public bool AllowBacktrack { get; set; } = false;
}
