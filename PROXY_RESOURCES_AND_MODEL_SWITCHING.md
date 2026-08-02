# Proxy Resources And Model Switching

This document lists the main resources used while building the Claude Code -> Zen proxy in this workspace, and explains how to reuse the proxy with other upstream models.

Last updated: 2026-05-14 (Asia/Kathmandu)

## Main Resources Used

### Anthropic / Claude Code

- Claude Code settings
  - https://docs.anthropic.com/en/docs/claude-code/settings
  - Used to understand Claude Code env/config behavior and model settings.

- Claude Code LLM gateway
  - https://docs.anthropic.com/en/docs/claude-code/llm-gateway
  - Used to shape the Anthropic-compatible proxy endpoints for Claude Code.

- Anthropic tool use overview
  - https://docs.anthropic.com/en/docs/agents-and-tools/tool-use/overview
  - Used to understand tool call and tool result semantics.

- Anthropic extended thinking
  - https://docs.anthropic.com/en/docs/build-with-claude/extended-thinking
  - Used when mapping reasoning/thinking state across turns.

### OpenAI-compatible request format

- OpenAI chat completions reference
  - https://platform.openai.com/docs/api-reference/chat/create-chat-completion
  - Used for the upstream request shape that Zen exposes for many non-Claude models.

### DeepSeek

- DeepSeek thinking mode
  - https://api-docs.deepseek.com/guides/thinking_mode
  - Used for `thinking` and `reasoning_effort` support.

- DeepSeek pricing / model quick start
  - https://api-docs.deepseek.com/quick_start/pricing/
  - Used to confirm current context window guidance and model family details.

- DeepSeek V4 release/news
  - https://api-docs.deepseek.com/news/news260424
  - Used as supporting context for the current V4 family behavior and capabilities.

### MiniMax

- MiniMax OpenAI-compatible text API
  - https://platform.minimax.io/docs/api-reference/text-openai-api
  - Used to reason about MiniMax compatibility as a future fallback model.

- MiniMax model list
  - https://platform.minimax.io/docs/api-reference/models/openai/list-models
  - Used to confirm current model naming and discovery.

### OpenCode Zen

- Live model listing endpoint used during verification
  - https://opencode.ai/zen/v1/models
  - Used to verify currently published model IDs.

- Live OpenAI-compatible endpoint used by the proxy
  - https://opencode.ai/zen/v1/chat/completions
  - Used as the upstream target for the proxy.

## Local Files In This Workspace

- Proxy server
  - [src/server.js](/Users/owner/Desktop/project_social_media/zen/src/server.js)

- Anthropic <-> OpenAI translation logic
  - [src/anthropic-openai-proxy.js](/Users/owner/Desktop/project_social_media/zen/src/anthropic-openai-proxy.js)

- Config loader
  - [src/config.js](/Users/owner/Desktop/project_social_media/zen/src/config.js)

- Main README
  - [README.md](/Users/owner/Desktop/project_social_media/zen/README.md)

- Zen-only Claude settings
  - [zen-claude-settings.json](/Users/owner/Desktop/project_social_media/zen/zen-claude-settings.json)

- Zen wrapper command
  - [claude-zen.sh](/Users/owner/Desktop/project_social_media/zen/claude-zen.sh)

- Local env files
  - [.env.local](/Users/owner/Desktop/project_social_media/zen/.env.local)
  - [.env.zen](/Users/owner/Desktop/project_social_media/zen/.env.zen)

- Tests
  - [test/translation.test.js](/Users/owner/Desktop/project_social_media/zen/test/translation.test.js)
  - [test/server.test.js](/Users/owner/Desktop/project_social_media/zen/test/server.test.js)

## Current Proxy Architecture

Claude Code sends Anthropic-style requests to the local proxy:

- `POST /v1/messages`
- `POST /v1/messages/count_tokens`
- `GET /v1/models`
- `GET /v1/models/:id`

The proxy translates those requests into OpenAI-style `chat/completions` requests for the upstream provider.

The current upstream flow is:

