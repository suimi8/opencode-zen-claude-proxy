import test from "node:test";
import assert from "node:assert/strict";

import {
  anthropicRequestToOpenAiRequest,
  buildAnthropicMessageFromOpenAiChoice,
  estimateAnthropicInputTokens,
} from "../src/anthropic-openai-proxy.js";

test("converts anthropic tool definitions and conversation history to OpenAI chat format", () => {
  const request = {
    model: "claude-code-proxy",
    system: "You are a test assistant.",
    tools: [
      {
        name: "read_file",
        description: "Read a file",
        input_schema: {
          type: "object",
          properties: {
            path: { type: "string" },
          },
          required: ["path"],
        },
      },
    ],
    messages: [
      {
        role: "user",
        content: [{ type: "text", text: "Read README.md" }],
      },
      {
        role: "assistant",
        content: [
          { type: "text", text: "I'll read that now." },
          {
            type: "tool_use",
            id: "toolu_123",
            name: "read_file",
            input: { path: "README.md" },
          },
        ],
      },
      {
        role: "user",
        content: [
          {
            type: "tool_result",
            tool_use_id: "toolu_123",
            content: "Project documentation",
          },
        ],
      },
    ],
  };

  const openAi = anthropicRequestToOpenAiRequest(request, {
    upstreamModel: "deepseek-v4-flash-free",
    deepseekThinkingType: "enabled",
    upstreamReasoningEffort: "xhigh",
  });

  assert.equal(openAi.model, "deepseek-v4-flash-free");
  assert.deepEqual(openAi.thinking, { type: "enabled" });
  assert.equal(openAi.reasoning_effort, "xhigh");
  assert.deepEqual(openAi.tools, [
    {
      type: "function",
      function: {
        name: "read_file",
        description: "Read a file",
        parameters: {
          type: "object",
          properties: {
            path: { type: "string" },
          },
          required: ["path"],
        },
      },
    },
  ]);
  assert.deepEqual(openAi.messages, [
    { role: "system", content: "You are a test assistant." },
    { role: "user", content: "Read README.md" },
    {
      role: "assistant",
      content: "I'll read that now.",
      tool_calls: [
        {
          id: "toolu_123",
          type: "function",
          function: {
            name: "read_file",
            arguments: "{\"path\":\"README.md\"}",
          },
        },
      ],
    },
    {
      role: "tool",
      tool_call_id: "toolu_123",
      content: "Project documentation",
    },
  ]);
});

test("converts OpenAI tool calls back to Anthropic tool_use blocks", () => {
  const anthropic = buildAnthropicMessageFromOpenAiChoice(
    {
      finish_reason: "tool_calls",
      message: {
        reasoning_content: "I should call the file reader first.",
        content: "Let me inspect that.",
        tool_calls: [
          {
            id: "call_1",
            type: "function",
            function: {
              name: "read_file",
              arguments: "{\"path\":\"README.md\"}",
            },
          },
        ],
      },
    },
    {
      model: "deepseek-v4-flash-free",
      usage: {
        prompt_tokens: 10,
        completion_tokens: 4,
      },
    },
  );

  assert.equal(anthropic.stop_reason, "tool_use");
  assert.equal(anthropic.usage.input_tokens, 10);
  assert.equal(anthropic.usage.output_tokens, 4);
  assert.deepEqual(anthropic.content, [
    {
      type: "thinking",
      thinking: "I should call the file reader first.",
      signature: "proxy-unverified",
    },
    { type: "text", text: "Let me inspect that." },
    {
      type: "tool_use",
      id: "call_1",
      name: "read_file",
      input: { path: "README.md" },
    },
  ]);
});

test("passes anthropic thinking blocks back to upstream reasoning_content", () => {
  const openAi = anthropicRequestToOpenAiRequest(
    {
      model: "claude-code-proxy",
      messages: [
        {
          role: "assistant",
          content: [
            {
              type: "thinking",
              thinking: "Need to preserve reasoning state across tool turns.",
              signature: "proxy-unverified",
            },
            {
              type: "tool_use",
              id: "call_1",
              name: "echo_args",
              input: { city: "Kathmandu" },
            },
          ],
        },
      ],
    },
    {
      upstreamModel: "deepseek-v4-flash-free",
      deepseekThinkingType: "enabled",
      upstreamReasoningEffort: "xhigh",
    },
  );

  assert.equal(
    openAi.messages[0].reasoning_content,
    "Need to preserve reasoning state across tool turns.",
  );
});

test("maps anthropic effort values onto Zen reasoning_effort", () => {
  const xhighRequest = anthropicRequestToOpenAiRequest(
    {
      model: "claude-code-proxy",
      output_config: { effort: "xhigh" },
      messages: [{ role: "user", content: "hello" }],
    },
    {
      upstreamModel: "deepseek-v4-flash-free",
      deepseekThinkingType: "enabled",
      upstreamReasoningEffort: "high",
    },
  );

  const mediumRequest = anthropicRequestToOpenAiRequest(
    {
      model: "claude-code-proxy",
      output_config: { effort: "medium" },
      messages: [{ role: "user", content: "hello" }],
    },
    {
      upstreamModel: "deepseek-v4-flash-free",
      deepseekThinkingType: "enabled",
      upstreamReasoningEffort: "xhigh",
    },
  );

  assert.equal(xhighRequest.reasoning_effort, "xhigh");
  assert.equal(mediumRequest.reasoning_effort, "medium");
});

test("does not send DeepSeek-only thinking option to Mimo models", () => {
  const openAi = anthropicRequestToOpenAiRequest(
    {
      model: "claude-code-proxy",
      messages: [{ role: "user", content: "hello" }],
    },
    {
      upstreamModel: "mimo-v2.5-free",
      deepseekThinkingType: "enabled",
      upstreamReasoningEffort: "xhigh",
    },
  );

  assert.equal(openAi.model, "mimo-v2.5-free");
  assert.equal(openAi.thinking, undefined);
  assert.equal(openAi.reasoning_effort, "xhigh");
});

test("estimates input tokens without an upstream tokenizer", () => {
  const count = estimateAnthropicInputTokens({
    system: "You are helpful",
    messages: [{ role: "user", content: "Hello there" }],
  });

  assert.equal(typeof count, "number");
  assert.ok(count > 0);
});
