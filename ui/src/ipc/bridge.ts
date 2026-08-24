import type { HostEvent } from "../state/types";

type RequestKind = "command" | "query";

type HostResponse<T> = {
  protocolVersion: number;
  requestId: string;
  success: boolean;
  result: T | null;
  error: { code: string; message: string; retryable: boolean } | null;
  eventCursor: number;
};

export class BridgeError extends Error {
  constructor(
    public readonly code: string,
    message: string,
    public readonly retryable = false,
  ) {
    super(message);
  }
}

export async function request<T>(
  kind: RequestKind,
  name: string,
  payload: object = {},
): Promise<T> {
  const response = await fetch("/api/v1/request", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      protocolVersion: 1,
      requestId: crypto.randomUUID(),
      kind,
      name,
      payload,
    }),
  });
  const envelope = (await response.json()) as HostResponse<T> | { code: string; message: string };
  if (!response.ok || !("success" in envelope)) {
    const failure = envelope as { code?: string; message?: string };
    throw new BridgeError(failure.code ?? "transport", failure.message ?? response.statusText);
  }
  if (!envelope.success) {
    throw new BridgeError(
      envelope.error?.code ?? "host-error",
      envelope.error?.message ?? "Deckwraith request failed.",
      envelope.error?.retryable,
    );
  }
  return envelope.result as T;
}

export const command = <T>(name: string, payload: object = {}) =>
  request<T>("command", name, payload);

export const query = <T>(name: string, payload: object = {}) =>
  request<T>("query", name, payload);

export function subscribe(
  initialCursor: number,
  onEvent: (event: HostEvent) => void,
  onSnapshotRequired: () => Promise<void>,
): () => void {
  let cursor = initialCursor;
  let source: EventSource | null = null;
  let stopped = false;
  let reconnectTimer = 0;

  const connect = () => {
    if (stopped) return;
    source = new EventSource(`/api/v1/events?after=${cursor}`);
    source.addEventListener("host", (raw) => {
      const event = JSON.parse((raw as MessageEvent).data) as HostEvent;
      cursor = event.cursor;
      onEvent(event);
    });
    source.onerror = () => {
      source?.close();
      source = null;
      window.clearTimeout(reconnectTimer);
      reconnectTimer = window.setTimeout(async () => {
        try {
          await onSnapshotRequired();
          const status = await fetch("/api/v1/status").then((result) => result.json()) as {
            eventCursor: number;
          };
          cursor = Math.max(cursor, status.eventCursor);
        } finally {
          connect();
        }
      }, 700);
    };
  };

  connect();
  return () => {
    stopped = true;
    window.clearTimeout(reconnectTimer);
    source?.close();
  };
}
