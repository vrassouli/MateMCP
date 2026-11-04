import { existsSync, readFileSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const errors = [];
const fail = (message) => errors.push(message);
const readJson = (name) => {
  try { return JSON.parse(readFileSync(path.join(root, name), "utf8")); }
  catch (error) { fail(name + ": " + error.message); return {}; }
};
const plugin = readJson("openai-plugin/plugin.json");
const mcp = readJson("openai-plugin/mcp.json");
const openai = plugin.extensions?.["com.openai"];
const ui = openai?.interface;

if (plugin.$schema !== "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json") fail("Unexpected plugin schema.");
if (!/^(?!.*(?:--|\.\.))[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$/.test(plugin.name ?? "")) fail("Invalid portable plugin name.");
if (!plugin.version || !plugin.description) fail("Package version and description are required.");
for (const field of ["websiteURL", "supportURL", "privacyPolicyURL", "termsOfServiceURL"]) {
  try {
    const url = new URL(ui?.[field]);
    if (url.protocol !== "https:" || url.username || url.password) fail(field + " must be a public HTTPS URL.");
  } catch { fail(field + " must be a valid HTTPS URL."); }
}
const packageRoot = path.resolve(root, "openai-plugin");
for (const field of ["logo", "composerIcon"]) {
  const relative = ui?.[field];
  if (typeof relative !== "string" || !relative.startsWith("./")) { fail(field + " must be a plugin-relative path."); continue; }
  const absolute = path.resolve(packageRoot, relative.slice(2));
  if (!absolute.startsWith(packageRoot + path.sep) || !existsSync(absolute) || !statSync(absolute).isFile()) { fail(field + " references a missing/out-of-package file."); continue; }
  if (!/\.(png|jpe?g|webp|svg)$/i.test(absolute) || statSync(absolute).size > 5 * 1024 * 1024) fail(field + " has an unsupported image format or size.");
  if (absolute.endsWith(".svg")) {
    const svg = readFileSync(absolute, "utf8");
    const w = Number(svg.match(/\bwidth="(\d+)"/i)?.[1]);
    const h = Number(svg.match(/\bheight="(\d+)"/i)?.[1]);
    const vb = svg.match(/\bviewBox="([\d.\s-]+)"/i)?.[1]?.trim().split(/\s+/).map(Number);
    if (!(w >= 48 && h >= 48 && w === h) && !(vb?.length === 4 && vb[2] >= 48 && vb[3] >= 48 && vb[2] === vb[3])) fail(path.basename(absolute) + " must be square and at least 48x48.");
  }
}
const skillRelative = openai?.onboardingSkill;
if (typeof skillRelative !== "string" || !skillRelative.startsWith("./")) fail("onboardingSkill must be a plugin-relative path.");
else {
  const skillPath = path.resolve(packageRoot, skillRelative.slice(2));
  if (!skillPath.startsWith(packageRoot + path.sep) || !existsSync(skillPath)) fail("Onboarding skill file is missing/outside the package.");
  else if (!/^---\s*\n(?=[\s\S]*?^name:\s*\S+)(?=[\s\S]*?^description:\s*\S+)[\s\S]*?^---/m.test(readFileSync(skillPath, "utf8"))) fail("Onboarding skill needs name and description YAML front matter.");
}
if (mcp.$schema !== "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json") fail("Unexpected MCP schema.");
const servers = mcp.mcpServers ?? {};
if (Object.keys(servers).length !== 1 || !servers.matemcp) fail("Declare exactly one MCP server named matemcp.");
const server = servers.matemcp ?? {};
if (server.type !== "streamable-http" || server.url !== "https://relay.matemcp.com/mcp") fail("MCP server must use Streamable HTTP and the stable public /mcp URL.");
try {
  const url = new URL(server.url);
  if (url.protocol !== "https:" || url.username || url.password || url.hash) fail("MCP URL must be public HTTPS without credentials or fragment.");
} catch { fail("MCP URL is invalid."); }
if (server.headers && Object.keys(server.headers).length) fail("Fixed MCP headers are forbidden in the public package.");
const auth = server.extensions?.["com.openai"]?.auth;
if (auth?.type !== "oauth" || auth?.client?.mode !== "dcr") fail("OpenAI MCP authentication must use OAuth with DCR.");
if (auth?.client?.clientId || auth?.client?.clientSecret || auth?.clientSecret) fail("OAuth client credentials must not be bundled.");
const positive = openai?.review?.test_cases?.positive;
const negative = openai?.review?.test_cases?.negative;
if (!Array.isArray(positive) || positive.length !== 5) fail("Exactly five positive test cases are required.");
if (!Array.isArray(negative) || negative.length !== 3) fail("Exactly three negative test cases are required.");
for (const [index, test] of (Array.isArray(positive) ? positive : []).entries())
  for (const field of ["description", "prompt", "tools_triggered", "expected_behavior"])
    if (typeof test?.[field] !== "string" || !test[field].trim()) fail("Positive test " + (index + 1) + " missing " + field + ".");
for (const [index, test] of (Array.isArray(negative) ? negative : []).entries())
  for (const field of ["description", "prompt", "expected_behavior"])
    if (typeof test?.[field] !== "string" || !test[field].trim()) fail("Negative test " + (index + 1) + " missing " + field + ".");
const forbidden = new Set(["clientSecret", "test_credentials", "reviewer_instructions", "access_token", "refresh_token", "api_key", "password"]);
const scan = (value, at = "package") => {
  if (!value || typeof value !== "object") return;
  for (const [key, child] of Object.entries(value)) {
    if (forbidden.has(key)) fail(at + " contains forbidden credential field " + key + ".");
    scan(child, at + "." + key);
  }
};
scan(plugin); scan(mcp);
if (!openai?.review?.demo_recording_url) console.log("NOTE: real reviewer demo URL is pending; required before submitting MCP review.");
if (errors.length) { console.error("OpenAI plugin package validation failed:"); for (const error of errors) console.error(" - " + error); process.exit(1); }
console.log("OpenAI plugin package validation passed.");
