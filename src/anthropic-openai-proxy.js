import crypto from "node:crypto";

function safeJsonParse(text, fallback) {
  try {
    return JSON.parse(text);
  } catch {
    return fallback;
  }
}

function normalizeTextParts(parts) {
  return parts
    .map((part) => {
      if (typeof part === "string") {
        return part;
      }

      if (!part || typeof part !== "object") {
        return "";
      }

      if (part.type === "text") {
        return part.text ?? "";
      }

      if (part.type === "tool_result") {
        return "";
      }

      if (part.type === "thinking" || part.type === "redacted_thinking") {
        return "";
      }

      return "";
    })
    .filter(Boolean);
}

function normalizeToolResultContent(content) {
  if (typeof content === "string") {
    return content;
  }

  if (Array.isArray(content)) {
    const text = normalizeTextParts(content).join("\n");
    return text || JSON.stringify(content);
  }

  if (content == null) {
    return "";
  }

  return JSON.stringify(content);
}

function mapEffortToZen(effort, fallback) {
  switch (effort) {
    case "none":
    case "minimal":
    case "low":
    case "medium":
    case "high":
    case "xhigh":
      return effort;
    case "max":
      return "xhigh";
    default:
      return fallback;
  }
}

function supportsDeepSeekThinking(model) {
  return typeof model === "string" && model.startsWith("deepseek-");
}

function contentToBlocks(content) {
  if (Array.isArray(content)) {
    return content;
  }

  if (typeof content === "string") {
    return [{ type: "text", text: content }];
  }

  return [];
}

export function anthropicRequestToOpenAiRequest(requestBody, options) {
  const openAiMessages = [];
  const systemParts = Array.isArray(requestBody.system)
    ? normalizeTextParts(requestBody.system)
    : typeof requestBody.system === "string"
      ? [requestBody.system]
      : [];

  if (systemParts.length > 0) {
    openAiMessages.push({
      role: "system",
      content: systemParts.join("\n\n"),
    });
  }

  for (const message of requestBody.messages ?? []) {
    const blocks = contentToBlocks(message.content);

    if (message.role === "assistant") {
      const text = normalizeTextParts(blocks).join("\n");
      const reasoningContent = blocks
        .filter((block) => block.type === "thinking")
        .map((block) => block.thinking ?? "")
        .filter(Boolean)
        .join("\n");
      const toolCalls = blocks
        .filter((block) => block.type === "tool_use")
        .map((block, index) => ({
          id: block.id ?? `toolu_proxy_${index}`,
          type: "function",
          function: {
            name: block.name,
            arguments: JSON.stringify(block.input ?? {}),
          },
        }));

      if (text || toolCalls.length > 0) {
        openAiMessages.push({
          role: "assistant",
          ...(text ? { content: text } : { content: "" }),
          ...(reasoningContent ? { reasoning_content: reasoningContent } : {}),
          ...(toolCalls.length > 0 ? { tool_calls: toolCalls } : {}),
        });
      }

      continue;
    }

    if (message.role === "user") {
      let bufferedUserText = [];

      for (const block of blocks) {
        if (block.type === "tool_result") {
          if (bufferedUserText.length > 0) {
            openAiMessages.push({
              role: "user",
              content: bufferedUserText.join("\n"),
            });
            bufferedUserText = [];
          }

          openAiMessages.push({
            role: "tool",
            tool_call_id: block.tool_use_id,
            content: normalizeToolResultContent(block.content),
          });
          continue;
        }

        if (block.type === "text") {
          bufferedUserText.push(block.text ?? "");
          continue;
        }
      }

      if (bufferedUserText.length > 0) {
        openAiMessages.push({
          role: "user",
          content: bufferedUserText.join("\n"),
        });
      }
    }
  }

  const requestedEffort =
    requestBody.output_config?.effort ??
    requestBody.metadata?.effort ??
    requestBody.reasoning?.effort;
  const reasoningEffort = mapEffortToZen(
    requestedEffort,
    mapEffortToZen(options.upstreamReasoningEffort, "xhigh"),
  );
  const thinkingType =
    requestBody.thinking?.type === "disabled" ? "disabled" : options.deepseekThinkingType;
  const upstreamModel = options.upstreamModel;

  return {
    model: upstreamModel,
    stream: Boolean(requestBody.stream),
    temperature: requestBody.temperature,
    max_completion_tokens: requestBody.max_tokens,
    ...(supportsDeepSeekThinking(upstreamModel)
      ? {
          thinking: {
            type: thinkingType,
          },
        }
      : {}),
    reasoning_effort: reasoningEffort,
    messages: openAiMessages,
    ...(Array.isArray(requestBody.stop_sequences) &&
    requestBody.stop_sequences.length > 0
      ? { stop: requestBody.stop_sequences }
      : {}),
    ...(Array.isArray(requestBody.tools) && requestBody.tools.length > 0
      ? {
          tools: requestBody.tools.map((tool) => ({
            type: "function",
            function: {
              name: tool.name,
              description: tool.description ?? "",
              parameters: tool.input_schema ?? {
                type: "object",
                properties: {},
              },
            },
          })),
          tool_choice:
            requestBody.tool_choice?.type === "tool"
              ? {
                  type: "function",
                  function: {
                    name: requestBody.tool_choice.name,
                  },
                }
              : "auto",
        }
      : {}),
  };
}

