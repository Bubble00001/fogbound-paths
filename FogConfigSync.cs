using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Networking.Sidecar;

namespace FogboundPaths;

public static class FogConfigSync
{
    private const string ModuleKey = "FogboundPaths";
    private const string MessageKey = "config";

    private static ulong _opcode;
    private static bool _initialized;
    private static bool _pendingBroadcast;

    private static FogConfig _localConfig = null!;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        _opcode = RitsuLibSidecarOpcodes.For(ModuleKey, MessageKey);

        _localConfig = FogOfWar.FogOfWarManager.Config;

        RitsuLibSidecarBus.RegisterHandler(_opcode, OnConfigReceived);

        RitsuLibFramework.SubscribeLifecycle<RunStartedEvent>(OnRunStarted);
        RitsuLibFramework.SubscribeLifecycle<RunLoadedEvent>(OnRunLoaded);
        RitsuLibSidecarEvents.OnSessionBound(OnSessionBound);

        Log.Info("[FogboundPaths] Config sync initialized (multiplayer host-authority mode)");
    }

    private static void OnConfigReceived(RitsuLibSidecarDispatchContext ctx)
    {
        try
        {
            var json = Encoding.UTF8.GetString(ctx.Payload.Span);
            var config = JsonSerializer.Deserialize<FogConfig>(json);
            if (config != null)
            {
                FogOfWar.FogOfWarManager.Config = config;
                Log.Info($"[FogboundPaths] Applied host config: RevealDepth={config.RevealDepth}, ErosionBuffer={config.ErosionBuffer}, EnableFog={config.EnableFog}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[FogboundPaths] Failed to deserialize host config: {ex.Message}");
        }
    }

    private static bool TryBroadcastConfig()
    {
        var runManager = RunManager.Instance;
        if (runManager?.NetService == null) return false;

        var json = JsonSerializer.Serialize(FogOfWar.FogOfWarManager.Config);
        var bytes = Encoding.UTF8.GetBytes(json);

        bool sent = RitsuLibSidecarHighLevelSend.TrySendAsHostBroadcast(
            runManager,
            _opcode,
            bytes,
            RitsuLibSidecarDeliverySemantics.StableSync);

        if (sent)
            Log.Info("[FogboundPaths] Host config broadcast to all clients");
        else
            Log.Info("[FogboundPaths] Not host or Sidecar not ready, skipping config broadcast");

        return sent;
    }

    private static void OnRunStarted(RunStartedEvent evt)
    {
        ResetForNewRun(evt.IsMultiplayer);
    }

    private static void OnRunLoaded(RunLoadedEvent evt)
    {
        ResetForNewRun(evt.IsMultiplayer);
    }

    private static void ResetForNewRun(bool isMultiplayer)
    {
        _pendingBroadcast = false;

        FogOfWar.FogOfWarManager.ClearAllActs();

        FogOfWar.FogOfWarManager.Config = _localConfig;

        if (isMultiplayer)
        {
            _pendingBroadcast = true;
            TryBroadcastConfig();
        }
    }

    private static void OnSessionBound(SidecarSessionBoundEvent evt)
    {
        if (!_pendingBroadcast) return;
        _pendingBroadcast = false;
        TryBroadcastConfig();
    }
}