1. Claude Code -> local proxy (Anthropic-style)
2. local proxy -> Zen `chat/completions` (OpenAI-style)
3. proxy converts tool calls, thinking, streaming events, and tool results back into Anthropic-style output for Claude Code

## How To Use Another Model In The Proxy

At the simplest level, switching models means changing the upstream model ID in the env file used by the proxy.

### Option 1: Change only the model name

Edit one of these:

- [.env.local](/Users/owner/Desktop/project_social_media/zen/.env.local)
- [.env.zen](/Users/owner/Desktop/project_social_media/zen/.env.zen)

Change:

```bash
UPSTREAM_MODEL=deepseek-v4-flash-free
```

to another published Zen model, for example:

```bash
UPSTREAM_MODEL=minimax-m2.5-free
```

Then restart the relevant proxy command:

```bash
./start-proxy.sh
```

or:

```bash
claude-zen
```

### Option 2: Change both model and upstream endpoint

If another provider or model family needs a different upstream endpoint, change:

```bash
UPSTREAM_CHAT_COMPLETIONS_URL=https://opencode.ai/zen/v1/chat/completions
```

This is only safe if the new endpoint still accepts OpenAI-style `chat/completions`.

## Compatibility Checklist Before Switching Models

Not every model can be swapped by name only. Check these areas:

### 1. Tool calling

The model/provider should support OpenAI-style tools or function calling.

What to verify:

- assistant tool calls are returned in `tool_calls`
- tool arguments are returned as JSON or JSON-like strings
- tool result follow-up turns are accepted

### 2. Reasoning / thinking format

Different providers represent reasoning differently.

Examples:

- DeepSeek uses `thinking` and `reasoning_content`
- MiniMax documents `reasoning_split` / `reasoning_details`

If the provider requires its reasoning state to be preserved across tool turns, the proxy may need a provider-specific adapter.

### 3. Content block compatibility

Claude Code may produce richer Anthropic-style structures.

Potential problem areas:

- `tool_reference`
- `thinking`
- mixed multimodal content

If the upstream provider only accepts `text`, `image_url`, or `video_url`, the proxy must normalize or strip unsupported block types before forwarding.

### 4. Streaming behavior

The provider should support streaming responses in a way that can be mapped to Anthropic SSE.

What to verify:

- partial text deltas
- partial tool call argument deltas
- final stop reason

### 5. Context window and output limits

Claude Code's config may need to be updated separately from the upstream model capability.

Examples:

- `context_window`
- `max_tokens`

The upstream model limit and Claude Code's local budgeting config are related, but not the same thing.

## What To Change For MiniMax Specifically

If you want to try MiniMax through this proxy, start with:

```bash
UPSTREAM_MODEL=minimax-m2.5-free
```

Then check whether these still work:

1. plain non-streaming prompt
2. streaming prompt
3. one tool call
4. tool result follow-up turn

If there is a failure, the most likely causes are:

- unsupported Anthropic-style content block types
- provider-specific reasoning format mismatch
- function/tool calling differences

## Recommended Safe Method To Add Another Model

Use this process:

1. Confirm the model is published in `https://opencode.ai/zen/v1/models`
2. Change `UPSTREAM_MODEL` in `.env.zen` or `.env.local`
3. Restart the proxy
4. Run a plain prompt check
5. Run a tool call check
6. Run a tool result follow-up check
7. If needed, add provider-specific translation code in [src/anthropic-openai-proxy.js](/Users/owner/Desktop/project_social_media/zen/src/anthropic-openai-proxy.js)

## Example Model Switch

DeepSeek to MiniMax:

```bash
# /Users/owner/Desktop/project_social_media/zen/.env.zen
UPSTREAM_MODEL=minimax-m2.5-free
```

Then run:

```bash
claude-zen --print "Reply with exactly: model switch ok"
```

If plain chat works, test tool usage next.

## Notes

- The current proxy is strongest with the DeepSeek path because it already preserves DeepSeek reasoning state across tool turns.
- MiniMax can be used, but may need an extra adapter if its reasoning or tool-call behavior differs in practice.
- Keeping the proxy in front of Claude Code is the safest way to avoid upstream content-format errors like unsupported `tool_reference` blocks.
