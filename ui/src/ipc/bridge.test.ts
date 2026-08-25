import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BridgeError, subscribe } from "./bridge";
import type { HostEvent } from "../state/types";

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  readonly listeners = new Map<string, (event: MessageEvent) => void>();
  onerror: ((event: Event) => void) | null = null;
  closed = false;

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  addEventListener(name: string, listener: EventListenerOrEventListenerObject) {
    this.listeners.set(name, listener as (event: MessageEvent) => void);
  }

  close() {
    this.closed = true;
  }

  emit(event: HostEvent) {
    this.listeners.get("host")?.({ data: JSON.stringify(event) } as MessageEvent);
  }
}

function status(cursor: number) {
  return new Response(JSON.stringify({
    protocolVersion: 1,
    eventCursor: cursor,
    deckPath: "/tmp/deck",
    theme: "system",
    themeTokens: {},
  }), { status: 200, headers: { "content-type": "application/json" } });
}

describe("host event subscription", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    FakeEventSource.instances = [];
    vi.stubGlobal("EventSource", FakeEventSource);
    vi.stubGlobal("window", globalThis);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("resets its cursor when the host restarts", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(status(2)));
    const snapshot = vi.fn().mockResolvedValue(undefined);
    const onError = vi.fn();
    const stop = subscribe(8, vi.fn(), snapshot, onError);

    expect(FakeEventSource.instances[0]?.url).toBe("/api/v1/events?after=8");
    FakeEventSource.instances[0]?.onerror?.(new Event("error"));
    await vi.advanceTimersByTimeAsync(700);

    expect(snapshot).toHaveBeenCalledOnce();
    expect(onError).not.toHaveBeenCalled();
    expect(FakeEventSource.instances[1]?.url).toBe("/api/v1/events?after=0");
    stop();
  });

  it("surfaces reconnect failures and keeps trying from the durable cursor", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("host unavailable")));
    const onError = vi.fn();
    const stop = subscribe(12, vi.fn(), vi.fn().mockResolvedValue(undefined), onError);

    FakeEventSource.instances[0]?.onerror?.(new Event("error"));
    await vi.advanceTimersByTimeAsync(700);

    expect(onError).toHaveBeenCalledWith(expect.objectContaining({ message: "host unavailable" }));
    expect(FakeEventSource.instances[1]?.url).toBe("/api/v1/events?after=12");
    stop();
  });

  it("ignores a late error from a superseded event source", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(status(12)));
    const snapshot = vi.fn().mockResolvedValue(undefined);
    const stop = subscribe(12, vi.fn(), snapshot, vi.fn());
    const first = FakeEventSource.instances[0]!;

    first.onerror?.(new Event("error"));
    await vi.advanceTimersByTimeAsync(700);
    expect(FakeEventSource.instances).toHaveLength(2);

    first.onerror?.(new Event("error"));
    await vi.advanceTimersByTimeAsync(700);
    expect(FakeEventSource.instances).toHaveLength(2);
    expect(snapshot).toHaveBeenCalledOnce();
    stop();
  });

  it("stops and reports an incompatible event protocol", async () => {
    const onEvent = vi.fn();
    const onError = vi.fn();
    subscribe(0, onEvent, vi.fn().mockResolvedValue(undefined), onError);

    FakeEventSource.instances[0]?.emit({
      protocolVersion: 99,
      cursor: 1,
      name: "model.started",
      timestamp: "2026-08-25T00:00:00Z",
      payload: {},
    });
    await vi.advanceTimersByTimeAsync(2000);

    expect(onEvent).not.toHaveBeenCalled();
    expect(onError).toHaveBeenCalledWith(expect.any(BridgeError));
    expect(FakeEventSource.instances).toHaveLength(1);
    expect(FakeEventSource.instances[0]?.closed).toBe(true);
  });
});
