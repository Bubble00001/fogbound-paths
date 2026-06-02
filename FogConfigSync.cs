using System.Linq;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Networking.Sidecar;
using FogboundPaths.FogOfWar;
using FogboundPaths.Patches;

namespace FogboundPaths;

public static class FogConfigSync
{
    private const string ModuleKey = "FogboundPaths";
    private const string MessageKey = "config";

    private static ulong _opcode;
    private static bool _initialized;
    private static bool _pendingBroadcast;
    private static int _currentActIndex;

    private static FogConfig _localConfig = null!;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        _opcode = RitsuLibSidecarOpcodes.For(ModuleKey, MessageKey);

        _localConfig = FogOfWarManager.Config;

        RitsuLibSidecarBus.RegisterHandler(_opcode, OnSyncReceived);

        RitsuLibFramework.SubscribeLifecycle<RunStartedEvent>(OnRunStarted);
        RitsuLibFramework.SubscribeLifecycle<RunLoadedEvent>(OnRunLoaded);
        RitsuLibSidecarEvents.OnHandshakeCompleted(OnHandshakeCompleted);

        Log.Info("[FogboundPaths] Config sync initialized (multiplayer host-authority mode)");
    }

    private static void OnSyncReceived(RitsuLibSidecarDispatchContext ctx)
    {
        try
        {
            var json = Encoding.UTF8.GetString(ctx.Payload.Span);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("config", out var configEl))
            {
                var config = JsonSerializer.Deserialize<FogConfig>(configEl.GetRawText());
                if (config != null)
                {
                    FogOfWarManager.Config = config;
                    Log.Info($"[FogboundPaths] Applied host config: RevealDepth={config.RevealDepth}, ErosionBuffer={config.ErosionBuffer}, EnableFog={config.EnableFog}, AllowBacktrack={config.AllowBacktrack}");
                }
            }

            if (root.TryGetProperty("state", out var stateEl) && root.TryGetProperty("actIndex", out var actIdxEl))
            {
                var actIndex = actIdxEl.GetInt32();
                FogOfWarManager.ApplySyncedState(actIndex, stateEl.GetRawText(), null);
                Log.Info($"[FogboundPaths] Applied host fog state for act {actIndex}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[FogboundPaths] Failed to deserialize host sync: {ex.Message}");
        }
    }

    public static void SetCurrentActIndex(int actIndex)
    {
        _currentActIndex = actIndex;
    }

    private static bool TryBroadcastSync()
    {
        var runManager = RunManager.Instance;
        if (runManager?.NetService == null) return false;

        var stateJson = FogOfWarManager.SerializeActState(_currentActIndex);
        var configJson = JsonSerializer.Serialize(FogOfWarManager.Config);
        var payload = $"{{\"config\":{configJson},\"state\":{stateJson},\"actIndex\":{_currentActIndex}}}";
        var bytes = Encoding.UTF8.GetBytes(payload);

        bool sent = RitsuLibSidecarHighLevelSend.TrySendAsHostBroadcast(
            runManager,
            _opcode,
            bytes,
            RitsuLibSidecarDeliverySemantics.StableSync);

        if (sent)
            Log.Info("[FogboundPaths] Host config + state broadcast to all clients");
        else
            Log.Info("[FogboundPaths] Not host or Sidecar not ready, skipping sync broadcast");

        return sent;
    }

    public static void TryBroadcastPendingConfig()
    {
        if (!_pendingBroadcast) return;
        Log.Info("[FogboundPaths] SetMap triggered, attempting config broadcast...");
        if (TryBroadcastSync())
            _pendingBroadcast = false;
    }

    private static void OnRunStarted(RunStartedEvent evt)
    {
        FogOfWarManager.ClearAllActs();
        ResetForNewRun(evt.IsMultiplayer);
    }

    private static void OnRunLoaded(RunLoadedEvent evt)
    {
        EnterMapCoordRevisitPatch.IsLoadingSave = true;
        ResetForNewRun(evt.IsMultiplayer);
    }

    private static void ResetForNewRun(bool isMultiplayer)
    {
        _pendingBroadcast = false;
        FogOfWarManager.Config = _localConfig;
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
        if (TryBroadcastSync())
            _pendingBroadcast = false;
    }
}
