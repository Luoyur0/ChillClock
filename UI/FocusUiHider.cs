using System;
using System.Collections.Generic;
using System.Reflection;
using Bulbul;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChillFocusWhitelist.UI;

/// <summary>
/// 番茄钟会话期间隐藏干扰控件：停止/跳过按钮，
/// 以及专注时主界面右侧的功能按钮列与等级图标。
/// </summary>
internal sealed class FocusUiHider
{
    private const float ReapplyInterval = 0.25f;

    private readonly List<HiddenEntry> _hidden = new List<HiddenEntry>();
    private readonly HashSet<GameObject> _hiddenTargets = new HashSet<GameObject>();

    /// <summary>
    /// 场景快照的复用时长。
    /// Resources.FindObjectsOfTypeAll 是全量扫描（Transform 会把场景里每个对象都返回），
    /// 原先每个按钮名各扫一次、每 0.25 秒重来一轮，等于每秒几十次全场景扫描。
    /// 现在同一轮只扫一次并且缓存这么久；隐藏动作本身仍然每 0.25 秒重放，
    /// 所以游戏把按钮重新打开时照样会被压回去。
    /// </summary>
    private const float SnapshotSeconds = 5f;

    private Transform[] _transformSnapshot;
    private float _transformSnapshotExpire;
    private TMP_Text[] _textSnapshot;
    private float _textSnapshotExpire;
    private PomodoroTimerUI[] _timerUiSnapshot;
    private float _timerUiSnapshotExpire;
    private PomodoroTimerStateView[] _stateViewSnapshot;
    private float _stateViewSnapshotExpire;
    private GameObject _rightIcons;
    private GameObject _topIcons;
    private GameObject _settingEscapeButton;
    private GameObject _exitEscapeButton;
    private LockUI _lockSource;

    /// <summary>我们压住的"锁"（游戏自己的 LockUI）和禁用状态，用来还原。</summary>
    private readonly List<ButtonLock> _locks = new List<ButtonLock>();

    private bool _hideStopSkip;
    private bool _hideUi;
    private bool _hideSessionButtons;
    private bool _lockSessionButtons;
    private bool _hidePauseButtons;
    private bool _stateApplied;
    private float _nextApplyTime;

