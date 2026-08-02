import http from "node:http";
import crypto from "node:crypto";

import {
  anthropicRequestToOpenAiRequest,
  buildAnthropicMessageFromOpenAiChoice,
  estimateAnthropicInputTokens,
  relayOpenAiStreamAsAnthropic,
} from "./anthropic-openai-proxy.js";
import { loadConfig } from "./config.js";

async function readJson(request) {
  const chunks = [];
  for await (const chunk of request) {
    chunks.push(chunk);
  }

  const raw = Buffer.concat(chunks).toString("utf8");
  return raw ? JSON.parse(raw) : {};
}

function writeJson(response, statusCode, payload) {
  response.writeHead(statusCode, {
    "content-type": "application/json",
  });
  response.end(JSON.stringify(payload));
}

function writeError(response, statusCode, message, type = "invalid_request_error") {
  writeJson(response, statusCode, {
    type: "error",
    error: {
      type,
      message,
    },
  });
}

function isAuthorized(request, config) {
  const expectedKey = config.proxyApiKey || config.upstreamApiKey;
  if (!expectedKey) {
    return true;
  }

  const candidate =
    request.headers["x-api-key"] ??
    request.headers.authorization?.replace(/^Bearer\s+/i, "");
  return candidate === expectedKey;
}

function getPathname(request) {
  return new URL(request.url ?? "/", "http://127.0.0.1").pathname;
}

function isPublicRoute(request) {
  const pathname = getPathname(request);
  return (
    (request.method === "GET" && pathname === "/health") ||
    (request.method === "GET" && pathname === "/v1/models") ||
    (request.method === "GET" && pathname.startsWith("/v1/models/"))
  );
}

async function forwardToUpstream(config, requestBody) {
  const upstreamRequest = anthropicRequestToOpenAiRequest(requestBody, config);

  const response = await fetch(config.upstreamChatCompletionsUrl, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      authorization: `Bearer ${config.upstreamApiKey}`,
    },
    body: JSON.stringify(upstreamRequest),
  });

  return response;
}

export function createRequestHandler(config = loadConfig()) {
  return async (request, response) => {
    const requestStartedAt = Date.now();
    const originalEnd = response.end.bind(response);
    response.end = (chunk, encoding, callback) => {
      originalEnd(chunk, encoding, callback);
      console.log(
        JSON.stringify({
          event: "request",
          method: request.method,
          path: new URL(request.url ?? "/", "http://127.0.0.1").pathname,
          status: response.statusCode,
          ms: Date.now() - requestStartedAt,
        }),
      );
    };
    try {
      const pathname = getPathname(request);

      if (request.method === "OPTIONS") {
        response.writeHead(204, {
          "access-control-allow-origin": "*",
          "access-control-allow-methods": "GET,POST,OPTIONS",
          "access-control-allow-headers":
            "content-type,x-api-key,authorization,anthropic-version,anthropic-beta",
        });
        response.end();
        return;
      }

      if (!isPublicRoute(request) && !isAuthorized(request, config)) {
        writeError(response, 401, "Invalid proxy API key", "authentication_error");
        return;
      }

      if (request.method === "GET" && pathname === "/health") {
        writeJson(response, 200, {
          ok: true,
          upstream_model: config.upstreamModel,
          upstream_url: config.upstreamChatCompletionsUrl,
        });
        return;
      }

      if (request.method === "GET" && pathname === "/v1/models") {
        writeJson(response, 200, {
          object: "list",
          data: [
            {
              id: config.anthropicModelAlias,
              type: "model",
              display_name: `${config.anthropicModelAlias} -> ${config.upstreamModel}`,
              created_at: new Date().toISOString(),
            },
          ],
          has_more: false,
          first_id: config.anthropicModelAlias,
          last_id: config.anthropicModelAlias,
        });
        return;
      }

      if (request.method === "GET" && pathname.startsWith("/v1/models/")) {
        const requestedModel = decodeURIComponent(
          pathname.slice("/v1/models/".length),
        );
        if (requestedModel && requestedModel !== config.anthropicModelAlias) {
          writeJson(response, 200, {
            id: requestedModel,
            type: "model",
            display_name: `${requestedModel} -> ${config.upstreamModel}`,
            created_at: new Date().toISOString(),
          });
          return;
        }

        writeJson(response, 200, {
          id: config.anthropicModelAlias,
          type: "model",
          display_name: `${config.anthropicModelAlias} -> ${config.upstreamModel}`,
          created_at: new Date().toISOString(),
        });
        return;
      }

      if (request.method === "POST" && pathname === "/v1/messages/count_tokens") {
        const requestBody = await readJson(request);
        writeJson(response, 200, {
          input_tokens: estimateAnthropicInputTokens(requestBody),
        });
        return;
      }

      if (request.method === "POST" && pathname === "/v1/messages") {
        if (!config.upstreamApiKey) {
          writeError(response, 500, "UPSTREAM_API_KEY is not configured", "api_error");
          return;
        }

        const requestBody = await readJson(request);
        const upstreamResponse = await forwardToUpstream(config, requestBody);

        if (!upstreamResponse.ok) {
          const upstreamText = await upstreamResponse.text();
          writeError(
            response,
            upstreamResponse.status,
            `Upstream request failed: ${upstreamText}`,
            "api_error",
          );
          return;
        }

        if (requestBody.stream) {
          await relayOpenAiStreamAsAnthropic(response, upstreamResponse);
          return;
        }

        const completion = await upstreamResponse.json();
        const choice = completion.choices?.[0];
        if (!choice) {
          writeError(response, 502, "Upstream response did not include a completion choice", "api_error");
          return;
        }

        const anthropicMessage = buildAnthropicMessageFromOpenAiChoice(
          choice,
          completion,
        );
        writeJson(response, 200, anthropicMessage);
        return;
      }

      writeError(response, 404, `No route for ${request.method} ${request.url}`, "not_found_error");
    } catch (error) {
      writeError(
        response,
        500,
        error instanceof Error ? error.message : "Unknown server error",
        "api_error",
      );
    }
  };
}

export function createProxyServer(config = loadConfig()) {
  return http.createServer(createRequestHandler(config));
}

export function startServer(config = loadConfig()) {
  const server = createProxyServer(config);
  server.listen(config.port, config.host, () => {
    const token = config.proxyApiKey ? "configured" : "disabled";
    console.log(
      JSON.stringify({
        event: "proxy_listening",
        host: config.host,
        port: config.port,
        anthropic_model_alias: config.anthropicModelAlias,
        upstream_model: config.upstreamModel,
        proxy_api_key: token,
      }),
    );
  });
  return server;
}

if (import.meta.url === `file://${process.argv[1]}`) {
  startServer();
}
