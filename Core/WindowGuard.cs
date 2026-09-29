using System;
using System.Collections.Generic;

namespace ChillFocusWhitelist.Core;

public sealed class WindowGuard
{
    private readonly WhitelistStore _store;
    private readonly HashSet<IntPtr> _minimizedByUs = new HashSet<IntPtr>();
    private readonly object _lock = new object();
    private float _nextSkipLog;

    /// <summary>已请求关闭、还在等它自己消失的任务管理器窗口 -> 放弃等待的时刻。</summary>
    private readonly Dictionary<IntPtr, float> _closeRequested = new Dictionary<IntPtr, float>();

    /// <summary>确认关不掉、只能压住的任务管理器窗口。日志每个窗口只记一次。</summary>
    private readonly HashSet<IntPtr> _closeGaveUp = new HashSet<IntPtr>();

    /// <summary>请它关闭到认定"关不掉"之间的宽限时间（正常情况任务管理器是秒关的）。</summary>
    private const float CloseGraceSeconds = 1.5f;

    public WindowGuard(WhitelistStore store)
    {
        _store = store;
    }

    public Action<string, bool> OnWindowMinimized { get; set; }

    public bool FocusActive { get; private set; }

    public void SetFocusActive(bool active)
    {
        lock (_lock)
        {
            if (active == FocusActive)
                return;

            FocusActive = active;
            if (active)
            {
                Plugin.Log.LogInfo("[Chill Clock] focus minimize started");
                // 开场这一轮收窗口**不出声**：那些应用是开始专注之前就开着的，
                // 游戏自己这时候也正在说"开始工作了"，紧跟着来一句"你又开别的应用了吧"
                // 会显得莫名其妙。专注期间新冒出来的窗口才会提醒（见 Tick）。
                if (!HeroineActionBridge.IsForegroundCritical())
                    SweepMinimizeLocked(false, false);
            }
            else
            {
                EndFocusLocked();
            }
        }
    }

    public void Tick()
    {
        lock (_lock)
        {
            if (!FocusActive)
                return;

            // 只在"开场问候 / 结束通话告别"这类依赖游戏前台焦点的演出里让路。
            // 以前这里用的是宽判据 IsGameSequenceBusy()，把野生动作（伸懒腰、端杯子）、
            // 睡觉、番茄钟动作、点击反应统统算成"忙"，结果她一想事情就不收窗口了 ——
            // 那些和窗口焦点没有任何关系。
            if (HeroineActionBridge.IsForegroundCritical())
            {
                LogSweepSkipped();
                return;
            }

            SweepMinimizeLocked(false, true);
            PruneDeadHandlesLocked();
        }
    }

    public void TickTaskManagerOnly()
    {
        lock (_lock)
        {
            if (HeroineActionBridge.IsForegroundCritical())
                return;

            SweepMinimizeLocked(true, true);
            PruneDeadHandlesLocked();
        }
    }

    public void ReleaseAll()
    {
        lock (_lock)
            EndFocusLocked();
    }

    /// <summary>演出期间跳过收窗口时留一条线索（10 秒最多一条，正常情况下几乎不会出现）。</summary>
    private void LogSweepSkipped()
    {
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _nextSkipLog)
            return;

