import { readFile } from "node:fs/promises";

const protocolSource = await readFile(
  new URL("../../src/Deckwraith.Application/Hosting/HostProtocol.cs", import.meta.url),
  "utf8",
);
const hostSource = await readFile(
  new URL("../../src/Deckwraith.Hosting/DeckwraithHost.cs", import.meta.url),
  "utf8",
);
const rendererProtocolSource = await readFile(
  new URL("../src/ipc/protocol.ts", import.meta.url),
  "utf8",
);
const rendererSource = await readFile(
  new URL("../src/App.tsx", import.meta.url),
  "utf8",
);

const hostVersion = requiredCapture(protocolSource, /CurrentVersion\s*=\s*(\d+)/, "host protocol version");
const rendererVersion = requiredCapture(
  rendererProtocolSource,
  /HOST_PROTOCOL_VERSION\s*=\s*(\d+)/,
  "renderer protocol version",
);
if (hostVersion !== rendererVersion) {
  throw new Error(`Renderer protocol ${rendererVersion} does not match host protocol ${hostVersion}.`);
}

const hostCommands = extractHostNames(hostSource, "Commands");
const hostQueries = extractHostNames(hostSource, "Queries");
const rendererCommands = extractCalls(rendererSource, "command");
const rendererQueries = extractCalls(rendererSource, "query");
assertSubset(rendererCommands, hostCommands, "command");
assertSubset(rendererQueries, hostQueries, "query");

function requiredCapture(source, pattern, label) {
  const match = source.match(pattern);
  if (!match) throw new Error(`Could not find ${label}.`);
  return match[1];
}

function extractHostNames(source, member) {
  const block = requiredCapture(
    source,
    new RegExp(`${member}\\s*=\\s*\\[(.*?)\\];`, "s"),
    `host ${member}`,
  );
  return new Set([...block.matchAll(/"([^"]+)"/g)].map((match) => match[1]));
}

function extractCalls(source, functionName) {
  const pattern = new RegExp(`\\b${functionName}(?:<[^>]+>)?\\(\\s*"([^"]+)"`, "g");
  return new Set([...source.matchAll(pattern)].map((match) => match[1]));
}

function assertSubset(rendererNames, hostNames, kind) {
  const missing = [...rendererNames].filter((name) => !hostNames.has(name));
  if (missing.length > 0) {
    throw new Error(`Renderer ${kind}s missing from the host schema: ${missing.join(", ")}.`);
  }
}