    public void Tick(
        bool hideStopSkip,
        bool hideUi,
        bool hideSessionButtons,
        bool lockSessionButtons,
        bool hidePauseButtons)
    {
        var changed = hideStopSkip != _hideStopSkip ||
                      hideUi != _hideUi ||
                      hideSessionButtons != _hideSessionButtons ||
                      lockSessionButtons != _lockSessionButtons ||
                      hidePauseButtons != _hidePauseButtons;
        _hideStopSkip = hideStopSkip;
        _hideUi = hideUi;
        _hideSessionButtons = hideSessionButtons;
        _lockSessionButtons = lockSessionButtons;
        _hidePauseButtons = hidePauseButtons;

        var shouldHide = hideStopSkip || hideUi || hideSessionButtons ||
                         lockSessionButtons || hidePauseButtons;
        if (changed)
        {
            RestoreAll();
            _nextApplyTime = 0f;
        }

        if (!shouldHide)
            return;

        if (_stateApplied && Time.realtimeSinceStartup < _nextApplyTime)
            return;
        _nextApplyTime = Time.realtimeSinceStartup + ReapplyInterval;

        try
        {
            if (hideStopSkip)
                HideStopAndSkip();
            if (hideUi)
                HideRightSideUi();
            if (hideSessionButtons)
                HideSessionEscapeButtons();
            if (lockSessionButtons)
                LockSessionEscapeButtons();
            // 番茄钟的播放/暂停按钮：专注和休息期间都要藏起来（和"隐藏UI"开关无关）
            if (hidePauseButtons)
                HideTimerPauseButtons();
            _stateApplied = true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] focus UI hide failed: " + e.Message);
        }
    }

    public void RestoreAll()
    {
        foreach (var entry in _hidden)
        {
            entry.Restore();
            _hiddenTargets.Remove(entry.Target);
        }
        _hidden.Clear();

        RestoreLocks();
        _stateApplied = false;
    }

    private void RestoreLocks()
    {
        foreach (var entry in _locks)
            entry.Restore();
        _locks.Clear();
    }

    /// <summary>
    /// 休息阶段：设置 / 结束通话按钮**显示出来**，但用游戏自己的 LockUI 打个锁 + 禁用。
    ///
    /// 直接藏掉按钮看着像 UI 缺了一块；游戏自己在"按钮暂时不能用"时就是
    /// LockUI.Activate() + Button.interactable = false（见 Bulbul.ExitUI、
    /// DeactivateDecorationButtonUI 的 UpdateLockUIState），这里照抄同一套。
    /// </summary>
    private void LockSessionEscapeButtons()
    {
        LockButtonByName(ref _settingEscapeButton,
            "Paremt/PCPlatform/Canvas/UI/MostFrontArea/RightIcons/IconSetting_Button", "IconSetting_Button");
        LockButtonByName(ref _exitEscapeButton,
            "Paremt/PCPlatform/Canvas/UI/MostFrontArea/RightIcons/IconExit_Button", "IconExit_Button");
    }

    private void LockButtonByName(ref GameObject cache, string path, string fallbackName)
    {
        var target = ResolveCached(ref cache, path, fallbackName);
        if (target == null)
            return;

        for (var i = 0; i < _locks.Count; i++)
        {
            if (_locks[i].BelongsTo(target))
            {
                // 游戏有可能自己把按钮放回去（它随时在改 interactable），每轮补一次
                _locks[i].Reapply();
                return;
            }
        }

        var button = target.GetComponentInChildren<Button>(true);
        var lockUi = target.GetComponentInChildren<LockUI>(true);
        if (lockUi == null)
        {
            if (_lockSource == null)
                _lockSource = FindExitLockUi();

            // 锁挂在 Bulbul.ExitUI 上、但确实属于这个按钮时就照用它（它还会顺带把按钮压暗）
            if (_lockSource != null && _lockSource.transform.IsChildOf(target.transform))
                lockUi = _lockSource;
        }

        if (lockUi != null)
        {
            _locks.Add(new ButtonLock(target, button, lockUi, null));
            return;
        }

        // 这个按钮游戏自己没做锁（比如设置按钮）：借结束通话那把锁的图标，
        // 外形和游戏自带的一致，位置按被锁的按钮居中。
        if (_lockSource == null)
            _lockSource = FindExitLockUi();
        _locks.Add(new ButtonLock(target, button, null, CloneLockVisual(_lockSource, target)));
    }

    /// <summary>
    /// 结束通话按钮的锁不一定挂在按钮底下：Bulbul.ExitUI 自己持有一个 _lockUI。
    /// 找不到按钮内的锁时就去问它。
    /// </summary>
    private static LockUI FindExitLockUi()
    {
        try
        {
            foreach (var behaviour in Resources.FindObjectsOfTypeAll<Bulbul.ExitUI>())
            {
                if (behaviour == null)
                    continue;

                var field = behaviour.GetType().GetField(
                    "_lockUI",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field?.GetValue(behaviour) is LockUI lockUi && lockUi != null)
                    return lockUi;
            }
        }
        catch
        {
            // 找不到就算了，至少按钮还是禁用的
        }

        return null;
    }

    private void HideStopAndSkip()
    {
        PruneDeadTargets();

        HideActiveObjectsByName("PomodoroResetButton");
        HideActiveObjectsByName("SkipButton");
        HideActiveObjectsByName("PomodoroNextButton");
        HidePomodoroButtonsByText();

        var uis = TimerUiSnapshot();

        foreach (var ui in uis)
        {
            if (ui == null)
                continue;

            var hiddenByField = false;
            hiddenByField |= HideReflectedObject(ui, "_resetButton");
            hiddenByField |= HideReflectedObject(ui, "_skipPomodoroButton");
            if (hiddenByField)
                continue;

            HideDescendantByName(ui.transform, "PomodoroResetButton");
            HideDescendantByName(ui.transform, "SkipButton");
            HideDescendantByName(ui.transform, "PomodoroNextButton");
        }

        var stateViews = StateViewSnapshot();

        foreach (var stateView in stateViews)
        {
            if (stateView == null)
                continue;

            HideReflectedObject(stateView, "_skipButtonObject");
            HideDescendantByName(stateView.transform, "PomodoroResetButton");
            HideDescendantByName(stateView.transform, "SkipButton");
            HideDescendantByName(stateView.transform, "PomodoroNextButton");
        }
    }

    private bool HideReflectedObject(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null)
            return false;

        object value;
        try
        {
            value = field.GetValue(instance);
        }
        catch
        {
            return false;
        }

        return HideUnityObject(value);
    }

    private void HideRightSideUi()
    {
        PruneDeadTargets();

        var rightIcons = ResolveCached(ref _rightIcons,
            "Paremt/PCPlatform/Canvas/UI/MostFrontArea/RightIcons", "RightIcons");
        if (rightIcons != null)
            HideTarget(rightIcons);

        // ChillPatcherLite 重排后上半部分按钮位于 TopIcons，与 RightIcons 分开。
        var topIcons = ResolveCached(ref _topIcons,
            "Paremt/PCPlatform/Canvas/UI/MostFrontArea/TopIcons", "TopIcons");
        if (topIcons != null)
            HideTarget(topIcons);

        // LeftIcons / CenterIcons 这两列**不整块收**。
        //
        // 没装 ChillPatcherLite 的 UI 重排时，播放列表按钮就住在 CenterIcons（屏幕左边），
        // 播放条也在附近；整块收掉就把切歌入口一起收了（用户反馈：藏了就没法切歌）。
        // 用户要藏的其实只是那几颗功能图标，所以这两列改成按名字收（上面那份名单），
        // 名单外的（音乐 / 播放列表 / 装饰等）一律留着。
        // RightIcons / TopIcons 整块收是安全的：重排版里图标都在这两个容器里，
        // 而音乐条被挪到 LeftIcons 的父节点下，不在其中。
        LogContainerChildrenOnce();

        var names = new[]
        {
            "UI_FacilityPlayerLevel",
            "FailityLevel",
            "IconNote_Button",
            "IconTodo_Button",
            "IconCalender_Button",
            "IconHabit_Button",
            "IconSetting_Button",
            // LeftIcons / CenterIcons 那两列里的按钮（容器不整块收，所以按名字点名）
            "IconStory_Button",
            "IconSpecial_Button",
            "IconDecoration_Button",
            "IconEnviroment_Button",
            "IconExit_Button"
        };
        foreach (var name in names)
        {
            var target = FindActiveByExactName(name);
            if (target != null)
                HideTarget(target);
        }
    }

    private void HideSessionEscapeButtons()
    {
        PruneDeadTargets();

        var names = new[]
        {
            "IconSetting_Button",
            "IconExit_Button"
        };
        foreach (var name in names)
        {
            var target = FindActiveByExactName(name);
            if (target != null)
                HideTarget(target);
        }
    }

    /// <summary>
    /// 番茄钟 / 正计时的播放暂停按钮：专注和休息期间都藏起来。
    /// （原来挂在 HideSessionEscapeButtons 里，改成"休息时上锁"之后休息阶段就漏出来了。）
    /// </summary>
    private void HideTimerPauseButtons()
    {
        PruneDeadTargets();

        foreach (var name in new[] { "PomodoroPlayOrPauseButton", "CountupPlayOrPauseButton" })
        {
            var target = FindActiveByExactName(name);
            if (target != null)
                HideTarget(target);
        }
    }

    /// <summary>缓存过的 GameObject.Find：命中缓存就不再走层级查找。</summary>
    private GameObject ResolveCached(ref GameObject cache, string path, string fallbackName)
    {
        if (cache != null)
            return cache;

        var found = GameObject.Find(path);
        if (found == null)
            found = FindActiveByExactName(fallbackName);
        cache = found;
        return found;
    }

    private GameObject FindActiveByExactName(string name)
    {
        var all = TransformSnapshot();
        foreach (var transform in all)
        {
            if (transform == null || !transform.gameObject.activeInHierarchy)
                continue;
            if (transform.name == name)
                return transform.gameObject;
        }

        return null;
    }

    private Transform[] TransformSnapshot()
    {
        var now = Time.realtimeSinceStartup;
        if (_transformSnapshot != null && now < _transformSnapshotExpire)
            return _transformSnapshot;

        try
        {
            _transformSnapshot = Resources.FindObjectsOfTypeAll<Transform>();
        }
        catch
        {
            _transformSnapshot = Array.Empty<Transform>();
        }

        _transformSnapshotExpire = now + SnapshotSeconds;
        return _transformSnapshot;
    }

    private TMP_Text[] TextSnapshot()
    {
        var now = Time.realtimeSinceStartup;
        if (_textSnapshot != null && now < _textSnapshotExpire)
            return _textSnapshot;

        try
        {
            _textSnapshot = Resources.FindObjectsOfTypeAll<TMP_Text>();
        }
        catch
        {
            _textSnapshot = Array.Empty<TMP_Text>();
        }

        _textSnapshotExpire = now + SnapshotSeconds;
        return _textSnapshot;
    }

    private PomodoroTimerUI[] TimerUiSnapshot()
    {
        var now = Time.realtimeSinceStartup;
        if (_timerUiSnapshot != null && now < _timerUiSnapshotExpire)
            return _timerUiSnapshot;

        try
        {
            _timerUiSnapshot = Resources.FindObjectsOfTypeAll<PomodoroTimerUI>();
        }
        catch
        {
            _timerUiSnapshot = Array.Empty<PomodoroTimerUI>();
        }

        _timerUiSnapshotExpire = now + SnapshotSeconds;
        return _timerUiSnapshot;
    }

    private PomodoroTimerStateView[] StateViewSnapshot()
    {
        var now = Time.realtimeSinceStartup;
        if (_stateViewSnapshot != null && now < _stateViewSnapshotExpire)
            return _stateViewSnapshot;

        try
        {
            _stateViewSnapshot = Resources.FindObjectsOfTypeAll<PomodoroTimerStateView>();
        }
        catch
        {
            _stateViewSnapshot = Array.Empty<PomodoroTimerStateView>();
        }

        _stateViewSnapshotExpire = now + SnapshotSeconds;
        return _stateViewSnapshot;
    }

    private void HideDescendantByName(Transform root, string name)
    {
        if (root == null)
            return;

        foreach (Transform child in root)
        {
            if (child.name == name)
                HideTarget(child.gameObject);
            HideDescendantByName(child, name);
        }
    }

    private bool HideUnityObject(object value)
    {
        if (value is GameObject gameObject)
        {
            HideTarget(gameObject);
            return true;
        }

        if (value is Component component)
        {
            if (component != null)
                HideTarget(component.gameObject);
            return true;
        }

        return false;
    }

    private void HideTarget(GameObject target)
    {
        if (target == null || !target.activeInHierarchy)
            return;
        // 音乐相关的控件永远不藏：播放列表按钮在某些布局里和要藏的图标挤在同一列，
        // 藏掉就没法切歌了（用户明确要求保留）。
        if (IsMusicControl(target.name))
            return;
        if (!_hiddenTargets.Add(target))
            return;

        var group = target.GetComponent<CanvasGroup>();
        if (group == null)
            group = target.AddComponent<CanvasGroup>();

        _hidden.Add(new HiddenEntry(target, group));
    }

    /// <summary>名字里带音乐 / 播放列表的控件（UI_FacilityMusic、IconMusicPlaylist_Button 等）。</summary>
    private static bool IsMusicControl(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        return name.IndexOf("Music", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("Playlist", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool _loggedContainers;

    /// <summary>
    /// 把四个图标容器的子项名单写一次日志。
    /// "该藏的没藏 / 不该藏的藏了"这类问题只能靠这份名单定位 ——
    /// 没装 UI 重排时各按钮坐在哪一列，跟重排版完全不一样。
    /// </summary>
    private void LogContainerChildrenOnce()
    {
        if (_loggedContainers)
            return;
        _loggedContainers = true;

        var roots = new[]
        {
            new[] { "Paremt/PCPlatform/Canvas/UI/MostFrontArea/RightIcons", "RightIcons" },
            new[] { "Paremt/PCPlatform/Canvas/UI/MostFrontArea/TopIcons", "TopIcons" },
            new[] { "Paremt/PCPlatform/Canvas/UI/MostFrontArea/LeftIcons", "LeftIcons" },
            new[] { "Paremt/PCPlatform/Canvas/UI/MostFrontArea/CenterIcons", "CenterIcons" }
        };

        foreach (var pair in roots)
        {
            try
            {
                var root = GameObject.Find(pair[0]);
                if (root == null)
                    root = FindActiveByExactName(pair[1]);
                if (root == null)
                {
                    Plugin.Log.LogInfo("[Chill Clock] UI 容器 " + pair[1] + "：找不到（这个布局里没有）");
                    continue;
                }

                var names = new List<string>();
                for (var i = 0; i < root.transform.childCount; i++)
                {
                    var child = root.transform.GetChild(i);
                    names.Add(child.name + (child.gameObject.activeSelf ? string.Empty : "(隐藏)"));
                }

                Plugin.Log.LogInfo("[Chill Clock] UI 容器 " + pair[1] +
                                   "（容器本身 active=" + root.activeInHierarchy + "）子项: " +
                                   string.Join(", ", names));
            }
            catch
            {
                // 诊断用，失败无所谓
            }
        }
    }

    private void PruneDeadTargets()
    {
        for (var i = _hidden.Count - 1; i >= 0; i--)
        {
            if (_hidden[i].Target == null)
            {
                _hiddenTargets.Remove(_hidden[i].Target);
                _hidden.RemoveAt(i);
            }
        }
    }

    private void HideActiveObjectsByName(string name)
    {
        var all = TransformSnapshot();
        foreach (var transform in all)
        {
            if (transform == null || !transform.gameObject.activeInHierarchy)
                continue;
            if (transform.name == name)
                HideTarget(transform.gameObject);
        }
    }

    private void HidePomodoroButtonsByText()
    {
        var texts = TextSnapshot();
        foreach (var text in texts)
        {
            if (text == null || !text.gameObject.activeInHierarchy)
                continue;
            if (!IsPomodoroControlText(text.text))
                continue;
            if (!IsUnderPomodoro(text.transform))
                continue;

            var button = text.GetComponentInParent<Button>(true);
            if (button != null && button.gameObject.activeInHierarchy)
                HideTarget(button.gameObject);
            else
                HideTarget(text.gameObject);
        }
    }

    private static bool IsPomodoroControlText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (text.Length == 0)
            return false;

        string[] exact =
        {
            "跳过", "结束", "停止",
            "スキップ", "終了", "停止",
            "Skip", "End", "Stop"
        };
        foreach (var candidate in exact)
        {
            if (string.Equals(text, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return text.IndexOf("结束计时", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("終了する", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("Skip Timer", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsUnderPomodoro(Transform transform)
    {
        var current = transform;
        while (current != null)
        {
            if (current.name.IndexOf("Pomodoro", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            current = current.parent;
        }
        return false;
    }

    /// <summary>
    /// 复制一份游戏自带的锁图标挂到按钮上（给游戏自己没做锁的按钮用）。
    /// 只借它自己的贴图和结构，位置按被锁的按钮居中。
    /// </summary>
    private static GameObject CloneLockVisual(LockUI source, GameObject target)
    {
        if (source == null || target == null)
            return null;

        try
        {
            var field = typeof(LockUI).GetField(
                "_lockImage",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.GetValue(source) is not Image image || image == null)
                return null;

            var clone = Object.Instantiate(image.gameObject, target.transform);
            clone.name = "ChillClockLockVisual";

            var cloneRect = clone.GetComponent<RectTransform>();
            if (cloneRect != null)
            {
                cloneRect.anchorMin = new Vector2(0.5f, 0.5f);
                cloneRect.anchorMax = new Vector2(0.5f, 0.5f);
                cloneRect.pivot = new Vector2(0.5f, 0.5f);
                cloneRect.anchoredPosition = Vector2.zero;
                cloneRect.localScale = Vector3.one;

                var sourceRect = image.rectTransform;
                if (sourceRect != null)
                    cloneRect.sizeDelta = sourceRect.sizeDelta;
            }

            var cloneImage = clone.GetComponent<Image>();
            if (cloneImage != null)
            {
                var color = cloneImage.color;
                cloneImage.color = new Color(color.r, color.g, color.b, 1f);
                cloneImage.raycastTarget = false;
            }

            clone.SetActive(true);
            return clone;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 一个被"锁住"的按钮：亮出游戏自己的 LockUI，同时把按钮设为不可点。
    /// 还原时两样都放回去。
    /// </summary>
    private sealed class ButtonLock
    {
        private readonly GameObject _target;
        private readonly Button _button;
        private readonly bool _originalInteractable;
        private readonly LockUI _lockUi;
        private readonly bool _lockWasActive;
        private readonly GameObject _visual;

        public ButtonLock(GameObject target, Button button, LockUI lockUi, GameObject visual)
        {
            _target = target;
            _button = button;
            if (button != null)
            {
                _originalInteractable = button.interactable;
                button.interactable = false;
            }

            _lockUi = lockUi;
            if (lockUi != null)
            {
                _lockWasActive = lockUi.IsActive;
                if (!_lockWasActive)
                    lockUi.Activate();
            }

            _visual = visual;
        }

        public bool BelongsTo(GameObject target)
        {
            return _target != null && target != null && _target == target;
        }

        public void Reapply()
        {
            if (_button != null && _button.interactable)
                _button.interactable = false;
            if (_lockUi != null && !_lockUi.IsActive)
                _lockUi.Activate();
        }

        public void Restore()
        {
            if (_button != null)
                _button.interactable = _originalInteractable;
            if (_lockUi != null && !_lockWasActive)
                _lockUi.Deactivate();
            if (_visual != null)
                Object.Destroy(_visual);
        }
    }

    private sealed class HiddenEntry
    {
        private readonly float _originalAlpha;
        private readonly bool _originalInteractable;
        private readonly bool _originalBlocksRaycasts;
        private readonly CanvasGroup _group;
        private readonly GameObject _target;

        public HiddenEntry(GameObject target, CanvasGroup group)
        {
            _target = target;
            _group = group;
            _originalAlpha = group.alpha;
            _originalInteractable = group.interactable;
            _originalBlocksRaycasts = group.blocksRaycasts;

            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
        }

        public GameObject Target
        {
            get { return _target; }
        }

        public void Restore()
        {
            if (_target == null || _group == null)
                return;

            _group.alpha = _originalAlpha;
            _group.interactable = _originalInteractable;
            _group.blocksRaycasts = _originalBlocksRaycasts;
        }
    }
}
