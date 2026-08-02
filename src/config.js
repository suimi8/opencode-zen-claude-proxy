export function loadConfig(env = process.env) {
  const port = Number.parseInt(env.PORT ?? "4040", 10);
  const host = env.HOST ?? "127.0.0.1";

  return {
    host,
    port: Number.isFinite(port) ? port : 4040,
    upstreamApiKey: env.UPSTREAM_API_KEY ?? env.ANTHROPIC_API_KEY ?? "",
    upstreamModel: env.UPSTREAM_MODEL ?? env.ANTHROPIC_MODEL ?? "deepseek-v4-flash-free",
    upstreamChatCompletionsUrl:
      env.UPSTREAM_CHAT_COMPLETIONS_URL ??
      "https://opencode.ai/zen/v1/chat/completions",
    anthropicModelAlias: env.ANTHROPIC_MODEL_ALIAS ?? "claude-code-proxy",
    proxyApiKey: env.PROXY_API_KEY ?? "",
    deepseekThinkingType: env.DEEPSEEK_THINKING_TYPE ?? "enabled",
    upstreamReasoningEffort:
      env.UPSTREAM_REASONING_EFFORT ?? env.DEEPSEEK_REASONING_EFFORT ?? "xhigh",
  };
}