        _nextSkipLog = now + 10f;
        Plugin.Log.LogInfo("[Chill Clock] 游戏演出中（开场 / 结束通话 / 离席），这一轮先不收窗口");
    }

    /// <summary>已经记过日志的"拿不到路径"的进程名，避免每轮刷屏。</summary>
    private readonly HashSet<string> _loggedUnknownPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 拿不到进程路径时记一条（每个进程名只记一次）。
    /// 这类窗口以前是被静默跳过的，出了问题日志上什么都看不到。
    /// </summary>
    private void NoteUnknownPath(Win32.WindowInfo window)
    {
        if (!string.IsNullOrEmpty(window.ProcessPath))
            return;

        var key = window.ProcessName ?? ("pid:" + window.ProcessId);
        if (!_loggedUnknownPath.Add(key))
            return;

        Plugin.Log.LogInfo("[Chill Clock] 拿不到进程路径（多半是以管理员/反作弊保护运行）：" +
                           key + " [" + window.ClassName + "] " + window.Title + " —— 按进程名处理");
    }

    private void SweepMinimizeLocked(bool taskManagerOnly, bool allowVoice)
    {
        var windows = Win32.EnumerateVisibleTopLevelWindows();
        var hidAny = false;
        foreach (var window in windows)
        {
            if (taskManagerOnly && !window.IsTaskManager)
                continue;
            if (window.IsShellWindow)
                continue;
            if (window.ProcessId == (uint)Win32.CurrentProcessId)
                continue;
            // 拿不到进程路径也照样收：带了反作弊 / 以管理员运行的游戏（比如明日方舟 PC 版）
            // 会让低权限进程的 QueryFullProcessImageName 失败，以前这里直接跳过，
            // 表现就是"开着别的游戏，我们的窗口守护当它不存在"。现在只剩进程名也能处理，
            // 白名单按进程名匹配（WhitelistStore.IsAllowed 支持）。
            if (string.IsNullOrEmpty(window.ProcessPath) && string.IsNullOrEmpty(window.ProcessName) &&
                !window.IsTaskManager)
                continue;
            NoteUnknownPath(window);
            if (Win32.IsMinimized(window.Handle))
                continue;
            if (_store.IsAllowed(window.ProcessPath, window.ProcessName))
                continue;

            if (window.IsTaskManager)
            {
                // 只在"真的动手了"的那一次提醒，别让等待和补压的轮次重复触发语音。
                var speak = HandleTaskManagerLocked(window.Handle);
                if (allowVoice && speak)
                    OnWindowMinimized?.Invoke(window.ProcessName ?? "taskmgr.exe", true);
                continue;
            }

            if (Win32.HideWindow(window.Handle))
            {
                hidAny = true;
                _minimizedByUs.Add(window.Handle);
                if (allowVoice)
                    OnWindowMinimized?.Invoke(window.ProcessName, false);
                Plugin.Log.LogInfo("[Chill Clock] minimized window: " + window.ProcessName);
            }
        }

        // 这里**不**去抢焦点：SetForegroundWindow 在我们不是前台进程时会失败，
        // 而 Windows 会把"有人想让这个窗口到前台"渲染成任务栏按钮闪红光 —— 实测会一直闪。
        // 收窗口本身不该动焦点，游戏自己会处理失焦/回焦。
        _ = hidAny;
    }

    /// <summary>
    /// 任务管理器：先请它自己关（WM_CLOSE），关不掉就退化成最小化，别让它继续挡着。
    ///
    /// "关不掉"有两种：消息根本没送进去（对方是管理员权限，UIPI 拦掉了），
    /// 或者送进去了它不理。前者立刻兜底，后者给它 CloseGraceSeconds 的时间自己走，
    /// 超时再兜底。放弃过的窗口不再重发消息，只负责保持压住。
    /// </summary>
    /// <returns>这一轮是否该提醒用户（只有首次尝试关闭、以及立刻放弃的那次是 true）。</returns>
    private bool HandleTaskManagerLocked(IntPtr handle)
    {
        if (_closeGaveUp.Contains(handle))
        {
            // 用户又把它捞起来了就再压回去（最小化状态下外层扫描会跳过它）
            Win32.HideWindow(handle);
            return false;
        }

        if (_closeRequested.TryGetValue(handle, out var giveUpAt))
        {
            if (UnityEngine.Time.realtimeSinceStartup < giveUpAt)
                return false;

            _closeRequested.Remove(handle);
            GiveUpOnTaskManager(handle, 0);
            return false;
        }

        if (Win32.RequestClose(handle, out var error))
        {
            _closeRequested[handle] = UnityEngine.Time.realtimeSinceStartup + CloseGraceSeconds;
            return true;
        }

        GiveUpOnTaskManager(handle, error);
        return true;
    }

    /// <summary>关不掉的任务管理器：至少最小化。结果记一行日志，方便按用户反馈定位。</summary>
    private void GiveUpOnTaskManager(IntPtr handle, int closeError)
    {
        _closeGaveUp.Add(handle);

        var minimized = Win32.HideWindow(handle);
        if (minimized)
            _minimizedByUs.Add(handle);

        var reason = closeError == 0
            ? "消息送达了但它没关"
            : "WM_CLOSE 发不进去，error=" + closeError + "（多半是以管理员权限运行）";
        Plugin.Log.LogInfo(
            "[Chill Clock] 任务管理器关不掉：" + reason +
            (minimized ? "，已改成最小化" : "，最小化也失败了（权限不够）"));
    }

    private void EndFocusLocked()
    {
        // 只解除管制：保留当前最小化状态，不自动把所有窗口弹回。
        _minimizedByUs.Clear();
        _closeRequested.Clear();
        _closeGaveUp.Clear();
        FocusActive = false;
        Plugin.Log.LogInfo("[Chill Clock] focus minimize ended (windows stay minimized)");
    }

    private void PruneDeadHandlesLocked()
    {
        _minimizedByUs.RemoveWhere(handle => !Win32.IsWindowAlive(handle));
        _closeGaveUp.RemoveWhere(handle => !Win32.IsWindowAlive(handle));

        if (_closeRequested.Count == 0)
            return;

        // Dictionary 没有 RemoveWhere，而这本字典平时是空的，所以就地取一份键快照来清理。
        foreach (var handle in new List<IntPtr>(_closeRequested.Keys))
        {
            if (!Win32.IsWindowAlive(handle))
                _closeRequested.Remove(handle);
        }
    }
}
