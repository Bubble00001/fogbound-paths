using Godot.Bridge;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using STS2RitsuLib;
using STS2RitsuLib.Data;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils;

namespace FogboundPaths;

/// <summary>
/// 「雾锁横途」Mod 入口点。
/// 注册 Harmony 补丁系统并通知游戏加载本程序集中的脚本。
/// </summary>
[ModInitializer(nameof(Init))]
public class Entry
{
    private static readonly Lazy<I18N> _i18n = new(() => new I18N(
        "FogboundPaths",
        resourceFolders: ["FogboundPaths.Localization"],
        resourceAssembly: typeof(Entry).Assembly));

    public static void Init()
    {
        var harmony = new Harmony("sts2.spinaria.fogboundpaths");
        harmony.PatchAll();
        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);
        RegisterConfig();
        FogConfigSync.Initialize();
        Log.Info("[FogboundPaths] Mod initialized!");
    }

    private static ModSettingsText T(string key, string fallback)
    {
        return ModSettingsText.I18N(_i18n.Value, key, fallback);
    }

    private static void RegisterConfig()
    {
        var store = ModDataStore.For("FogboundPaths");
        store.Register<FogConfig>("config", "fog_config.json",
            global::STS2RitsuLib.Utils.Persistence.SaveScope.Global,
            () => new FogConfig());

        var saved = store.Get<FogConfig>("config");
        if (saved != null)
            FogOfWar.FogOfWarManager.Config = saved;

        RitsuLibFramework.RegisterModSettings("FogboundPaths", page => page
            .WithModSidebarOrder(100)
            .WithTitle(T("ritsulib.page.title", "Fogbound Paths"))
            .WithModDisplayName(T("ritsulib.page.title", "Fogbound Paths"))
            .AddSection("gameplay", section => section
                .WithTitle(T("fogboundpaths.section.gameplay.title", "Gameplay"))
                .AddToggle("enableFog",
                    T("fogboundpaths.enableFog.label", "Enable Fog"),
                    new ModSettingsValueBinding<FogConfig, bool>(
                        "FogboundPaths", "config",
                        global::STS2RitsuLib.Utils.Persistence.SaveScope.Global,
                        c => c.EnableFog,
                        (c, v) => c.EnableFog = v),
                    description: T("fogboundpaths.enableFog.description",
                        "When disabled, all nodes are directly visible."))
                .AddIntSlider("erosionBuffer",
                    T("fogboundpaths.erosionBuffer.label", "Erosion Buffer"),
                    new ModSettingsValueBinding<FogConfig, int>(
                        "FogboundPaths", "config",
                        global::STS2RitsuLib.Utils.Persistence.SaveScope.Global,
                        c => c.ErosionBuffer,
                        (c, v) => c.ErosionBuffer = v),
                    minValue: 0, maxValue: 20,
                    description: T("fogboundpaths.erosionBuffer.description",
                        "How many steps between current position and erosion boundary."))
                .AddIntSlider("revealDepth",
                    T("fogboundpaths.revealDepth.label", "Reveal Depth"),
                    new ModSettingsValueBinding<FogConfig, int>(
                        "FogboundPaths", "config",
                        global::STS2RitsuLib.Utils.Persistence.SaveScope.Global,
                        c => c.RevealDepth,
                        (c, v) => c.RevealDepth = v),
                    minValue: 0, maxValue: 20,
                    description: T("fogboundpaths.revealDepth.description",
                        "How many steps from current position are revealed."))
                .AddToggle("allowBacktrack",
                    T("fogboundpaths.allowBacktrack.label", "Allow Backtrack"),
                    new ModSettingsValueBinding<FogConfig, bool>(
                        "FogboundPaths", "config",
                        global::STS2RitsuLib.Utils.Persistence.SaveScope.Global,
                        c => c.AllowBacktrack,
                        (c, v) => c.AllowBacktrack = v),
                    description: T("fogboundpaths.allowBacktrack.description",
                        "When enabled, visited nodes can be entered again."))));
    }
}
