using System;
using System.Globalization;
using System.Linq;
using System.Speech.Recognition;
using System.Threading.Tasks;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 一次性麦克风/语音识别测试：打开引擎 5 秒，能识别到任意文字就算成功。
/// 用于在设置面板里给用户做"我的麦克风能用吗"的快速自检。
/// </summary>
public sealed class VoiceTestRunner : IDisposable
{
    private SpeechRecognitionEngine? _engine;
    private TaskCompletionSource<string>? _tcs;

    public async Task<string> RunAsync(TimeSpan listenFor)
    {
        _tcs = new TaskCompletionSource<string>();

        var culture = new CultureInfo("zh-CN");
        var info = SpeechRecognitionEngine.InstalledRecognizers()
            .FirstOrDefault(r => r.Culture.Equals(culture));
        if (info == null)
            throw new InvalidOperationException("未找到 zh-CN 语音识别引擎。请先安装中文语音包。");

        _engine = new SpeechRecognitionEngine(info);
        _engine.SpeechRecognized += (_, e) =>
        {
            // 取第一个识别结果即可，不做置信度过滤（测试时尽量宽松）
            if (e.Result != null && !string.IsNullOrWhiteSpace(e.Result.Text))
                _tcs?.TrySetResult(e.Result.Text);
        };
        // 用 dictation（自由说）模式，不强制语法
        _engine.LoadGrammar(new DictationGrammar());

        try
        {
            _engine.SetInputToDefaultAudioDevice();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法访问默认麦克风，请检查 Windows 麦克风权限设置。\n底层错误: {ex.Message}", ex);
        }

        _engine.RecognizeAsync(RecognizeMode.Multiple);
        // listenFor 秒后停止识别
        var delay = Task.Delay(listenFor);
        var finished = await Task.WhenAny(_tcs.Task, delay);
        try { _engine.RecognizeAsyncStop(); } catch { }

        if (finished == _tcs.Task)
            return await _tcs.Task;
        return ""; // 超时
    }

    public void Dispose()
    {
        try { _engine?.Dispose(); } catch { }
        _engine = null;
        _tcs = null;
    }
}