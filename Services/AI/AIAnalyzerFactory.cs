using System.Threading.Tasks;
using WoWSBattleAssistant.Models;

namespace WoWSBattleAssistant.Services.AI;

/// <summary>根据配置创建对应的 AI 分析器</summary>
public static class AIAnalyzerFactory
{
    /// <summary>异步创建分析器。DeepSeek 分支可能需要在网络自动获取 Token，必须异步等待，
    /// 避免在 UI 线程用 .Result 同步阻塞造成死锁（sync-over-async）。</summary>
    public static async Task<IAIBattleAnalyzer> CreateAsync(AppSettings settings)
    {
        return settings.AiProvider switch
        {
            AiProvider.Glm => new GlmBattleAnalyzer(settings.GlmApiKey, settings.GlmModel),
            AiProvider.Qwen => new QwenVlBattleAnalyzer(settings.QwenApiKey, settings.QwenModel),
            AiProvider.DeepSeek => await CreateDeepSeekAsync(settings).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(settings.AiProvider))
        };
    }

    private static async Task<DeepSeek.DeepSeekVisionAnalyzer> CreateDeepSeekAsync(AppSettings settings)
    {
        var token = settings.DeepSeekToken;
        var cookie = settings.DeepSeekCookie;

        // 如果 Token 为空但 Cookie 不为空，尝试自动获取 Token
        if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(cookie))
        {
            try
            {
                var fetched = await DeepSeek.DeepSeekVisionAnalyzer
                    .TryFetchTokenAsync(cookie)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(fetched))
                {
                    token = fetched;
                    // 回写到 settings，下次无需再次获取
                    settings.DeepSeekToken = token;
                    AppLog.Info("已从 Cookie 自动获取 DeepSeek Token。");
                }
            }
            catch { /* 自动获取失败不影响后续流程 */ }
        }

        return new DeepSeek.DeepSeekVisionAnalyzer(token, cookie, settings.EnableDeepSeekThinking);
    }

    /// <summary>列出各提供方可选模型</summary>
    public static readonly string[] GlmModels = { "glm-4v", "glm-4v-plus" };
    public static readonly string[] QwenModels = { "qwen-vl-plus", "qwen-vl-max" };
}
