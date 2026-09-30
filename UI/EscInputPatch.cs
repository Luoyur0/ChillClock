// 专注期间的 ESC 拦截 —— 不用全局键盘钩子，改成让游戏的 Input 直接读不到 ESC。
//
// 为什么换成这个做法：
//   以前用过 SetWindowsHookEx（WH_KEYBOARD_LL）拦 Ctrl+Shift+Esc，那是全局钩子，
//   每一次按键都要经过我们的托管代码，结果整个键盘都变粘（用户实测）。
//   Harmony 是在游戏进程内部改方法，游戏调 Input.GetKeyDown(Escape) 时我们直接
//   把结果改成 false —— 按键根本没进过游戏，也没有任何全局副作用。
//
// 注意：这只能挡住**游戏自己**对 ESC 的读取（游戏内的设置 / 结束通话菜单）。
// Ctrl+Shift+Esc＝任务管理器的热键是 Windows 自己（登录会话）处理的，不会经过游戏进程，
// 所以任何进程内的补丁都拦不到它 —— 那件事仍然由 WindowGuard 的"检测到任务管理器就最小化"
// 那套来兜（顺带说一句台词）。
//
// 放在 .UI 命名空间下，才能用 Plugin.PatchMethod("类名", "方法名") 注册。
using HarmonyLib;
using UnityEngine;

namespace ChillFocusWhitelist.UI;

internal static class EscInputPatch
{
    private static bool _logged;

    /// <summary>
    /// 专注期间（且开了"专注时禁止关闭游戏"）让 ESC 读不到：
    /// 结果直接给 false，并跳过原方法。
    /// </summary>
    [HarmonyPrefix]
    private static bool Prefix(KeyCode key, ref bool __result)
    {
        if (key != KeyCode.Escape)
            return true;

        var plugin = Plugin.Instance;
        // 注意：**必须用 ReferenceEquals**，不能用 plugin == null。
        //
        // Plugin 是 UnityEngine.Object，`==` 走的是 Unity 的重载：对象被销毁时它会判成 null。
        // 而游戏在切换显示模式 / 换场景时会重载场景，BepInEx 的插件对象会被一起销毁 ——
        // 从那一刻起 `plugin == null` 就永远是 true，我们把每一次 ESC 都当成"没有插件"放行了。
        // 表现就是用户看到的那样：窗口化之后 ESC 拦不住，重启游戏又好了（还没触发场景重载），
        // 再切换一次显示模式又坏。托管对象本身还在（静态字段持有它），里面的状态也都有效，
        // 所以这里改用真正的引用判空，照样能拦。
        if (ReferenceEquals(plugin, null) || !plugin.ShouldBlockEscape())
        {
            // 按了 ESC 但没拦：把闸门状态写一次日志，免得"没生效"没法查
            LogMissOnce(plugin);
            return true;
        }

        LogOnce("Input.GetKey*(Escape)");
        __result = false;
        return false;
    }

    /// <summary>
    /// 还有一条路要堵：Unity 的 UI（EventSystem / StandaloneInputModule）关面板用的是
    /// <c>Input.GetButtonDown("Cancel")</c>，而 "Cancel" 在键盘上就是 ESC。
    /// 只堵 KeyCode 版的话，用 ESC 打开/关闭游戏里的设置、结束通话那些面板还是能穿过去。
    /// </summary>
    [HarmonyPrefix]
    private static bool PrefixButton(string buttonName, ref bool __result)
    {
        if (string.IsNullOrEmpty(buttonName))
            return true;
        if (!buttonName.Equals("Cancel", System.StringComparison.OrdinalIgnoreCase))
            return true;

        var plugin = Plugin.Instance;
        // 同上：用真正的引用判空，别被 Unity 的"已销毁对象"判空骗了
        if (ReferenceEquals(plugin, null) || !plugin.ShouldBlockEscape())
        {
            LogMissOnce(plugin);
            return true;
        }

        LogOnce("Input.GetButton*(Cancel)");
        __result = false;
        return false;
    }

    /// <summary>第一次真的拦住时写一条日志，方便确认"到底有没有生效"。</summary>
    private static void LogOnce(string what)
    {
        if (_logged)
            return;
        _logged = true;
        Plugin.Log.LogInfo("[Chill Clock] ESC 拦截生效：" + what + " 被挡掉了");
    }

    private static float _nextMissLog;

    /// <summary>
    /// 收到 ESC 但闸门是关的（比如不在专注中）时写日志：
    /// 这样"ESC 没拦住"能立刻分辨是"闸门没开"、"总开关关了"还是"根本没走到我们的补丁"。
    /// 以前只记一次，排查时经常刚好错过；现在改成每 2 秒最多一条。
    /// </summary>
    private static void LogMissOnce(Plugin plugin)
    {
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _nextMissLog)
            return;
        _nextMissLog = now + 2f;
        var alive = !ReferenceEquals(plugin, null);
        Plugin.Log.LogInfo("[Chill Clock] 收到 ESC，但这次放行了：插件实例=" + alive +
                           "（Unity 认为已销毁=" + (plugin == null) + "）" +
                           " 总开关=" + (alive && plugin.IsEscapeBlockConfigured()) +
                           " 结束通话演出中=" + (alive && plugin.IsGameEndingCallTrusted()) +
                           " 最终判据=" + (alive && plugin.ShouldBlockEscape()));
    }
}
