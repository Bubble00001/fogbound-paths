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
        RitsuLibSidecarEvents.OnHandshakeCompleted(OnHandshakeCompleted);

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
                Log.Info($"[FogboundPaths] Applied host config: RevealDepth={config.RevealDepth}, ErosionBuffer={config.ErosionBuffer}, EnableFog={config.EnableFog}, AllowBacktrack={config.AllowBacktrack}");
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

    public static void TryBroadcastPendingConfig()
    {
        if (!_pendingBroadcast) return;
        Log.Info("[FogboundPaths] SetMap triggered, attempting config broadcast...");
        if (TryBroadcastConfig())
            _pendingBroadcast = false;
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
            Log.Info("[FogboundPaths] Config broadcast deferred, waiting for map open...");
        }
    }

    private static void OnHandshakeCompleted(SidecarHandshakeCompletedEvent evt)
    {
        if (!_pendingBroadcast) return;
        Log.Info($"[FogboundPaths] Sidecar handshake completed for peer {evt.PeerNetId}, attempting config broadcast...");
        if (TryBroadcastConfig())
            _pendingBroadcast = false;
    }
}