function mapStopReason(finishReason) {
  switch (finishReason) {
    case "tool_calls":
      return "tool_use";
    case "length":
      return "max_tokens";
    case "stop":
      return "end_turn";
    case "content_filter":
      return "end_turn";
    default:
      return "end_turn";
  }
}

export function buildAnthropicMessageFromOpenAiChoice(choice, completion) {
  const content = [];
  const message = choice?.message ?? {};
  if (typeof message.reasoning_content === "string" && message.reasoning_content) {
    content.push({
      type: "thinking",
      thinking: message.reasoning_content,
      signature: "proxy-unverified",
    });
  }
  const text = typeof message.content === "string" ? message.content : "";
  if (text) {
    content.push({ type: "text", text });
  }

  for (const toolCall of message.tool_calls ?? []) {
    content.push({
      type: "tool_use",
      id: toolCall.id ?? `toolu_proxy_${crypto.randomUUID()}`,
      name: toolCall.function?.name ?? "tool",
      input: safeJsonParse(toolCall.function?.arguments ?? "{}", {}),
    });
  }

  return {
    id: completion.id ?? `msg_${crypto.randomUUID()}`,
    type: "message",
    role: "assistant",
    model: completion.model,
    content,
    stop_reason: mapStopReason(choice?.finish_reason),
    stop_sequence: null,
    usage: {
      input_tokens: completion.usage?.prompt_tokens ?? 0,
      output_tokens: completion.usage?.completion_tokens ?? 0,
    },
  };
}

function writeSse(response, event, data) {
  response.write(`event: ${event}\n`);
  response.write(`data: ${JSON.stringify(data)}\n\n`);
}

