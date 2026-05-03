import { readFileSync } from "node:fs";

const OPENAPI_JSON_PATH = new URL("../../../docs/openapi.actions.min.json", import.meta.url);
const OPENAPI_YAML_PATH = new URL("../../../docs/openapi.actions.yaml", import.meta.url);
const OPENAPI_PLACEHOLDER = "https://YOUR-RELAY-URL.example.com";

const openApiJsonTemplate = JSON.parse(readFileSync(OPENAPI_JSON_PATH, "utf8")) as {
  servers?: Array<Record<string, unknown>>;
  [key: string]: unknown;
};
const openApiYamlTemplate = readFileSync(OPENAPI_YAML_PATH, "utf8");

export function renderOpenApiJson(baseUrl: string): string {
  const payload = structuredClone(openApiJsonTemplate);
  if (Array.isArray(payload.servers) && payload.servers[0]) {
    payload.servers[0] = {
      ...payload.servers[0],
      url: baseUrl,
    };
  }

  return JSON.stringify(payload);
}

export function renderOpenApiYaml(baseUrl: string): string {
  return openApiYamlTemplate.replace(OPENAPI_PLACEHOLDER, baseUrl);
}
