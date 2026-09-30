using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using ChillFocusWhitelist.UI;
using UnityEngine;
using UnityEngine.Networking;

namespace ChillFocusWhitelist.Core;

/// <summary>一次语音提醒的启动结果。</summary>
internal enum VoiceStartResult
{
    /// <summary>已经开播（含连播）。</summary>
    Started,

    /// <summary>现在不方便播（游戏在说话 / 上一句还没完），保留待播标记稍后重试。</summary>
    Deferred,

    /// <summary>没得播（冷却中 / 候选池为空 / 已经在播），这条提醒可以丢掉。</summary>
    Skipped
}

/// <summary>
/// 聪音的语音提醒。
///
/// 语音**只从 DLL 内嵌的 Voices.pack 里读**：发布形态就是单独一个 ChillClock.dll，
/// 用户那边不会有额外的语音文件夹，所以不需要外置回退。
/// （以前还找过 plugins\ChillClock\Voices.pack 和 plugins\ChillClock\Voices，
///   结果是"删掉那个文件夹就没声音"，已经去掉了。）
///
/// 兜底：DLL 里还嵌着最早那几十条 WAV，包读不到时才用得上。
/// </summary>
internal sealed class VoiceManager
{
    /// <summary>
    /// 解码好的语音缓存条数。一条 3 秒的语音解码后大约 0.5MB，64 条 ≈ 30MB。
    /// 缓存太小的话，点着点着就要反复"解包 → 解码"，那一步会卡帧。
    /// </summary>
    private const int MaxCachedClips = 64;
    private const float ChainGap = 0.42f;

    /// <summary>
    /// 闭嘴的时间要卡在音频结束点之前一点点。
    /// 嘴型开关（Animator 的 Enable_Talk）在整段音频期间都是开的，如果按
    /// clip.length + ChainGap 关，话说完之后嘴还会多动大半秒。
    /// </summary>
    private const float MouthTailMargin = 0.15f;

    private const float LoadTimeout = 5f;

    /// <summary>已经说过节日台词的那一天（yyyy-MM-dd）；这天不再挑节日台词。</summary>
    private string _festivalSaidDay = string.Empty;

    /// <summary>上次写进日志的"时段对不上"组合，避免每抽一次就刷一条。</summary>
    private string _lastLoggedPhaseMismatch = string.Empty;

    /// <summary>最近一次"游戏那边有人开口"的时刻（判定在 DriveMouth 里，这里只做记录）。</summary>
    private float _gameVoiceStartedAt = -1f;

    /// <summary>下一次允许写"被游戏的过滤器挡掉"那条日志的时刻（同一个原因 30 秒最多一条）。</summary>
    private float _nextFilteredLogTime;

    /// <summary>
    /// 看门狗：连播最后一次推进的时间、以及游戏"正在说话"连续持续了多久。
    ///
    /// 这两个状态一旦被卡住（协程被中途掐断、游戏那边的 _isFinishedVoice 停在 false），
    /// 表现就是"她几乎不说话、点她也没反应" —— 因为 Play() 一律返回 Deferred，
    /// 而点击被我们映射成"现在不能反应"。所以给它们加超时自愈。
    /// </summary>
    private float _lastChainTick;
    private float _gameVoiceBusySince;
    private bool _loggedGameVoiceStuck;
    /// <summary>最近一次解码的结果（TryDecode 写，LoadOgg 读；协程之间不好用返回值，就用这个）。</summary>
    private AudioClip _lastDecoded;
    /// <summary>单条语音的解码超时：超过就放弃并写日志，别让外面一直等。</summary>
    private const float DecodeTimeoutSeconds = 6f;
    /// <summary>"正在解码"标记的最长有效期：超过就当它丢了，允许重新取。</summary>
    private const float LoadingStuckSeconds = 12f;
    /// <summary>每条语音是什么时候开始解码的（给上面那个超时用）。</summary>
    private readonly Dictionary<string, float> _loadingSince = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    private float _chainStartedAt;
    private string _chainStage = string.Empty;
    /// <summary>
    /// 连播的总时长上限**按句数算**：一句 15 秒 + 30 秒余量。
    ///
    /// 以前写死 45 秒，结果长故事（5〜14 句、40〜70 秒）讲到后面就被当成"卡住"掐掉 ——
    /// 用户听到的正是"长语音说到最后一两句突然断了"。故事本身就是要讲这么久的，
    /// 所以上限必须跟着句数走；真正判断"卡住"的是下面那个"多久没有推进"的判据。
    /// </summary>
    private const float ChainSecondsPerLine = 15f;
    private const float ChainLimitSlackSeconds = 30f;
    private const float ChainStuckSeconds = 20f;
    private float _chainMaxSeconds = 45f;
    private const float GameVoiceStuckSeconds = 30f;

    /// <summary>
    /// "短音"的上限。游戏那边响这么久以内就当它是动作自带的小声音
    ///（看书时的嗯声、翻页、呼呼吹气），超过这个长度就当她是真的在说话。
    ///
    /// 为什么要区分：这两种声音都会先调 VoiceManager.Stop() 把我们正在播的那条掐掉，
    /// 但处理方式完全不同 —— 她真开口要让路，动作小声音只该让我们把这一句重放一遍。
    /// 以前一律当成"她开口了"，用户听到的就是"讲到一半被嗯声打断，而且再也不接上"。
    /// </summary>
    private const float ShortBlipSeconds = 1.6f;

    /// <summary>同一句最多被短音打断几次（防死循环：声音一直响就放弃）。</summary>
    private const int MaxResumesPerLine = 2;

    /// <summary>"游戏刚出过声"的判定窗口。只有在这个窗口里才按短音 / 长句处理，
    /// 免得游戏那边的语音标志卡住时把我们整段连播判死。</summary>
    private const float GameVoiceRecentSeconds = 3f;

    private readonly AudioSource _source;
    private readonly VoiceRunner _runner;
    private readonly GameSubtitle _subtitle;
    private readonly Dictionary<string, VoiceLine> _catalog = new Dictionary<string, VoiceLine>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _pools = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _chains = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _nextTimes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _lru = new List<string>();
    private readonly HashSet<string> _loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly string _tempDir;
    private byte[] _packBytes;
    private readonly HashSet<string> _packNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool _chainRunning;
    private bool _abortRequested;
    /// <summary>当前这句是不是走游戏语音系统播的（决定"还在不在响"该问谁）。</summary>
    private bool _playingNative;
    private string _lastPlayed;
    private float _nextAttempt;
    private bool _nextIsClick;
    private int _gestureChance;

    public VoiceManager(GameObject host)
    {
        if (host.GetComponent<AudioSource>() == null)
            host.AddComponent<AudioSource>();
        _source = host.GetComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.loop = false;
        _source.spatialBlend = 0f;
        _source.volume = 0.9f;
        _runner = host.GetComponent<VoiceRunner>() ?? host.AddComponent<VoiceRunner>();
        _subtitle = host.GetComponent<GameSubtitle>() ?? host.AddComponent<GameSubtitle>();

        _tempDir = PickTempDir();
        LoadPack();

        LoadCatalog();
        SelfCheckVoices();
    }

