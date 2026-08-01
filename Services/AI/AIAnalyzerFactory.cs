using WoWSBattleAssistant.Models;

namespace WoWSBattleAssistant.Services.AI;

/// <summary>根据配置创建对应的 AI 分析器</summary>
public static class AIAnalyzerFactory
{
    public static IAIBattleAnalyzer Create(AppSettings settings)
    {
        return settings.AiProvider switch
        {
            AiProvider.Glm => new GlmBattleAnalyzer(settings.GlmApiKey, settings.GlmModel),
            AiProvider.Qwen => new QwenVlBattleAnalyzer(settings.QwenApiKey, settings.QwenModel),
            _ => throw new ArgumentOutOfRangeException(nameof(settings.AiProvider))
        };
    }

    /// <summary>列出各提供方可选模型</summary>
    public static readonly string[] GlmModels = { "glm-4v", "glm-4v-plus" };
    public static readonly string[] QwenModels = { "qwen-vl-plus", "qwen-vl-max" };
}