export async function relayOpenAiStreamAsAnthropic(response, upstreamResponse) {
  response.writeHead(200, {
    "content-type": "text/event-stream",
    "cache-control": "no-cache",
    connection: "keep-alive",
  });

  const decoder = new TextDecoder();
  let buffer = "";
  let messageId = `msg_${crypto.randomUUID()}`;
  let model = null;
  let stopReason = "end_turn";
  let promptTokens = 0;
  let completionTokens = 0;
  let textBlockStarted = false;
  let textBlockStopped = false;
  let thinkingBlockStarted = false;
  let thinkingBlockStopped = false;
  const toolBlockState = new Map();

  writeSse(response, "message_start", {
    type: "message_start",
    message: {
      id: messageId,
      type: "message",
      role: "assistant",
      content: [],
      model: "proxy-pending",
      stop_reason: null,
      stop_sequence: null,
      usage: {
        input_tokens: 0,
        output_tokens: 0,
      },
    },
  });

  const ensureTextBlock = () => {
    stopThinkingBlock();
    if (textBlockStarted) {
      return;
    }

    textBlockStarted = true;
    writeSse(response, "content_block_start", {
      type: "content_block_start",
      index: 0,
      content_block: {
        type: "text",
        text: "",
      },
    });
  };

  const stopTextBlock = () => {
    if (!textBlockStarted || textBlockStopped) {
      return;
    }

    textBlockStopped = true;
    writeSse(response, "content_block_stop", {
      type: "content_block_stop",
      index: 0,
    });
  };

  const ensureThinkingBlock = () => {
    if (thinkingBlockStarted) {
      return;
    }

    thinkingBlockStarted = true;
    writeSse(response, "content_block_start", {
      type: "content_block_start",
      index: 0,
      content_block: {
        type: "thinking",
        thinking: "",
        signature: "proxy-unverified",
      },
    });
  };

  const stopThinkingBlock = () => {
    if (!thinkingBlockStarted || thinkingBlockStopped) {
      return;
    }

    thinkingBlockStopped = true;
    writeSse(response, "content_block_delta", {
      type: "content_block_delta",
      index: 0,
      delta: {
        type: "signature_delta",
        signature: "proxy-unverified",
      },
    });
    writeSse(response, "content_block_stop", {
      type: "content_block_stop",
      index: 0,
    });
  };

  const ensureToolBlock = (toolDelta) => {
    const key = toolDelta.index ?? 0;
    const index =
      (thinkingBlockStarted ? 1 : 0) + (textBlockStarted ? 1 : 0) + key;
    if (toolBlockState.has(key)) {
      const current = toolBlockState.get(key);
      if (toolDelta.id && !current.id) {
        current.id = toolDelta.id;
      }
      if (toolDelta.function?.name && !current.name) {
        current.name = toolDelta.function.name;
      }
      return current;
    }

    stopTextBlock();
    stopThinkingBlock();
    const state = {
      index,
      id: toolDelta.id ?? `toolu_proxy_${crypto.randomUUID()}`,
      name: toolDelta.function?.name ?? "tool",
    };
    toolBlockState.set(key, state);
    writeSse(response, "content_block_start", {
      type: "content_block_start",
      index,
      content_block: {
        type: "tool_use",
        id: state.id,
        name: state.name,
        input: {},
      },
    });
    return state;
  };

  for await (const chunk of upstreamResponse.body) {
    buffer += decoder.decode(chunk, { stream: true });

    while (buffer.includes("\n\n")) {
      const boundary = buffer.indexOf("\n\n");
      const rawEvent = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);

      const dataLines = rawEvent
        .split("\n")
        .filter((line) => line.startsWith("data:"))
        .map((line) => line.slice(5).trim());

      for (const dataLine of dataLines) {
        if (!dataLine || dataLine === "[DONE]") {
          continue;
        }

        const parsed = safeJsonParse(dataLine, null);
        if (!parsed) {
          continue;
        }

        if (parsed.id) {
          messageId = parsed.id.replace(/^chatcmpl/, "msg");
        }
        if (parsed.model) {
          model = parsed.model;
        }
        if (parsed.usage?.prompt_tokens != null) {
          promptTokens = parsed.usage.prompt_tokens;
        }
        if (parsed.usage?.completion_tokens != null) {
          completionTokens = parsed.usage.completion_tokens;
        }

        const choice = parsed.choices?.[0];
        if (!choice) {
          continue;
        }

        if (choice.finish_reason) {
          stopReason = mapStopReason(choice.finish_reason);
        }

        const delta = choice.delta ?? {};

        if (typeof delta.content === "string" && delta.content.length > 0) {
          ensureTextBlock();
          writeSse(response, "content_block_delta", {
            type: "content_block_delta",
            index: 0,
            delta: {
              type: "text_delta",
              text: delta.content,
            },
          });
        }

        if (
          typeof delta.reasoning_content === "string" &&
          delta.reasoning_content.length > 0
        ) {
          ensureThinkingBlock();
          writeSse(response, "content_block_delta", {
            type: "content_block_delta",
            index: 0,
            delta: {
              type: "thinking_delta",
              thinking: delta.reasoning_content,
            },
          });
        }

        for (const toolDelta of delta.tool_calls ?? []) {
          const state = ensureToolBlock(toolDelta);
          if (toolDelta.function?.arguments) {
            writeSse(response, "content_block_delta", {
              type: "content_block_delta",
              index: state.index,
              delta: {
                type: "input_json_delta",
                partial_json: toolDelta.function.arguments,
              },
            });
          }
        }
      }
    }
  }

  stopTextBlock();
  stopThinkingBlock();
  for (const state of toolBlockState.values()) {
    writeSse(response, "content_block_stop", {
      type: "content_block_stop",
      index: state.index,
    });
  }

  writeSse(response, "message_delta", {
    type: "message_delta",
    delta: {
      stop_reason: stopReason,
      stop_sequence: null,
    },
    usage: {
      output_tokens: completionTokens,
    },
  });
  writeSse(response, "message_stop", {
    type: "message_stop",
  });
  response.end();
}

export function estimateAnthropicInputTokens(requestBody) {
  const payload = JSON.stringify({
    system: requestBody.system ?? "",
    messages: requestBody.messages ?? [],
    tools: requestBody.tools ?? [],
  });

  return Math.max(1, Math.ceil(payload.length / 4));
}
