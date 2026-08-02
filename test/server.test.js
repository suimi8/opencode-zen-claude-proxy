import test from "node:test";
import assert from "node:assert/strict";
import { Readable } from "node:stream";

import { createRequestHandler } from "../src/server.js";

function makeRequest({ method, url, headers = {}, body }) {
  const stream = Readable.from(body ? [Buffer.from(JSON.stringify(body))] : []);
  stream.method = method;
  stream.url = url;
  stream.headers = headers;
  return stream;
}

function makeResponse() {
  const state = {
    statusCode: null,
    headers: null,
    chunks: [],
    ended: false,
  };

  return {
    ...state,
    writeHead(statusCode, headers) {
      state.statusCode = statusCode;
      state.headers = headers;
    },
    write(chunk) {
      state.chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(String(chunk)));
    },
    end(chunk = "") {
      if (chunk) {
        this.write(chunk);
      }
      state.ended = true;
    },
    get bodyText() {
      return Buffer.concat(state.chunks).toString("utf8");
    },
    get json() {
      return JSON.parse(this.bodyText);
    },
    get statusCode() {
      return state.statusCode;
    },
    get headers() {
      return state.headers;
    },
  };
}

test("proxy returns anthropic JSON for a non-streaming tool call response", async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () =>
    new Response(
      JSON.stringify({
        id: "chatcmpl_123",
        object: "chat.completion",
        model: "deepseek-v4-flash-free",
        choices: [
          {
            index: 0,
            finish_reason: "tool_calls",
            message: {
              role: "assistant",
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
        ],
        usage: {
          prompt_tokens: 12,
          completion_tokens: 7,
        },
      }),
      {
        status: 200,
        headers: { "content-type": "application/json" },
      },
    );

  const handler = createRequestHandler({
    upstreamApiKey: "test-key",
    upstreamModel: "deepseek-v4-flash-free",
    deepseekThinkingType: "enabled",
    upstreamReasoningEffort: "xhigh",
    upstreamChatCompletionsUrl: "http://example.test/v1/chat/completions",
  });

  const request = makeRequest({
    method: "POST",
    url: "/v1/messages",
    headers: {
      "content-type": "application/json",
      "x-api-key": "client-key",
      "anthropic-version": "2023-06-01",
    },
    body: {
      model: "claude-code-proxy",
      max_tokens: 512,
      messages: [{ role: "user", content: "Read README.md" }],
    },
  });
  const response = makeResponse();
  await handler(request, response);

  assert.equal(response.statusCode, 200);
  const body = response.json;
  assert.equal(body.stop_reason, "tool_use");
  assert.deepEqual(body.content[1], {
    type: "tool_use",
    id: "call_1",
    name: "read_file",
    input: { path: "README.md" },
  });

  globalThis.fetch = originalFetch;
});

test("proxy converts OpenAI streaming chunks into Anthropic SSE events", async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () =>
    new Response(
      Readable.from([
        `data: ${JSON.stringify({
          id: "chatcmpl_stream",
          object: "chat.completion.chunk",
          model: "deepseek-v4-flash-free",
          choices: [
            {
              index: 0,
              delta: { role: "assistant", content: "Checking" },
              finish_reason: null,
            },
          ],
        })}\n\n`,
        `data: ${JSON.stringify({
          id: "chatcmpl_stream",
          object: "chat.completion.chunk",
          model: "deepseek-v4-flash-free",
          choices: [
            {
              index: 0,
              delta: {
                tool_calls: [
                  {
                    index: 0,
                    id: "call_2",
                    type: "function",
                    function: {
                      name: "read_file",
                      arguments: "{\"path\":",
                    },
                  },
                ],
              },
              finish_reason: null,
            },
          ],
        })}\n\n`,
        `data: ${JSON.stringify({
          id: "chatcmpl_stream",
          object: "chat.completion.chunk",
          model: "deepseek-v4-flash-free",
          choices: [
            {
              index: 0,
              delta: {
                tool_calls: [
                  {
                    index: 0,
                    function: {
                      arguments: "\"README.md\"}",
                    },
                  },
                ],
              },
              finish_reason: null,
            },
          ],
        })}\n\n`,
        `data: ${JSON.stringify({
          id: "chatcmpl_stream",
          object: "chat.completion.chunk",
          model: "deepseek-v4-flash-free",
          choices: [
            {
              index: 0,
              delta: {},
              finish_reason: "tool_calls",
            },
          ],
          usage: {
            prompt_tokens: 9,
            completion_tokens: 6,
          },
        })}\n\n`,
        "data: [DONE]\n\n",
      ]),
      {
        status: 200,
        headers: { "content-type": "text/event-stream" },
      },
    );

  const handler = createRequestHandler({
    upstreamApiKey: "test-key",
    upstreamModel: "deepseek-v4-flash-free",
    deepseekThinkingType: "enabled",
    upstreamReasoningEffort: "xhigh",
    upstreamChatCompletionsUrl: "http://example.test/v1/chat/completions",
  });

  const request = makeRequest({
    method: "POST",
    url: "/v1/messages",
    headers: {
      "content-type": "application/json",
      "x-api-key": "client-key",
      "anthropic-version": "2023-06-01",
    },
    body: {
      model: "claude-code-proxy",
      max_tokens: 512,
      stream: true,
      messages: [{ role: "user", content: "Read README.md" }],
    },
  });
  const response = makeResponse();
  await handler(request, response);

  assert.equal(response.statusCode, 200);
  const body = response.bodyText;
  assert.match(body, /event: message_start/);
  assert.match(body, /event: content_block_start/);
  assert.match(body, /"type":"tool_use"/);
  assert.match(body, /"partial_json":"\\\"README\.md\\\"}"/);
  assert.match(body, /"stop_reason":"tool_use"/);

  globalThis.fetch = originalFetch;
});
