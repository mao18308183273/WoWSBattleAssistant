using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace WoWSBattleAssistant.Services;

public sealed class VoiceController : IDisposable
{
    private SpeechRecognitionEngine? _engine;
    private readonly Dispatcher _dispatcher;
    private double _confidenceThreshold;
    private bool _userRequestedStop;
    private int _restartCount;
    private const int MaxRestarts = 3;

    public event Action<string, double>? CommandRecognized;
    public event Action<string>? StatusChanged;
    public bool IsRunning => _engine != null;

    public VoiceController(Dispatcher dispatcher, double confidenceThreshold = 0.5)
    {
        _dispatcher = dispatcher;
        _confidenceThreshold = confidenceThreshold;
    }

    /// <summary>运行时更新置信度阈值，无需重启引擎</summary>
    public void UpdateConfidenceThreshold(double threshold)
    {
        _confidenceThreshold = threshold;
    }

    public void Start()
    {
        if (_engine != null) return;

        try
        {
            _userRequestedStop = false;
            var culture = new CultureInfo("zh-CN");

            // 列出所有可用识别器（诊断）
            var all = SpeechRecognitionEngine.InstalledRecognizers().ToList();
            AppLog.Info($"可用语音识别器: {all.Count} 个");
            foreach (var r in all) AppLog.Info($"  [{r.Culture.Name}] {r.Name}");

            var info = all.FirstOrDefault(r => r.Culture.Equals(culture));
            if (info != null)
            {
                _engine = new SpeechRecognitionEngine(info);
                AppLog.Info($"zh-CN 引擎: {info.Name}");
            }
            else
            {
                AppLog.Warn("无 zh-CN，尝试默认引擎");
                try { _engine = new SpeechRecognitionEngine(); }
                catch (Exception ex)
                {
                    AppLog.Error($"默认引擎创建失败: {ex.Message}");
                    StatusChanged?.Invoke("未找到语音识别引擎");
                    return;
                }
                AppLog.Info($"默认引擎: {_engine.RecognizerInfo?.Name ?? "?"}");
            }

            _engine.SpeechRecognized += OnSpeechRecognized;
            _engine.SpeechRecognitionRejected += (_, args) =>
            {
                var txt = args.Result?.Text ?? "(null)";
                var alts = args.Result?.Alternates;
                var altStr = alts != null && alts.Count > 0
                    ? string.Join(" | ", alts.Select(a => $"\"{a.Text}\" cf={a.Confidence:0.00}"))
                    : "无";
                AppLog.Info($"语音被拒: \"{txt}\" cf={args.Result?.Confidence:0.00} 候选: [{altStr}]");
            };
            _engine.RecognizeCompleted += OnRecognizeCompleted;
            _engine.AudioStateChanged += (_, args) =>
            {
                AppLog.Info($"音频状态: {args.AudioState}");
                if (args.AudioState == AudioState.Stopped && !_userRequestedStop)
                {
                    AppLog.Warn("音频意外停止，尝试重启");
                    Task.Delay(2000).ContinueWith(_ => TryRestart());
                }
            };

            // 加载语法（失败不影响启动）
            try
            {
                var cmds = new Choices();
                cmds.Add("截图","截取小地图","截小地图","截","分析","开始分析","分析战局",
                    "清空","重置","自动模式","手动模式","精简模式","完整模式","迷你模式","简洁模式",
                    "最小化","最小化窗口","最小化悬浮窗","恢复","恢复窗口","还原窗口",
                    "战力浮窗","战力悬浮窗","打开战力","显示战力","关闭战力","隐藏战力","收起战力",
                    "详细","看详细","展开详情","简略","看简略","收起详情",
                    "复制","复制结果","发送","发送消息","设置","打开设置","关闭设置");
                _engine.LoadGrammar(new Grammar(new GrammarBuilder(cmds) { Culture = culture }));
                AppLog.Info("语法已加载");
            }
            catch (Exception ex) { AppLog.Error($"语法加载失败: {ex.Message}"); }

            // 设置音频输入
            try { _engine.SetInputToDefaultAudioDevice(); AppLog.Info("已设置默认麦克风"); }
            catch (Exception ex) { AppLog.Error($"麦克风失败: {ex.Message}"); try { _engine.SetInputToNull(); } catch { } }

            // 启动识别（不超时，持续监听）
            _engine.BabbleTimeout = TimeSpan.FromSeconds(0);
            _engine.EndSilenceTimeout = TimeSpan.FromSeconds(0);
            _engine.InitialSilenceTimeout = TimeSpan.FromSeconds(0);
            _engine.RecognizeAsync(RecognizeMode.Multiple);
            _restartCount = 0;
            StatusChanged?.Invoke("语音已启动");
            AppLog.Info("语音识别启动完成");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"语音启动失败: {ex.Message}");
            AppLog.Error("语音启动异常", ex);
            _engine?.Dispose(); _engine = null;
        }
    }

    public void Stop() { if (_engine == null) return; try { _userRequestedStop = true; _engine.RecognizeAsyncCancel(); _engine.SetInputToNull(); } catch { } }
    public void Dispose() { Stop(); _engine?.Dispose(); _engine = null; }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs args)
    {
        var text = args.Result?.Text ?? "";
        var conf = args.Result?.Confidence ?? 0;
        AppLog.Info($"语音识别: \"{text}\" conf={conf:0.00} sem={args.Result?.Semantics.Value}");

        if (string.IsNullOrWhiteSpace(text) || conf < _confidenceThreshold)
        {
            AppLog.Info($"  跳过(置信度过低/空白)");
            return;
        }
        var cmd = MapToCommand(text);
        if (cmd != null)
        {
            AppLog.Info($"  匹配指令: {cmd}");
            var handler = CommandRecognized;
            if (handler == null) { AppLog.Warn("  无指令订阅者！事件丢失"); return; }
            _dispatcher.Invoke(() =>
            {
                try { handler(cmd, conf); }
                catch (Exception ex) { AppLog.Error($"指令执行异常: {ex.Message}", ex); }
            });
        }
        else AppLog.Info($"  未匹配指令");
    }

    private static string? MapToCommand(string t)
    {
        t = t.Replace(" ", "").Trim();
        return t switch
        {
            "截图" or "截取小地图" or "截小地图" or "截" => "截小地图",
            "分析" or "开始分析" or "分析战局" => "分析",
            "清空" or "重置" => "清空",
            "自动模式" => "切自动", "手动模式" => "切手动",
            "精简模式" or "完整模式" or "迷你模式" or "简洁模式" => t.Replace("模式", ""),
            "精简" or "完整" or "迷你" or "简洁" => t,
            "最小化" or "最小化窗口" or "最小化悬浮窗" => "最小化",
            "恢复" or "恢复窗口" or "还原窗口" => "恢复",
            "战力浮窗" or "战力悬浮窗" or "打开战力" or "显示战力" => "开战力",
            "关闭战力" or "隐藏战力" or "收起战力" => "关战力",
            "详细" or "看详细" or "展开详情" => "看详细",
            "简略" or "看简略" or "收起详情" => "看简略",
            "复制" or "复制结果" => "复制",
            "发送" or "发送消息" => "发送",
            "设置" or "打开设置" => "设置", "关闭设置" => "关闭设置",
            _ => null
        };
    }

    public static string? GetInstalledRecognizerInfo()
    {
        try { return SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.Name == "zh-CN")?.Name; }
        catch (Exception ex) { AppLog.Warn($"获取语音识别器信息失败: {ex.Message}"); return null; }
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs args)
    {
        var err = args.Error;
        _engine?.Dispose(); _engine = null;
        if (err != null)
        {
            AppLog.Error($"语音异常: {err.GetType().Name}: {err.Message}", err);
            StatusChanged?.Invoke($"语音错误: {err.Message}");
            if (err is InvalidOperationException) { AppLog.Warn("内部错误，已禁用语音"); return; }
            if (!_userRequestedStop) TryRestart();
        }
        else if (!_userRequestedStop) TryRestart();
    }

    private void TryRestart()
    {
        if (_restartCount >= MaxRestarts) { AppLog.Warn("重启次数用尽"); return; }
        _restartCount++;
        Task.Delay(3000).ContinueWith(_ =>
        {
            try { Start(); if (_engine != null) _restartCount = 0; }
            catch (Exception ex) { AppLog.Error($"重启异常: {ex.Message}"); }
        });
    }
}