    /// <summary>
    /// 选一个能写的临时目录（语音从包里取出来后要落成文件交给 Unity 解码）。
    ///
    /// 优先系统临时目录；写不进去（被安全软件拦、权限异常）就退到游戏自己的
    /// 持久化目录，再退到插件目录 —— 总有能用的，不会因为"没地方写"而整体没声音。
    /// </summary>
    private static string PickTempDir()
    {
        var candidates = new List<string>
        {
            Path.Combine(Path.GetTempPath(), "ChillClockVoice"),
            Path.Combine(Application.persistentDataPath ?? ".", "ChillClockVoice"),
            Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "ChillClockVoice"),
        };

        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, "probe.tmp");
                File.WriteAllBytes(probe, new byte[] { 1, 2, 3 });
                File.Delete(probe);
                return dir;
            }
            catch
            {
                // 换下一个候选
            }
        }

        return candidates[0];
    }

    /// <summary>
    /// 启动自检：从语音包里真抽一条出来解码一次，把"语音链路通不通"写进日志。
    /// 以后有人反馈"她不出声"时，看这一行就能判断是不是文件/解码的问题。
    /// </summary>
    private void SelfCheckVoices()
    {
        try
        {
            var sample = _catalog.Keys.FirstOrDefault(
                f => f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase));
            if (sample == null)
            {
                Plugin.Log.LogWarning("[Chill Clock] 语音自检：目录里一条 ogg 都没有（目录没读到？）");
                return;
            }

            using var archive = OpenArchive();
            var entry = archive?.GetEntry(sample);
            if (entry == null)
            {
                Plugin.Log.LogWarning("[Chill Clock] 语音自检：包里找不到 " + sample +
                                      " —— 内嵌语音包不可用，语音不会响（发布版只有一个 dll，" +
                                      "出现这种情况请重新下载）");
                return;
            }

            using var stream = entry.Open();
            // 真写到临时目录再比字节数：这一步就是运行时播放前的完整流程，
            // 它过了就说明"从包里取一条 → 交给 Unity 解码"这条路是通的。
            Directory.CreateDirectory(_tempDir);
            var probe = Path.Combine(_tempDir, "selfcheck.ogg");
            using (var dst = File.Create(probe))
                stream.CopyTo(dst);
            var written = new FileInfo(probe).Length;

            if (written != entry.Length)
            {
                TryDelete(probe);
                Plugin.Log.LogWarning("[Chill Clock] 语音自检：写出的文件和包里的不一样（" +
                                      written + " / " + entry.Length + " 字节），语音可能不正常");
                return;
            }

            Plugin.Log.LogInfo("[Chill Clock] 语音自检：取包 OK（内嵌包抽一条 " + sample + "，" +
                               entry.Length + " 字节；解包目录 " + _tempDir +
                               "；目录条数 " + _catalog.Count + "）");

            // 关键一步：**真解码一次**。这一步过了才说明"取包 → 落文件 → 交给 Unity 解码"
            // 整条链路是通的；失败的话日志里会写明超时还是报错，不用靠点她去猜。
            _runner.StartCoroutine(SelfCheckDecode(sample, probe));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] 语音自检失败：" + e.Message);
        }
    }

    /// <summary>启动自检的第二半：把刚写出来的那条真解一次，并写进日志。</summary>
    private IEnumerator SelfCheckDecode(string sample, string probePath)
    {
        yield return TryDecode(sample, probePath);
        var clip = _lastDecoded;
        TryDelete(probePath);

        if (clip != null)
        {
            Plugin.Log.LogInfo("[Chill Clock] 语音自检：解码 OK（" + sample + "，" +
                               clip.length.ToString("0.00") + " 秒）—— 语音链路正常");
            UnityEngine.Object.Destroy(clip);
            yield break;
        }

        Plugin.Log.LogWarning("[Chill Clock] 语音自检：解码失败 —— 她不出声就是这一步断了（具体原因看上面那条警告）");
    }

    /// <summary>
    /// 把 DLL 内嵌的语音包整个读到内存里。
    ///
    /// 这里**不长期持有 ZipArchive**，只留字节：Mono 的 ZipArchive 一旦释放过
    /// 条目流，底层流就可能一起被关掉，之后所有条目都读不出来
    /// （日志里就是 "Cannot access a disposed object."）。每次取语音时现开一个
    /// 包，用完整个丢掉，就不会被这件事影响。
    /// </summary>
    private void LoadPack()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Voices.pack", StringComparison.OrdinalIgnoreCase));
            if (resource != null)
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream != null)
                {
                    _packBytes = ReadAllBytes(stream);
                    Plugin.Log.LogInfo("[Chill Clock] 内嵌语音包：" + IndexPack() + " 条");
                    return;
                }
            }
        }
        catch (Exception e)
        {
            _packBytes = null;
            Plugin.Log.LogWarning("[Chill Clock] 内嵌语音包读取失败: " + e);
        }

        // 内嵌包读不到：说清楚，别让人对着"她怎么不说话了"猜
        _packBytes = null;
        Plugin.Log.LogWarning("[Chill Clock] 读不到内嵌语音包，语音不会响 —— " +
                              "请重新下载官方 release 的 ChillClock.dll。");
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>开一个一次性的语音包，只用来取这一次的数据。</summary>
    private ZipArchive OpenArchive()
    {
        var bytes = _packBytes;
        return bytes == null ? null : new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
    }

    /// <summary>把包里的条目名记下来，省得每次取语音都去问一遍包。</summary>
    private int IndexPack()
    {
        _packNames.Clear();
        using var archive = OpenArchive();
        if (archive == null)
            return 0;

        foreach (var entry in archive.Entries)
            _packNames.Add(entry.FullName);
        return archive.Entries.Count;
    }

    /// <summary>
    /// 立刻停掉我们正在说的这句，并把口型/视线收干净。
    ///
    /// 用在两种情况：番茄钟到点（游戏马上要自己开口）、以及发现游戏已经在说话。
    /// 否则两边会叠在一起。
    /// </summary>
    public void Abort()
    {
        if (!_chainRunning && !_source.isPlaying && !HeroineActionBridge.IsNativeVoicePlaying())
            return;

        Interrupt();
    }

    /// <summary>
    /// 游戏那边有人开口（HeroineAI.PlayVoice）。**不要**立刻把我们的整段作废：
    /// 动作自带的小声音（看书时的嗯声、翻页、呼呼吹气）也走这条路，直接作废就是
    /// 用户遇到的"讲到一半被嗯声掐掉，而且再也不接上"。
    ///
    /// 这里只做两件事：
    ///   1) 记一笔时间，交给 DriveMouth 按"这条声音有多长"来判定该重放还是该让路；
    ///   2) 我们那份音频如果是自己 AudioSource 播的（游戏 Stop() 管不到它），先停掉，
    ///      免得跟她的声音叠在一起。
    /// </summary>
    public void NotifyGameVoiceStarted()
    {
        _gameVoiceStartedAt = Time.realtimeSinceStartup;

        if (!_chainRunning && !_source.isPlaying)
            return;

        if (_playingNative)
            return;     // 借游戏语音系统播的那条：游戏自己那下 Stop() 已经把它停掉了

        try
        {
            _source.Stop();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>游戏那边是不是刚刚出过声（3 秒以内）。</summary>
    private bool GameVoiceRecent
    {
        get
        {
            return _gameVoiceStartedAt > 0f &&
                   Time.realtimeSinceStartup - _gameVoiceStartedAt <= GameVoiceRecentSeconds;
        }
    }

    /// <summary>
    /// 立刻闭嘴：停掉我们的音频（不管它是走游戏语音系统还是走自己的 AudioSource）、
    /// 收掉字幕、关掉口型，并且让连播里还没说的句子作废。
    ///
    /// 为什么要连"游戏语音系统里那条"一起停：我们的语音是借游戏自己的 VoiceManager 播的，
    /// 而游戏每次自己开口都会先 VoiceManager.Stop()，那一下会把它管的所有 voice player
    /// 全停掉——我们的也在其中。以前这里只停自己的 AudioSource，于是音频已经被游戏掐断，
    /// 连播的第二句却还照念：听感就是"话说一半断了，等她的动作完了又接上后半段"。
    /// </summary>
    private void Interrupt()
    {
        _abortRequested = true;

        try
        {
            _source.Stop();
        }
        catch
        {
            // ignore
        }

        HeroineActionBridge.StopNativeVoice();

        try
        {
            _subtitle?.HideNow();
        }
        catch
        {
            // ignore
        }

        HeroineActionBridge.SetMouthTalk(false);
        HeroineActionBridge.EndLineReaction();
    }

    /// <summary>连播都结束了、口型还开着多久，就认定"没人管了"。</summary>
    private const float OrphanMouthSeconds = 4f;

    /// <summary>字幕显示超过这么久还没收掉，就是收尾协程没跑完。</summary>
    private const float StuckSubtitleSeconds = 30f;

    /// <summary>
    /// 结束通话演出期间、以及演出结束后的这一小段时间里，我们一句话都不说。
    ///
    /// 用户实测：点「结束通话」并确认之后，聪音说完告别的那句，我们这边的台词又接上了 ——
    /// 因为演出一结束，游戏那个"正在结束通话"的标志就变回 false，我们按常规又觉得自己可以开口。
    /// 这段时间本来就该安静（他刚跟你道别），所以演出中停掉我们的台词，结束后再静默这么一会儿。
    /// </summary>
    private const float CallEndingQuietSeconds = 20f;

    private float _callEndingQuietUntil;
    private float _endingSeenSince = -1f;
    private bool _sawEndingClear;

    /// <summary>
    /// 「点了结束通话」专用：这段时间里被打断也不许重放。
    /// 只影响这一种情况 —— 平时"被动作小声音打断就重放这一句"的功能保持原样
    ///（那是用户特意要的行为，不能动）。新的连播开始时清掉。
    /// </summary>
    private bool _noResumeAfterCut;

    /// <summary>
    /// 用户点了「结束通话」并确认（由 ExitCallPatch 直接挂到游戏那个按钮上调用）：
    /// 立刻停下我们的声音，并在接下来 20 秒内不再开口。
    /// </summary>
    public void NotifyCallEnding()
    {
        _callEndingQuietUntil = Time.realtimeSinceStartup + CallEndingQuietSeconds;
        _endingSeenSince = -1f;
        _sawEndingClear = true;
        _noResumeAfterCut = true;

        try
        {
            if (_chainRunning || _source.isPlaying || HeroineActionBridge.IsNativeVoicePlaying())
            {
                Plugin.Log.LogInfo("[Chill Clock] 结束通话：立刻停掉我们的台词");
                Interrupt();
            }

            _chainRunning = false;
            _chainStage = string.Empty;
            _chainStartedAt = 0f;
            HeroineActionBridge.SetMouthTalk(false, true);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] 结束通话处理失败: " + e.Message);
        }
    }

    /// <summary>
    /// 每帧跑的"卡死自愈"。
    ///
    /// 为什么必须每帧跑：这些检查原来只写在 Play() 里，可真正卡住的时候点击根本走不到
    /// Play()（一律被映射成"现在不能反应"），于是整局都解不开。用户报的现象就是
    /// 「只剩游戏那句字幕、聪音嘴巴一直开合、怎么点都没反应，连休息结束进入学习还是这样」。
    /// 成因是游戏那边把自己的语音标志卡在"正在说话"，我们关口型时第一道检查直接返回，
    /// 口型就永远留在了开着的状态（见 HeroineActionBridge.SetMouthTalk 的 force 参数）。
    /// </summary>
    public void TickWatchdog()
    {
        try
        {
            var now = Time.realtimeSinceStartup;

            // 0) 结束通话：演出中马上停掉我们的台词，演出结束后再静默一小段
            SuperviseCallEnding(now);

            // 1) 连播协程被掐断，或者卡在某个等待里
            var stuck = _chainRunning &&
                        ((_lastChainTick > 0f && now - _lastChainTick > ChainStuckSeconds) ||
                         (_chainStartedAt > 0f && now - _chainStartedAt > _chainMaxSeconds));
            if (stuck)
            {
                Plugin.Log.LogWarning("[Chill Clock] 连播卡住（" + _chainStage + "），看门狗重置");
                ResetStuckChain();
            }

            // 2) 没人在播、口型却还开着 = 上面那次卡住留下的后遗症。
            //    只有在"游戏也没在说话"时才强制闭口：她真的在说台词时口型本来就该动，
            //    那由游戏自己管。游戏那边标志卡死超过 30 秒后 IsGameVoiceBusySafe()
            //    会返回 false（它的既有自愈），这时候才会轮到我们把口型收掉。
            var gameVoiceBusy = IsGameVoiceBusySafe();
            if (!_chainRunning && !gameVoiceBusy && HeroineActionBridge.MouthTalkOn &&
                now - HeroineActionBridge.MouthTalkChangedAt > OrphanMouthSeconds)
            {
                Plugin.Log.LogWarning("[Chill Clock] 口型没人管了，强制闭上");
                HeroineActionBridge.SetMouthTalk(false, true);
            }

            // 3) 字幕收不回去（游戏把它的字幕顶上来之后我们的收尾被打断）
            if (_subtitle != null && _subtitle.IsShowing &&
                now - _subtitle.ShowingSince > StuckSubtitleSeconds)
            {
                Plugin.Log.LogWarning("[Chill Clock] 字幕卡住了，看门狗收起");
                _subtitle.HideNow();
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] watchdog failed: " + e.Message);
        }
    }

    /// <summary>把卡住的连播状态整个清干净（音频、口型、字幕、动作反应）。</summary>
    /// <summary>
    /// 盯着「结束通话」这件事：
    ///   - 用户一点确认就立刻停（见 NotifyCallEnding，由 ExitCallPatch 直接挂到按钮上）
    ///   - 演出期间：立刻停掉我们的台词（他说完再见之后不该再有我们的话）
    ///   - 演出结束后 20 秒内：不开口（见 CallEndingQuietSeconds）
    /// 标志位被存档带进来一直挂着 true 的那种情况，超过 60 秒就当卡住，不再静默。
    /// </summary>
    private void SuperviseCallEnding(float now)
    {
        var ending = HeroineActionBridge.IsGameEndingCall();

        if (!ending)
        {
            _endingSeenSince = -1f;
            _sawEndingClear = true;

            // 演出已经结束、但还在静默窗口里：这时候口型要是还开着（我们的标志没清掉，
            // 或者游戏那边的语音标志卡在"正在说话"导致普通路径关不掉），强制闭上。
            // 用户反馈的就是"点结束通话之后声音停了，嘴却一直在动"。
            if (now < _callEndingQuietUntil && !_chainRunning &&
                HeroineActionBridge.MouthTalkOn && now - HeroineActionBridge.MouthTalkChangedAt > 3f)
            {
                Plugin.Log.LogInfo("[Chill Clock] 结束通话后口型还开着，强制闭上");
                HeroineActionBridge.SetMouthTalk(false, true);
            }

            return;
        }

        // 从游戏启动就挂着 true（存档里的），不是真的在演出：忽略
        if (!_sawEndingClear)
            return;

        if (_endingSeenSince < 0f)
            _endingSeenSince = now;
        if (now - _endingSeenSince > 60f)
            return;

        _callEndingQuietUntil = now + CallEndingQuietSeconds;
        _noResumeAfterCut = true;

        if (_chainRunning || (_source != null && _source.isPlaying))
        {
            Plugin.Log.LogInfo("[Chill Clock] 结束通话演出中：停掉我们的台词");
            Interrupt();
            _chainRunning = false;
            _chainStage = string.Empty;
            _chainStartedAt = 0f;
        }

    }

    private void ResetStuckChain()
    {
        _chainRunning = false;
        _chainStage = string.Empty;
        _chainStartedAt = 0f;
        _abortRequested = false;

        try
        {
            _source.Stop();
        }
        catch
        {
            // ignore
        }

        HeroineActionBridge.StopNativeVoice();
        HeroineActionBridge.EndLineReaction();
        // 口型不在这里强关：她这时候可能真的在说游戏自己的台词，
        // 那个由上面第 2 条规则按"游戏到底有没有在说话"判断后再收。

        try
        {
            _subtitle?.HideNow();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>退出时丢开语音包。</summary>
    public void Dispose()
    {
        // 注意：**不要**把 _packBytes / _packNames 丢掉。
        //
        // 游戏中途发起过一次"退出"又取消时，我们的 OnDestroy 会被调到 → 以前这里
        // 就把语音包扔了。之后点击、走神提醒都还会走到我们这（日志能看到"点击：接管"），
        // 但每条语音都取不到 —— 表现就是"用了之后几乎不说话、点她也没反应"。
        // 这里只清掉缓存对象，包本身留着。
        try
        {
            _clips.Clear();
            _lru.Clear();
            _loading.Clear();
            _loadingSince.Clear();
        }
        catch
        {
            // ignore
        }
    }

    public VoiceStartResult PlayDistraction() => Play("Distraction", 3f);

    /// <summary>我们这会儿正在念台词（连播中或音频还在响）。</summary>
    public bool IsBusy => _chainRunning || (_source != null && _source.isPlaying);
    public VoiceStartResult PlayTaskManager() => Play("TaskManager", 4f);
    public VoiceStartResult PlayExitAttempt() => Play("Exit", 4f);
    public VoiceStartResult PlayRestReminder() => Play("Rest", 30f);

    /// <summary>休息中**自发**的那一条：池子和上面那条共用，但身份是闲聊，要跟游戏过滤器走。</summary>
    public VoiceStartResult PlayRestChat() => Play("Rest", 30f, null, spontaneous: true);
    public VoiceStartResult PlayAmbient() => Play("Ambient", 45f);

    /// <summary>非专注（既不专注也不休息）时的自言自语：小课堂那种连着讲几句的段落。</summary>
    public VoiceStartResult PlayIdleTalk() => Play("IdleTalk", 90f);

    /// <summary>休息时的闲聊：同样是小课堂段落，和"休息提醒"分开两个池子。</summary>
    public VoiceStartResult PlayBreakTalk() => Play("BreakTalk", 90f);

    // ===== 临时测试用（测完删掉）=====
    // 非专注时第一次点击：强制播「トゲアリ トゲナシ トゲハムシ」那一段（开场是 Satone_0304）。
    public VoiceStartResult PlayTestBeetle() => Play("IdleTalk", 0f, "Satone_0304.ogg");

    // 之后的点击：从小课堂这一批里随机抽（同一个池子，但冷却缩短到 2 秒方便连着点）。
    public VoiceStartResult PlayTestBatch() => Play("IdleTalk", 2f);

    /// <summary>测试用：强制播指定开头的那一段（传该段第一句的文件名）。</summary>
    public VoiceStartResult PlayTestStory(string firstFile) => Play("IdleTalk", 2f, firstFile);
    // ===== 临时测试用结束 =====

    /// <summary>
    /// 点击聪音时的反应台词。state 取 Work / Break / Normal，
    /// 对应她此刻是在工作、休息还是普通待机；再按当前时段筛选。
    /// </summary>
    public VoiceStartResult PlayClick(string state)
    {
        var result = Play("Click_" + state, 4f);
        if (result == VoiceStartResult.Started)
            _nextIsClick = true;
        return result;
    }

    /// <param name="spontaneous">这一句算不算"她自己找话说"（池子名判不出来的才要显式传，见 PlayRestChat）。</param>
    private VoiceStartResult Play(string trigger, float cooldown, string forced = null, bool spontaneous = false)
    {
        if (_source == null || _runner == null)
        {
            return VoiceStartResult.Deferred;
        }

        // 刚结束通话（或者正在走结束演出）时一句话都不说
        if (Time.realtimeSinceStartup < _callEndingQuietUntil)
        {
            return VoiceStartResult.Deferred;
        }

        // 连播"卡住"自愈：正常一句最长十几秒（含等字幕），超过 20 秒没动静就是协程被掐断了
        var nowChain = Time.realtimeSinceStartup;
        if (_chainRunning && nowChain - _lastChainTick > ChainStuckSeconds)
        {
            Plugin.Log.LogWarning("[Chill Clock] 连播状态卡住了，重置（这样她才会重新开口）");
            _chainRunning = false;
            _abortRequested = false;
            HeroineActionBridge.SetMouthTalk(false);
        }
        else if (_chainRunning && _chainStartedAt > 0f && nowChain - _chainStartedAt > _chainMaxSeconds)
        {
            // 总时长硬上限：协程还活着、但明显太久（例如某个等待循环条件永远不成立）
            Plugin.Log.LogWarning("[Chill Clock] 连播超过 " + (int)_chainMaxSeconds +
                                  " 秒还没结束（卡在：" + _chainStage + "），强制结束");
            _chainRunning = false;
            _abortRequested = true;
            HeroineActionBridge.SetMouthTalk(false);
            HeroineActionBridge.EndLineReaction();
        }

        if (_chainRunning || _source.isPlaying)
        {
            return VoiceStartResult.Deferred;
        }

        // 上一句的字幕还在显示 = 她还没说完。这时候不开口（点击也是一样），
        // 免得新句子把上一句的字幕顶掉、或者两句叠在一起。
        if (_subtitle != null && _subtitle.IsShowing)
        {
            // 字幕卡住同样会让她彻底不开口（点击也会被当成"现在不能反应"）：
            // 超过 30 秒还挂着就说明这条收尾协程没跑完，强制收掉。
            if (Time.realtimeSinceStartup - _subtitle.ShowingSince > 30f)
            {
                Plugin.Log.LogWarning("[Chill Clock] 字幕卡住了，强制收起");
                _subtitle.HideNow();
            }
            else
            {
                return VoiceStartResult.Deferred;
            }
        }

        var now = Time.realtimeSinceStartup;
        if (now < _nextAttempt)
        {
            return VoiceStartResult.Deferred;
        }

        if (_nextTimes.TryGetValue(trigger, out var next) && now < next)
        {
            return VoiceStartResult.Skipped;
        }

        if (!_pools.TryGetValue(trigger, out var pool) || pool.Count == 0)
        {
            return VoiceStartResult.Skipped;
        }

        // 游戏自己的「自言自语过滤器」**开着**（= 静音自语）时，她自发的闲聊一律不开口 ——
        // 和游戏里的做法一致：那道闸门管着"她什么时候能自言自语"，而我们的语音走的是
        // 自己的播放链路、绕过了它，所以在这里补上同一个判据。
        // 极性：过滤器"开"对应 IsPlaySelfTalk = false，判据在 HeroineActionBridge.AllowsSelfTalk
        // 里翻了正（详见那边的注释）。
        // 只挡"她自己找话说"的那几段（见 IsSelfTalkPool / spontaneous 参数）；提醒类不受影响 ——
        // 游戏那边这两个开关本来也是各管各的，休息开始 / 创作结束的提醒照播。
        if ((spontaneous || IsSelfTalkPool(trigger)) && !HeroineActionBridge.AllowsSelfTalk)
        {
            // 写一条日志（同一个池子 30 秒最多一条）：不然"她怎么突然不闲聊了"
            // 从日志上完全看不出来，只能靠猜。
            if (now >= _nextFilteredLogTime)
            {
                _nextFilteredLogTime = now + 30f;
                Plugin.Log.LogInfo("[Chill Clock] 游戏的「自言自语」过滤器开着，" + trigger + " 不播");
            }

            return VoiceStartResult.Skipped;
        }

        // 游戏自己正在说话时先让路：既不会盖掉它，也不会让它的 PlayVoice 因为
        // _isFinishedVoice 还是 false 而被静默丢弃。
        if (IsGameVoiceBusySafe())
        {
            _nextAttempt = now + 0.5f;
            return VoiceStartResult.Deferred;
        }

        // 游戏正在放它自己的演出（开场问候、结束通话挥手等）时同样让路：
        // 这段时间插话既会盖掉台词，也会打断它排好的动作。
        if (HeroineActionBridge.IsGameSequenceBusy())
        {
            _nextAttempt = now + 1f;
            return VoiceStartResult.Deferred;
        }

        var start = string.IsNullOrEmpty(forced) ? Pick(pool) : forced;
        if (start == null)
        {
            return VoiceStartResult.Skipped;
        }
        _lastPlayed = start;

        // 记一条"这次说了什么、当时是什么时段"：以后"冒出不该出现的台词"这类问题，
        // 日志里一眼就能看到到底是哪条池子挑出来的。
        Plugin.Log.LogInfo("[Chill Clock] 播放 " + trigger + "：" + start +
                           "（" + DateTime.Now.ToString("HH:mm") + " / " +
                           PhaseFromClock(DateTime.Now.Hour) + "）");

        var chain = _chains.TryGetValue(start, out var found) && found.Count > 1
            ? found
            : new List<string> { start };

        _nextTimes[trigger] = now + cooldown + (chain.Count > 1 ? chain.Count * 4.5f : 0f);
        _gestureChance = GestureChanceFor(trigger);
        _abortRequested = false;
        _noResumeAfterCut = false;   // 新的一段开始：恢复"短音打断可重放"的老规矩
        _runner.StartCoroutine(PlayChain(chain));
        return VoiceStartResult.Started;
    }

    /// <summary>
    /// 这个池子算不算"她自发的闲聊"。游戏的「自言自语过滤器」只管这一类：
    ///   Ambient   —— 专注中的自言自语
    ///   IdleTalk  —— 非专注（待机）时说的小课堂段落
    ///   BreakTalk —— 休息中的闲聊（同样是"小课堂"那批，和"休息提醒"分开两个池子）
    /// 提醒类（走神 / 任务管理器 / 退出 / 休息开始 / 创作结束）和点击回应都不在此列 ——
    /// 游戏自己的过滤器也不管这些，把过滤器打开（静音自语）之后那些提醒照样会来。
    ///
    /// <c>Rest</c> 故意不在这里：它一半是提醒（进入休息那条，要留）、一半是闲聊
    /// （休息中定时那条，要挡），靠 Play 的 spontaneous 参数区分。
    /// </summary>
    private static bool IsSelfTalkPool(string trigger)
    {
        return string.Equals(trigger, "Ambient", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trigger, "IdleTalk", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trigger, "BreakTalk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 各池子"顺便做个动作"的概率。
    /// 提醒类（走神 / 任务管理器 / 退出）一律不做动作 —— 那时候她该看着你说话，
    /// 而不是换姿势；闲聊和点击才偶尔来一下。
    /// </summary>
    private static int GestureChanceFor(string trigger)
    {
        switch (trigger)
        {
            case "Distraction":
            case "TaskManager":
            case "Exit":
                return 0;
            default:
                return 10;
        }
    }

    /// <summary>
    /// 从候选池里抽一条，优先当前时段的专属台词。
    /// 别的时段的台词不会被抽到；一条都不匹配时才退回"未标时段"的中性台词。
    ///
    /// 节日台词（目录第 11 列）是另一条硬规则：写着节日的条目**只在当天**参与抽取，
    /// 别的一律先剔掉；当天则优先说节日台词（留一点概率说平时的，免得整晚都在拜年）。
    /// </summary>
    /// <summary>
    /// 问游戏"你是不是正在说话"，但带超时。
    ///
    /// 游戏那边的 _isFinishedVoice 一旦停在 false（它自己的语音流程被中途打断就会这样），
    /// 我们这边会永远让路：一句话都不说、点她也被映射成"现在不能反应" ——
    /// 用户看到的就是"用了之后很少说话、点击没反应"。
    /// 所以连续超过 30 秒还报"在说话"，就当它卡住了，先忽略。
    /// </summary>
    /// <summary>
    /// 记一条"这次没开口是因为被挡住了"。同一个原因 10 秒最多写一条，
    /// 用来排查"用了之后几乎不说话、点她也没反应"这类问题。
    /// </summary>
    private bool IsGameVoiceBusySafe()
    {
        if (!HeroineActionBridge.IsGameVoiceBusy())
        {
            _gameVoiceBusySince = 0f;
            _loggedGameVoiceStuck = false;
            return false;
        }

        var now = Time.realtimeSinceStartup;
        if (_gameVoiceBusySince <= 0f)
            _gameVoiceBusySince = now;

        if (now - _gameVoiceBusySince > GameVoiceStuckSeconds)
        {
            if (!_loggedGameVoiceStuck)
            {
                _loggedGameVoiceStuck = true;
                Plugin.Log.LogWarning("[Chill Clock] 游戏语音标志疑似卡住（连续 30 秒都在说话），先忽略它");
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// 从池子里抽一条。用户设定的两条规则在这里：
    ///   1) 池子里既有"我们的故事"又有原本的台词时，我们的占 30%
    ///   2) 不要连续两段都抽到同一个故事
    /// </summary>
    private string Pick(List<string> pool)
    {
        var ours = new List<string>();
        var theirs = new List<string>();
        foreach (var file in pool)
        {
            if (file.StartsWith("Satone_", StringComparison.OrdinalIgnoreCase))
                ours.Add(file);
            else
                theirs.Add(file);
        }

        var useOurs = ours.Count > 0 && (theirs.Count == 0 || UnityEngine.Random.value < 0.30f);
        var source = useOurs ? ours : theirs;
        if (source.Count == 0)
            source = pool;

        var picked = PickByTime(source);

        // 不要连着两段同一个故事（只在"我们的池子"里避让）
        if (useOurs && ours.Count > 1 && picked == _lastPlayed)
        {
            var again = PickByTime(ours);
            if (!string.IsNullOrEmpty(again))
                picked = again;
        }

        return picked;
    }

    private string PickByTime(List<string> pool)
    {
        if (pool.Count == 0)
            return null;

        // 节日判定优先用游戏自己的限时活动（圣诞 / 愚人节），其余节日看日期表
        var today = HeroineActionBridge.GameEventFestivalId() ?? FestivalCalendar.TodayId();
        var dayKey = DateTime.Now.ToString("yyyy-MM-dd");
        var hour = DateTime.Now.Hour;
        // 时段直接按本机时钟算，规则和游戏自己的 TimeOfDayProvider 一模一样
        //（早上 6-10 / 中午 11-16 / 傍晚 17-19 / 晚上 20-5）。
        // 以前这里问的是游戏那边的 provider —— 它读的也是同一个时钟，多一层反射只是
        // 多一个可能读不到、读到旧值的点。顺便把两边比一下，对不上就写日志。
        var timeOfDay = PhaseFromClock(hour);
        var gamePhase = HeroineActionBridge.GetTimeOfDay();
        var phaseKey = timeOfDay + "|" + gamePhase;
        if (!string.IsNullOrEmpty(gamePhase) &&
            !string.Equals(gamePhase, timeOfDay, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_lastLoggedPhaseMismatch, phaseKey, StringComparison.Ordinal))
        {
            _lastLoggedPhaseMismatch = phaseKey;
            Plugin.Log.LogWarning("[Chill Clock] 时段对不上：游戏说 " + gamePhase +
                                  "，本机时钟（" + hour + " 点）算出来是 " + timeOfDay +
                                  "，按本机时钟走");
        }
        var festival = new List<string>();
        var regular = new List<string>(pool.Count);
        foreach (var file in pool)
        {
            if (!_catalog.TryGetValue(file, out var line))
            {
                // 目录里查不到的（理论上不会有）当普通台词处理
                regular.Add(file);
                continue;
            }

            // 小时窗：游戏自己的时段只有 Morning/Noon/Evening/Night 四段，
            // Noon 实际覆盖 11:00-16:59，所以"午饭/午休"这种过了点就很怪的台词
            // 单独写一个更窄的区间（目录第 12 列），不在区间内就不参与抽取。
            if (!HourAllows(line, hour))
                continue;

            if (string.IsNullOrEmpty(line.Festival))
            {
                regular.Add(file);
                continue;
            }

            // 节日台词还要过时段这一关：赏月、七夕这类只该在晚上说的，
            // 目录里同样要写 Time（Night / Evening），不匹配就不参与抽取。
            if (!string.IsNullOrEmpty(today) &&
                string.Equals(line.Festival, today, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(line.Time) ||
                 string.IsNullOrEmpty(timeOfDay) ||
                 string.Equals(line.Time, timeOfDay, StringComparison.OrdinalIgnoreCase)))
            {
                festival.Add(file);
            }
            // 其它节日的台词直接丢弃：绝不能在普通日子冒出来
        }

        // 一天只说一次节日台词：说过就记下日期，这天不再挑节日台词
        if (festival.Count > 0 && _festivalSaidDay != dayKey)
        {
            _festivalSaidDay = dayKey;
            return festival[UnityEngine.Random.Range(0, festival.Count)];
        }

        if (regular.Count == 0)
            return festival.Count > 0 ? festival[UnityEngine.Random.Range(0, festival.Count)] : null;

        pool = regular;

        var neutral = new List<string>();
        foreach (var file in pool)
        {
            if (_catalog.TryGetValue(file, out var line) && string.IsNullOrEmpty(line.Time))
                neutral.Add(file);
        }

        var now = timeOfDay;
        if (!string.IsNullOrEmpty(now) && neutral.Count > 0)
        {
            var matches = new List<string>();
            foreach (var file in pool)
            {
                if (_catalog.TryGetValue(file, out var line) &&
                    !string.IsNullOrEmpty(line.Time) &&
                    string.Equals(line.Time, now, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(file);
                }
            }

            // 时段专属台词只是"多一次中签机会"，不能把中性台词全挡掉。
            // 以前这里只要有时段专属就直接返回它们，而每个时段只有八九句 ——
            // 结果一整段时间里翻来覆去就那几句，池子里四百多条等于没用到。
            if (matches.Count > 0 && UnityEngine.Random.value < 0.2f)
                return matches[UnityEngine.Random.Range(0, matches.Count)];
        }

        var source = neutral.Count > 0 ? neutral : pool;
        return source[UnityEngine.Random.Range(0, source.Count)];
    }

    private IEnumerator PlayChain(List<string> files)
    {
        _chainRunning = true;
        // 我们讲这一段期间，把她"自发的自言自语"关掉（包括跟着动作出来的小声音），
        // 讲完再还原 —— 比事后拦声音干净得多。
        HeroineActionBridge.SuppressSelfTalk(true);
        _lastChainTick = Time.realtimeSinceStartup;
        _chainStartedAt = Time.realtimeSinceStartup;
        // 这一段允许响多久：按句数算（见 ChainSecondsPerLine 的注释）
        _chainMaxSeconds = ChainLimitSlackSeconds + ChainSecondsPerLine * Mathf.Max(1, files.Count);
        try
        {
            // 连播组只在第一句转头：每句都转一次头会看着像"来回扭头"
            var firstLine = true;
            foreach (var file in files)
            {
                // 上一句被游戏打断了（她去喝茶吹气、说自己的台词……），连播剩下的不说了
                if (_abortRequested)
                    break;

                // 注意：这里**不要**再调 HeroineActionBridge.ResetLookIfActionStarted()。
                //
                // 原因（用户实测）：她端着杯子时点不动，拿走书/放下杯子就正常 ——
                // 差异就是我们这段代码会调 SetLookScale。而游戏那个方法内部起了一个
                // 视线补间动画，动画在跑的时候游戏的"点击反应是否空闲"就是 false，
                // 于是我们的点击接管被判成不可接管 → 端着杯子时点她完全没反应。
                //
                // 所以喝水这块只保留"等她喝完再讲"（下面那段等待），不再碰视线。

                // 注意：这里**不要**加"她喝水就等着"的循环。
                //
                // 用户实测：加了它之后，她端着杯子时点她完全没反应 —— 因为等待是在
                // 连播里跑的，而连播期间我们的点击一律返回 Deferred（游戏那边显示
                // 禁止光标）。"倒茶时间"这个状态又会持续好一阵，看起来就是点不动。
                // 喝水这块只做视线处理（HeroineActionBridge.ResetLookIfActionStarted）。

                // 游戏正在跑它自己的演出（开窗/关窗这类）：等它演完再讲。
                // 那段时间字幕框整个归它管，我们硬说的话字幕会被收走（用户遇到的就是这个）。
                // 等完再开口，字幕和语音就一起出现。最多等 12 秒，避免卡死。
                var seqDeadline = Time.realtimeSinceStartup + 12f;
                while (HeroineActionBridge.IsGameSequenceBusy() &&
                       Time.realtimeSinceStartup < seqDeadline)
                {
                    _lastChainTick = Time.realtimeSinceStartup;
                    yield return null;
                }

                if (_abortRequested)
                    break;

                // 游戏自己开口了（它的 VoiceManager.Stop() 会把我们的音频一起掐断）：
                // 以前这里直接把整段作废 —— 用户听到的就是"讲到一半突然断了"。
                // 现在改成**等它说完再继续**，整段能一句不落地讲完。
                if (IsGameVoiceBusySafe())
                {
                    // 最多等 3 秒：等太久她会僵在那儿，比断掉更怪。
                    var waitUntil = Time.realtimeSinceStartup + 3f;
                    while (IsGameVoiceBusySafe() && Time.realtimeSinceStartup < waitUntil)
                    {
                        _lastChainTick = Time.realtimeSinceStartup;
                        yield return null;
                    }

                    if (_abortRequested)
                        break;
                }

                RequestClip(file);

                // 等待解码：用**真实时间**做闸门。
                // 之前用累加 Time.unscaledDeltaTime，一旦这个帧间隔是 0
                //（后台运行、被打断的那一帧）累加值永远不动，这个循环就永远转下去，
                // _chainRunning 卡在 true —— 表现就是"点她没反应、走神也不提醒"。
                _chainStage = "等解码 " + file;
                var loadDeadline = Time.realtimeSinceStartup + LoadTimeout;
                while (GetClip(file) == null && Time.realtimeSinceStartup < loadDeadline)
                {
                    _lastChainTick = Time.realtimeSinceStartup;
                    yield return null;
                }

                var clip = GetClip(file);
                if (clip == null)
                {
                    // 等不到就把"正在加载"标记清掉：否则这条语音会被永久跳过，
                    // 表现就是"点她、走神提醒全都没声音"。日志写清楚状态便于以后排查。
                    if (_loading.Contains(file))
                    {
                        _loading.Remove(file);
                        _loadingSince.Remove(file);
                        Plugin.Log.LogWarning("[Chill Clock] 语音没能加载出来：" + file +
                                              "（已清掉加载标记，下次会重试）");
                    }
                    else
                    {
                        Plugin.Log.LogWarning("[Chill Clock] 语音没能加载出来：" + file +
                                              "（缓存=" + _clips.Count + " 在包内=" +
                                              (_packBytes != null && _packNames.Contains(file)) + "）");
                    }

                    continue;
                }

                // 口型跟着"真正在出声"的时间段走，台词中间的停顿会闭嘴
                var line = _catalog.TryGetValue(file, out var found) ? found : null;

                // 最后一道时段闸门：目录里标了时段（Morning / Noon / Evening / Night）的台词
                // 只在该时段说。抽签那一步已经筛过一次，这里再挡一次 —— 哪怕有哪条路径绕过了
                // 抽签（连播的后几句、强制指定的开头……），也不会让她在晚上说出"早上好"。
                if (line != null && !string.IsNullOrEmpty(line.Time))
                {
                    var phase = PhaseFromClock(DateTime.Now.Hour);
                    if (!string.Equals(line.Time, phase, StringComparison.OrdinalIgnoreCase))
                    {
                        Plugin.Log.LogWarning("[Chill Clock] 时段不符，跳过这句：" + file +
                                              "（这句标的是 " + line.Time + "，现在是 " + phase + "）");
                        if (firstLine)
                            break;      // 整段第一句就不该说：这一段作废
                        continue;
                    }
                }

                PlayLine(file, clip, firstLine);
                yield return DriveMouth(clip, line);

                // 下一句开口前，先让上一句的字幕读得完：
                // 短句（语音 1.5 秒、字幕要停 2.5 秒）以前会被下一句直接顶掉，看着就是"一闪"。
                _chainStage = "句间停顿";
                var gap = ChainGap;
                if (line != null)
                {
                    var english = string.IsNullOrEmpty(line.English) ? line.Japanese : line.English;
                    var text = LocalizedText.Pick(line.Chinese, english, line.Japanese);
                    gap = Mathf.Max(gap, GameSubtitle.DisplaySeconds(text, clip.length) - clip.length);
                }

                yield return new WaitForSecondsRealtime(gap);

                // 等上一句字幕真的收掉再开口（含打字机还没打完的情况，最多等 8 秒）。
                // 判据用字幕组件自己的状态，而不是我们估的时长。
                _chainStage = "等字幕收掉";
                var subtitleDeadline = Time.realtimeSinceStartup + 8f;
                while (_subtitle != null && _subtitle.IsShowing && Time.realtimeSinceStartup < subtitleDeadline)
                {
                    _lastChainTick = Time.realtimeSinceStartup;
                    yield return null;
                }

                firstLine = false;
            }
        }
        finally
        {
            _chainRunning = false;
            _chainStage = string.Empty;
            _chainStartedAt = 0f;

            // 讲完了：把她自己的自言自语还回去
            HeroineActionBridge.SuppressSelfTalk(false);

            // 不管中间怎么结束，都收拾干净：视线慢慢回正、表情复位
            HeroineActionBridge.EndLineReaction();
        }
    }

    /// <summary>
    /// 说话期间跟着音频开关口型。
    ///
    /// 原来是一条语音从头开到尾，台词中间有停顿的时候嘴还在动。
    /// 这里用离线算好的时间段（目录第 10 列）来管：说到哪一段就开嘴，空档就闭嘴。
    /// 没有分段信息的老语音包保持原来的行为（整条开着，结束前一点闭嘴）。
    /// </summary>
    /// <summary>
    /// 被"短的动作声音"（呼呼吹气、翻书嗯声…）掐断后：把这一句从头再放一遍，
    /// 然后接着往下讲。她**自己**开口那种情况不在这里处理（调用方直接让路）。
    /// </summary>
    private bool ResumeAfterShortCut(AudioClip clip, VoiceLine line, ref float progress, ref bool speaking)
    {
        if (clip == null || line == null)
            return false;

        var name = Path.GetFileNameWithoutExtension(line.File);
        if (string.IsNullOrEmpty(name))
            return false;

        var native = false;
        try
        {
            native = HeroineActionBridge.TryPlayNative(name, clip);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] 恢复播放失败: " + e.Message);
            return false;
        }

        if (!native)
            _source.PlayOneShot(clip);
        _playingNative = native;

        HeroineActionBridge.SetMouthTalk(true);
        Plugin.Log.LogInfo("[Chill Clock] 被短音打断，重放这一句: " + name);
        speaking = true;
        progress = 0f;
        return true;
    }

    private IEnumerator DriveMouth(AudioClip clip, VoiceLine line)
    {
        // 见下面 ResumeAfterShortCut：被短音打断时用它重放当前句
        var spans = line?.TalkSpans;
        // 小课堂这批是我们自己用 AudioSource 播的，游戏那边 Stop 不到它；
        // 所以**不要**再因为"游戏在说话/在演出"就判成被打断（那样等于自己把自己停了）。
        var independent = !string.IsNullOrEmpty(line?.Action);
        if (spans == null || spans.Length < 2)
        {
            // 老语音包没有分段信息：整条开着嘴。但中途被游戏掐了也要立刻收，
            // 不能傻等一整条放完（那样会出现"声音没了嘴还在动"）。
            var waited = 0f;
            var tail = Mathf.Max(0.1f, clip.length - MouthTailMargin);
            var tailDeadline = Time.realtimeSinceStartup + tail + 1f;
            var mouthResumes = 0;
            _chainStage = "口型（无分段）";
            while (waited < tail && Time.realtimeSinceStartup < tailDeadline)
            {
                if (IsInterrupted(clip, waited))
                {
                    // 只有"用户点了结束通话"这一种情况不重放：
                    // 那种时候我们的声音必须彻底停掉，重放会让嘴继续动。
                    // 其它打断（动作小声音、她开口）仍旧照老规矩 —— 短音重放、长音让路。
                    if (_noResumeAfterCut)
                        yield break;

                    if (GameVoiceRecent)
                    {
                        // 是她的台词（长句）还是动作自带的小声音（嗯声 / 翻页 / 呼呼）？
                        // 前者要让路，后者只该让我们把这一句重放一遍 —— 听它响多久再决定。
                        if (HeroineActionBridge.IsGameVoiceBusy())
                        {
                            var blipDeadline = Time.realtimeSinceStartup + ShortBlipSeconds;
                            while (HeroineActionBridge.IsGameVoiceBusy() &&
                                   Time.realtimeSinceStartup < blipDeadline)
                            {
                                _lastChainTick = Time.realtimeSinceStartup;
                                yield return null;
                            }

                            if (HeroineActionBridge.IsGameVoiceBusy() || mouthResumes >= MaxResumesPerLine)
                            {
                                // 她真的在说话（或者同一句被反复掐）：让路，这段不讲了
                                Interrupt();
                                yield break;
                            }
                        }

                        // 短音（呼呼吹气、翻书嗯声…）：把这句从头再说一遍，然后接着往下讲。
                        var mouthOn = true;
                        if (!ResumeAfterShortCut(clip, line, ref waited, ref mouthOn))
                        {
                            Interrupt();
                            yield break;
                        }

                        mouthResumes++;
                        tailDeadline = Time.realtimeSinceStartup + tail + 1f;
                    }
                    else if (!independent)
                    {
                        // 没抓到"游戏刚出声"，但我们的音频确实没了：按老办法收工
                        Interrupt();
                        yield break;
                    }
                }

                // 她起身开窗/关窗这类演出会把字幕框收走：我们这句还没播完就补回来
                if (line != null && _subtitle != null && !_subtitle.IsShowing &&
                    waited < clip.length - 0.3f)
                {
                    var en = string.IsNullOrEmpty(line.English) ? line.Japanese : line.English;
                    _subtitle.Show(LocalizedText.Pick(line.Chinese, en, line.Japanese),
                                   clip.length - waited);
                }

                waited += Time.unscaledDeltaTime;
                _lastChainTick = Time.realtimeSinceStartup;
                yield return null;
            }

            HeroineActionBridge.SetMouthTalk(false);
            yield break;
        }

        var elapsed = 0f;
        var speaking = true;
        var resumes = 0;
        var mouthDeadline = Time.realtimeSinceStartup + clip.length + 1f;
        _chainStage = "口型 " + line?.File;
        while (elapsed < clip.length && Time.realtimeSinceStartup < mouthDeadline)
        {
            if (IsInterrupted(clip, elapsed))
            {
                // 同上：只有"点了结束通话"之后不再重放
                if (_noResumeAfterCut)
                    yield break;

                if (GameVoiceRecent)
                {
                    // 同上：先看这条游戏语音有多长，短音重放这一句，长句让路。
                    if (HeroineActionBridge.IsGameVoiceBusy())
                    {
                        var blipDeadline = Time.realtimeSinceStartup + ShortBlipSeconds;
                        while (HeroineActionBridge.IsGameVoiceBusy() &&
                               Time.realtimeSinceStartup < blipDeadline)
                        {
                            _lastChainTick = Time.realtimeSinceStartup;
                            yield return null;
                        }

                        if (HeroineActionBridge.IsGameVoiceBusy() || resumes >= MaxResumesPerLine)
                        {
                            Interrupt();
                            yield break;
                        }
                    }

                    if (!ResumeAfterShortCut(clip, line, ref elapsed, ref speaking))
                    {
                        Interrupt();
                        yield break;
                    }

                    resumes++;
                    mouthDeadline = Time.realtimeSinceStartup + clip.length + 1f;
                }
                else if (!independent)
                {
                    // 没抓到"游戏刚出声"，但我们的音频确实没了：按老办法收工
                    Interrupt();
                    yield break;
                }
            }

            var shouldTalk = InSpans(spans, elapsed);
            if (shouldTalk != speaking)
            {
                speaking = shouldTalk;
                HeroineActionBridge.SetMouthTalk(speaking);
            }

            // 同上：她开窗/关窗把字幕收走了就补回来
            if (line != null && _subtitle != null && !_subtitle.IsShowing &&
                elapsed < clip.length - 0.3f)
            {
                var en = string.IsNullOrEmpty(line.English) ? line.Japanese : line.English;
                _subtitle.Show(LocalizedText.Pick(line.Chinese, en, line.Japanese),
                               clip.length - elapsed);
            }

            elapsed += Time.unscaledDeltaTime;
            _lastChainTick = Time.realtimeSinceStartup;
            yield return null;
        }

        if (speaking)
            HeroineActionBridge.SetMouthTalk(false);
    }

    /// <summary>
    /// 这句是不是该让路 / 已经废了。判两条：
    ///
    ///   1. 游戏自己开口了（剧情台词、野生动作的碎碎念……）：它一开口就先
    ///      VoiceManager.Stop()，我们那条借它的 player 播的语音会被一起掐掉，
    ///      继续动嘴只会没声音。
    ///   2. 我们那条音频确实已经没在响了，而按时间还没到结尾 —— 就是被掐的那一下。
    ///
    /// 第 2 条要跳过开头那零点几秒：刚 Play 的同一帧里 Unity 的 isPlaying 还没翻过来，
    /// 直接判会把每一句都当成"被掐了"。
    /// </summary>
    private bool IsInterrupted(AudioClip clip, float elapsed)
    {
        if (_abortRequested || IsGameVoiceBusySafe())
            return true;

        if (elapsed < 0.2f || elapsed >= clip.length - 0.25f)
            return false;

        var alive = _playingNative ? HeroineActionBridge.IsNativeVoicePlaying() : _source.isPlaying;
        return !alive;
    }

    private static bool InSpans(float[] spans, float t)
    {
        for (var i = 0; i + 1 < spans.Length; i += 2)
        {
            if (t >= spans[i] && t <= spans[i + 1])
                return true;
        }

        return false;
    }

    /// <summary>
    /// 播放一条语音。优先把音频交给游戏自己的语音系统（这样音量走游戏设置），
    /// 拿不到时才退回自己的 AudioSource。口型一律由我们自己开关。
    /// </summary>
    private void PlayLine(string fileName, AudioClip clip, bool firstLineOfChain)
    {
        var clipName = Path.GetFileNameWithoutExtension(fileName);
        var native = false;
        var isScenarioLine = _catalog.TryGetValue(fileName, out var scenarioLine) &&
                             !string.IsNullOrEmpty(scenarioLine.Action);

        // 小课堂这批（目录里配了动作的）**不走游戏的语音系统**：
        // 游戏自己每次播它的动作音效（喝咖啡"呼呼"、翻书）都会先 VoiceManager.Stop()，
        // 那一下会把挂在它上面的我们的音频一起掐掉。用我们自己的 AudioSource 播，
        // 它的 Stop() 就管不着了 —— 口型本来就是我们自己开关的，没影响。
        // 改回**用游戏自己的语音系统**播（用户要求）：
        // 这样音量/静音跟随游戏设置里的那一档，不会被我们自己的音量顶掉；
        // 代价是游戏一播它自己的东西（呼呼声、翻书声、她的台词）就会把我们掐断 ——
        // 掐断后的恢复逻辑见 PlayChain（短音恢复、她自己说话则不恢复）。
        try
        {
            native = HeroineActionBridge.TryPlayNative(clipName, clip);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] native voice failed: " + e.Message);
        }

        if (!native)
            _source.PlayOneShot(clip);

        _playingNative = native;

        // 我们这句要响多久：这段时间里，游戏"挂在动作上的小声音"（Motion_*）不播，
        // 免得她一边喝咖啡一边讲话。（Harmony 补丁读这个时间戳）
        var until = Time.realtimeSinceStartup + clip.length + 0.3f;
        if (until > UI.MotionVoiceSuppressPatch.SpeakingUntil)
            UI.MotionVoiceSuppressPatch.SpeakingUntil = until;

        // 口型由我们自己开关：VoiceManager.Play 不管这个，
        // 也不能走 HeroineVoiceController.PlayVoice（会把 _isFinishedVoice 卡死）。
        HeroineActionBridge.SetMouthTalk(true);

        if (!_catalog.TryGetValue(fileName, out var line))
            return;

        // 念台词时按游戏自己的规则来（她干活时不动身体、只转头；不在干活时只换表情），
        // 链子结束时统一把视线放回去。连播组只在第一句转头。
        // 目录里给这句配了动作的话，出手概率抬高一点，不然配了也基本看不到。
        var chance = _gestureChance;
        // 剧情式台词（目录里配了动作的）：她大部分时间是安静坐着说话的，
        // 只偶尔配一个小动作（像剧情/小课堂里那样，讲到有趣的地方笑一下），
        // 不该每句都换姿势 —— 聪音本来就是个内敛的人。
        // 提醒类池子照样是 0（那时候她该看着你说，不该换姿势）。
        if (!string.IsNullOrEmpty(line.Action) && chance > 0)
            chance = 30;
        HeroineActionBridge.Play(line.Emotion, _nextIsClick, firstLineOfChain, chance, line.Action);
        _nextIsClick = false;

        // 英文还没翻译完时，英语用户至少能看到日文原文，不至于空字幕
        var english = string.IsNullOrEmpty(line.English) ? line.Japanese : line.English;
        _subtitle.Show(LocalizedText.Pick(line.Chinese, english, line.Japanese), clip.length);
    }

    private AudioClip GetClip(string file)
    {
        if (!_clips.TryGetValue(file, out var clip))
            return null;

        // Unity 的"假 null"：对象被销毁后 != null 在 C# 层面仍为 true，
        // 但用起来就是空。以前这种条目会一直留在缓存里，导致这条语音**永远**取不到
        //（RequestClip 看见它在缓存里就直接返回）—— 点她没声音就是这么来的。
        if (clip == null)
        {
            _clips.Remove(file);
            _lru.Remove(file);
            return null;
        }

        return clip;
    }

    private void Store(string file, AudioClip clip)
    {
        _clips[file] = clip;
        _lru.Remove(file);
        _lru.Add(file);
        while (_lru.Count > MaxCachedClips)
        {
            var oldest = _lru[0];
            _lru.RemoveAt(0);
            if (_clips.TryGetValue(oldest, out var old) && old != null)
                UnityEngine.Object.Destroy(old);
            _clips.Remove(oldest);
        }
    }

    private void RequestClip(string file)
    {
        // 只要手里没有"能用的"音频，就去取 —— 不做"正在加载就跳过"的判重。
        //
        // 原因：这个判重状态一旦不同步（解码协程中途没了、缓存里是个空音频），
        // 这条语音会被**永久**跳过：点她没声音、走神不提醒，而且日志里什么都看不到。
        // 重复解码最多浪费一点 CPU，比"哑掉"划算得多。
        if (GetClip(file) != null)
            return;

        // 包被丢过（中途取消的退出会走到 Dispose）就重新加载一次，别让它一直哑着
        if (_packBytes == null)
        {
            Plugin.Log.LogWarning("[Chill Clock] 语音包之前被丢掉了，重新加载：" + file);
            LoadPack();
        }

        // 语音只从 DLL 内嵌的包里取（发布形态就一个 dll）。
        // 以前还支持 plugins\ChillClock\Voices.pack 和 plugins\ChillClock\Voices 两级回退，
        // 但用户那边根本不会有这两个东西，反而制造了"删了文件夹就没声音"的坑，已经去掉。
        if (_packBytes != null && _packNames.Contains(file))
        {
            _loading.Add(file);
            _loadingSince[file] = Time.realtimeSinceStartup;
            _runner.StartCoroutine(LoadOggFromPack(file));
            return;
        }

        // 兜底：DLL 里还嵌了最早那几十条 WAV，包读不到时才用得上
        var embedded = LoadEmbedded(file);
        if (embedded != null)
            Store(file, embedded);
    }

    /// <summary>从 Voices.pack 里取出这一条，落到临时文件再交给 Unity 解码。</summary>
    private IEnumerator LoadOggFromPack(string file)
    {
        string temp = null;
        try
        {
            Directory.CreateDirectory(_tempDir);
            temp = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".ogg");

            using (var archive = OpenArchive())
            {
                var entry = archive?.GetEntry(file);
                if (entry == null)
                    throw new FileNotFoundException("pack 里没有这一条");

                using var src = entry.Open();
                using var dst = File.Create(temp);
                src.CopyTo(dst);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] pack extract failed: " + file + " " + e);
            _loading.Remove(file);
            _loadingSince.Remove(file);
            TryDelete(temp);

            var fallback = LoadEmbedded(file);
            if (fallback != null)
                Store(file, fallback);
            yield break;
        }

        yield return LoadOgg(file, temp, true);
    }

    private IEnumerator LoadOgg(string file, string path, bool deleteAfterLoad)
    {
        // 解码走 UnityWebRequest（file:// 读临时文件）。
        // 用户报告过"语音一直不响"，日志显示卡在等解码 —— 也就是这个请求没有回调。
        // 所以这里改成 **带超时的等待**：超时/失败都写清楚日志，并且换一个目录重试一次。
        yield return TryDecode(file, path);
        var clip = _lastDecoded;

        if (clip == null && !string.IsNullOrEmpty(path))
        {
            // 换到游戏自己的数据目录再试一次（系统临时目录被安全软件盯上时的退路）
            // 注意：C# 不允许在带 catch 的 try 里 yield，所以复制和重试分成两步
            string retryPath = null;
            try
            {
                var retryDir = Path.Combine(Application.persistentDataPath ?? ".", "ChillClockVoice");
                Directory.CreateDirectory(retryDir);
                retryPath = Path.Combine(retryDir, Path.GetFileName(path));
                File.Copy(path, retryPath, true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Chill Clock] 换目录重试失败: " + e.Message);
                retryPath = null;
            }

            if (retryPath != null)
            {
                yield return TryDecode(file, retryPath);
                clip = _lastDecoded;
                TryDelete(retryPath);
            }
        }

        _loading.Remove(file);
        _loadingSince.Remove(file);

        if (clip != null)
        {
            Store(file, clip);
            if (deleteAfterLoad)
                TryDelete(path);
            yield break;
        }

        if (deleteAfterLoad)
            TryDelete(path);

        var embedded = LoadEmbedded(file);
        if (embedded != null)
            Store(file, embedded);
    }

    /// <summary>
    /// 真正解码一条 ogg：请求 + 超时等待。超时会 <c>Abort()</c> 并写日志，
    /// 不会再出现"协程一直等、外面什么都听不到"这种哑状态。
    /// </summary>
    private IEnumerator TryDecode(string file, string path)
    {
        _lastDecoded = null;
        var url = "file:///" + path.Replace('\\', '/');
        var request = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.OGGVORBIS);
        var operation = request.SendWebRequest();

        var deadline = Time.realtimeSinceStartup + DecodeTimeoutSeconds;
        while (!operation.isDone && Time.realtimeSinceStartup < deadline)
            yield return null;

        if (!operation.isDone)
        {
            Plugin.Log.LogWarning("[Chill Clock] 解码超时（" + DecodeTimeoutSeconds + " 秒）：" + file +
                                  " ← " + url);
            try
            {
                request.Abort();
            }
            catch
            {
                // ignore
            }

            yield break;
        }

        if (request.result == UnityWebRequest.Result.Success)
        {
            var clip = DownloadHandlerAudioClip.GetContent(request);
            request.Dispose();
            if (clip != null)
            {
                clip.name = file;
                if (!clip.LoadAudioData())
                    Plugin.Log.LogWarning("[Chill Clock] LoadAudioData failed: " + file);
                _lastDecoded = clip;
                yield break;
            }

            Plugin.Log.LogWarning("[Chill Clock] ogg decode null: " + file);
        }
        else
        {
            Plugin.Log.LogWarning("[Chill Clock] ogg load failed: " + file + " " + request.error + " ← " + url);
            request.Dispose();
        }

        yield break;
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 临时文件删不掉就算了，下次启动会覆盖
        }
    }

    private static AudioClip LoadEmbedded(string fileName)
    {
        try
        {
            var wav = fileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                ? fileName
                : fileName;
            var assembly = Assembly.GetExecutingAssembly();
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".Voices." + Path.GetFileNameWithoutExtension(wav) + ".wav", StringComparison.OrdinalIgnoreCase));
            if (resource == null)
                return null;

            using var stream = assembly.GetManifestResourceStream(resource);
            return stream == null ? null : LoadWave(stream, Path.GetFileName(resource));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] embedded voice failed: " + e.Message);
            return null;
        }
    }

    private void LoadCatalog()
    {
        try
        {
            if (ReadCatalogFromPack())
            {
                // 目录来自 DLL 内嵌的语音包（发布形态唯一来源）
            }
            else
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resource = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(".Voices.voice_catalog.tsv", StringComparison.OrdinalIgnoreCase));
                if (resource != null)
                {
                    using var stream = assembly.GetManifestResourceStream(resource);
                    using var reader = new StreamReader(stream);
                    var rows = new List<string>();
                    string row;
                    while ((row = reader.ReadLine()) != null)
                        rows.Add(row);
                    ParseCatalog(rows);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] voice catalog load failed: " + e.Message);
        }

        BuildPools();
        BuildChains();

        // 启动时把池子/联动组的规模写进日志：一眼看出"是不是池子空了"
        var summary = new List<string>();
        foreach (var pair in _pools)
            summary.Add(pair.Key + "=" + pair.Value.Count);
        summary.Sort(StringComparer.OrdinalIgnoreCase);
        Plugin.Log.LogInfo("[Chill Clock] 台词池：" + string.Join("  ", summary) +
                           "  联动组=" + _chains.Count);
    }

    private bool ReadCatalogFromPack()
    {
        if (_packBytes == null)
            return false;

        try
        {
            var rows = new List<string>();
            using (var archive = OpenArchive())
            {
                var entry = archive?.GetEntry("voice_catalog.tsv");
                if (entry == null)
                    return false;

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                string row;
                while ((row = reader.ReadLine()) != null)
                    rows.Add(row);
            }

            ParseCatalog(rows);
            Plugin.Log.LogInfo("[Chill Clock] voice catalog from pack: " + _catalog.Count + " lines");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] pack catalog failed: " + e);
            return false;
        }
    }

    private void ParseCatalog(IEnumerable<string> rows)
    {
        var first = true;
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row))
                continue;
            if (first)
            {
                first = false;
                continue;
            }

            var parts = row.Split('\t');
            if (parts.Length < 5)
                continue;

            var (hourFrom, hourTo) = ParseHour(parts.Length > 11 ? parts[11] : null);

            var line = new VoiceLine
            {
                File = parts[0].Trim(),
                Japanese = parts[1],
                Chinese = parts[2],
                English = parts[3],
                Emotion = parts[4].Trim(),
                Trigger = parts.Length > 5 ? parts[5].Trim() : string.Empty,
                SeqGroup = parts.Length > 6 ? parts[6].Trim() : string.Empty,
                SeqOrder = parts.Length > 7 && int.TryParse(parts[7].Trim(), out var order) ? order : 0,
                // 第 9 列（可选）：Morning / Noon / Evening / Night，留空表示任何时段都能用
                Time = parts.Length > 8 ? parts[8].Trim() : string.Empty,
                // 第 10 列（可选）：真正在出声的时间段，用来管口型
                TalkSpans = parts.Length > 9 ? ParseSpans(parts[9]) : null,
                // 第 11 列（可选）：节日 id，只有那天才会被选中
                Festival = parts.Length > 10 ? parts[10].Trim() : string.Empty,
                // 第 12 列（可选）：小时区间 "11-14"，留空表示不限
                HourFrom = hourFrom,
                HourTo = hourTo
            };
            // 第 13 列（可选）：这句台词配的动作（数字 = 动作 id，或 Happy / Think 这类键）
            if (parts.Length > 12)
                line.Action = parts[12].Trim();
            _catalog[line.File] = line;
        }
    }

    /// <summary>把 "11-14" 解析成 [11,14) 的起止小时；空值 / 格式不对返回 (-1,-1)。</summary>
    private static (int From, int To) ParseHour(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (-1, -1);

        var dash = value.IndexOf('-');
        if (dash <= 0)
            return (-1, -1);

        if (int.TryParse(value.Substring(0, dash).Trim(), out var from) &&
            int.TryParse(value.Substring(dash + 1).Trim(), out var to) &&
            from >= 0 && to <= 24 && to > from)
        {
            return (from, to);
        }

        return (-1, -1);
    }

    /// <summary>这条台词现在这个点能不能说（没写小时窗就都能说）。</summary>
    private static bool HourAllows(VoiceLine line, int hour)
    {
        if (line == null || line.HourFrom < 0)
            return true;

        return hour >= line.HourFrom && hour < line.HourTo;
    }

    /// <summary>
    /// 当前时段。规则抄的是游戏自己的 Bulbul.TimeOfDayProvider.GetCurrentTimeOfDayType()：
    /// 5 &lt; h &lt; 11 早上 / 11 &lt;= h &lt; 17 中午 / 17 &lt;= h &lt; 20 傍晚 / 其余晚上。
    /// （那个方法内部也是读 DateTime.Now.Hour，所以两边本来就该完全一致。）
    /// </summary>
    private static string PhaseFromClock(int hour)
    {
        if (hour > 5 && hour < 11)
            return "Morning";
        if (hour >= 11 && hour < 17)
            return "Noon";
        if (hour >= 17 && hour < 20)
            return "Evening";
        return "Night";
    }

    /// <summary>把 "0.08-1.24;1.62-3.05" 解析成 start,end,start,end… 的扁平数组。</summary>
    private static float[] ParseSpans(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var spans = new List<float>();
        foreach (var part in value.Split(';'))
        {
            var dash = part.IndexOf('-');
            if (dash <= 0)
                continue;

            if (float.TryParse(part.Substring(0, dash), NumberStyles.Float, CultureInfo.InvariantCulture, out var start) &&
                float.TryParse(part.Substring(dash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var end) &&
                end > start)
            {
                spans.Add(start);
                spans.Add(end);
            }
        }

        return spans.Count >= 2 ? spans.ToArray() : null;
    }

    private void BuildPools()
    {
        foreach (var line in _catalog.Values)
        {
            var trigger = string.IsNullOrEmpty(line.Trigger) ? GuessTrigger(line.File) : line.Trigger;
            if (string.IsNullOrEmpty(trigger))
                continue;

            // 联动组只把第一句放进候选池
            if (!string.IsNullOrEmpty(line.SeqGroup) && line.SeqOrder > 1)
                continue;

            // Trigger 可以写多个池子（用 | 分隔）：同一句既能在待机闲聊里出现，
            // 也能在休息闲聊 / 点击回应里出现。
            foreach (var one in trigger.Split('|'))
            {
                var key = one.Trim();
                if (key.Length == 0)
                    continue;
                if (!_pools.TryGetValue(key, out var pool))
                {
                    pool = new List<string>();
                    _pools[key] = pool;
                }
                pool.Add(line.File);
            }
        }

        // 启动时把每个池子有多少条写进日志：一眼就能看出"是不是池子空了"
    }

    private static string GuessTrigger(string file)
    {
        if (file.StartsWith("New_Rest", StringComparison.OrdinalIgnoreCase))
            return "Rest";
        if (file.StartsWith("New_TaskManager", StringComparison.OrdinalIgnoreCase))
            return "TaskManager";
        if (file.StartsWith("New_Exit", StringComparison.OrdinalIgnoreCase))
            return "Exit";
        return "Distraction";
    }

    private void BuildChains()
    {
        var groups = new Dictionary<string, List<VoiceLine>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in _catalog.Values)
        {
            if (string.IsNullOrEmpty(line.SeqGroup))
                continue;
            if (!groups.TryGetValue(line.SeqGroup, out var list))
            {
                list = new List<VoiceLine>();
                groups[line.SeqGroup] = list;
            }
            list.Add(line);
        }

        foreach (var group in groups.Values)
        {
            group.Sort((a, b) => a.SeqOrder.CompareTo(b.SeqOrder));
            if (group.Count <= 1)
                continue;

            var files = new List<string>(group.Count);
            foreach (var line in group)
                files.Add(line.File);
            _chains[files[0]] = files;
        }
    }

    private static AudioClip LoadWave(Stream stream, string clipName)
    {
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF")
            return null;
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
            return null;

        var channels = 1;
        var sampleRate = 32000;
        var bitsPerSample = 16;
        byte[] data = null;

        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();
            var chunkEnd = reader.BaseStream.Position + chunkSize;

            if (chunkId == "fmt ")
            {
                var audioFormat = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();
                if (audioFormat != 1 || bitsPerSample != 16)
                    return null;
            }
            else if (chunkId == "data")
            {
                data = reader.ReadBytes(chunkSize);
            }

            if (reader.BaseStream.Position < chunkEnd)
                reader.BaseStream.Position = chunkEnd;
            if ((chunkSize & 1) != 0 && reader.BaseStream.Position < reader.BaseStream.Length)
                reader.BaseStream.Position++;
        }

        if (data == null || data.Length < 2 || channels <= 0)
            return null;

        var sampleCount = data.Length / 2;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
            samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;

        var clip = AudioClip.Create(clipName, sampleCount / channels, channels, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private sealed class VoiceLine
    {
        public string File;
        public string Japanese;
        public string Chinese;
        public string English;
        public string Emotion;
        public string Trigger;
        public string SeqGroup;
        public int SeqOrder;
        public string Time;
        public string Action;

        /// <summary>
        /// 节日 id（目录第 11 列，可选）。空表示平时都能用；
        /// 非空时只有 FestivalCalendar 报出同一个 id 的那天才会被选中。
        /// </summary>
        public string Festival;

        /// <summary>
        /// 小时窗（目录第 12 列，可选，形如 "11-14" 表示 11:00-13:59）。
        ///
        /// 游戏自己的时段只有 Morning / Noon / Evening / Night 四段，Noon 覆盖
        /// 11:00-16:59，午饭/午休这类台词挂在 Noon 上会一直说到下午四五点。
        /// 这一列用来把这种台词收窄到真正说得通的时段；-1 表示不限。
        /// </summary>
        public int HourFrom = -1;
        public int HourTo = -1;

        /// <summary>
        /// 这条语音"真正在出声"的时间段，扁平存成 start,end,start,end…（秒）。
        /// 由 tools/analyze-speech-spans.py 离线算好写进目录的第 10 列。
        /// 空表示没有分段信息，口型按整条处理。
        /// </summary>
        public float[] TalkSpans;
    }
}

/// <summary>
/// 语音播放需要一个 MonoBehaviour 来跑协程。
/// </summary>
internal sealed class VoiceRunner : MonoBehaviour
{
}
