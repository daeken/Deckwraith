import { describe, expect, it } from "vitest";
import {
  appendHostEvent,
  conversationMessage,
  describeActivity,
  eventChangesSnapshot,
  providerNeedsAttention,
  reconcileProviderAccess,
  statusLabel,
  visibleActivity,
} from "./App";
import type { ConversationAttachment } from "./ipc/bridge";
import type { HostEvent } from "./state/types";

function hostEvent(cursor: number, name: string, payload: Record<string, unknown> = {}): HostEvent {
  return {
    protocolVersion: 1,
    cursor,
    name,
    timestamp: "2026-08-25T00:00:00Z",
    payload,
  };
}

describe("activity presentation", () => {
  it("keeps an active lifecycle start when the bounded event buffer trims", () => {
    let events: HostEvent[] = [];
    events = appendHostEvent(events, hostEvent(1, "model.started", {
      shellId: "shell-one",
      wraith: "steward",
      runId: "01a03798a2abcdef",
    }));
    for (let cursor = 2; cursor <= 100; cursor += 1) {
      events = appendHostEvent(events, hostEvent(cursor, "model.text-delta", {
        shellId: "shell-one",
        delta: ".",
      }));
    }

    expect(events).toHaveLength(81);
    expect(events[0]?.name).toBe("model.started");
    expect(visibleActivity(events).map((event) => event.name)).toEqual(["model.started"]);
  });

  it("drops retained lifecycle starts after their terminal event", () => {
    let events = [hostEvent(1, "model.started", { shellId: "shell-one" })];
    for (let cursor = 2; cursor <= 100; cursor += 1) {
      events = appendHostEvent(events, hostEvent(cursor, "model.text-delta", {
        shellId: "shell-one",
        delta: ".",
      }));
    }
    events = appendHostEvent(events, hostEvent(101, "model.completed", {
      shellId: "shell-one",
      finishReason: "stop",
    }));

    expect(events).toHaveLength(80);
    expect(events.some((event) => event.name === "model.started")).toBe(false);
    expect(visibleActivity(events).at(-1)?.name).toBe("model.completed");
  });

  it("tracks simultaneous model shells independently", () => {
    const events = [
      hostEvent(1, "model.started", { shellId: "a", wraith: "vesper" }),
      hostEvent(2, "model.started", { shellId: "b", wraith: "lumen" }),
      hostEvent(3, "model.completed", { shellId: "a" }),
    ];

    expect(visibleActivity(events).map((event) => [event.name, event.payload.shellId])).toEqual([
      ["model.started", "b"],
      ["model.completed", "a"],
    ]);
  });

  it("hides the expected uninitialized-deck probe but keeps real failures", () => {
    const onboarding = hostEvent(1, "host.request.failed", {
      name: "deck.snapshot",
      code: "state-conflict",
      message: "The deck is not initialized.",
    });
    const failure = hostEvent(2, "host.request.failed", {
      name: "run.turn",
      code: "provider-error",
      message: "Provider refused the request.",
    });

    expect(visibleActivity([onboarding, failure])).toEqual([failure]);
    expect(describeActivity(failure)).toEqual({
      title: "Request failed",
      detail: "run.turn · Provider refused the request.",
      tone: "failed",
    });
  });

  it("collapses a model error and its matching command failure into one activity", () => {
    const modelError = hostEvent(20, "model.error", {
      code: "credential-rejected",
      message: "Reconnect the account.",
    });
    const requestFailure = hostEvent(28, "host.request.failed", {
      kind: "command",
      name: "run.turn",
      code: "credential-rejected",
      message: "Reconnect the account.",
    });
    const unrelatedFailure = hostEvent(29, "host.request.failed", {
      kind: "command",
      name: "deckbook.run-cell",
      code: "kernel-error",
      message: "Cell failed.",
    });

    expect(visibleActivity([modelError, requestFailure, unrelatedFailure])).toEqual([
      modelError,
      unrelatedFailure,
    ]);
  });

  it("explains cold-start recovery instead of exposing its raw payload", () => {
    const recovery = hostEvent(9, "recovery.completed", {
      wraith: "steward",
      incident: {
        recoveredRunIds: ["run-one"],
        outcomeUnknownOperationIds: ["operation-one", "operation-two"],
      },
    });

    expect(describeActivity(recovery)).toEqual({
      title: "Recovered durable state",
      detail: "steward · 2 interrupted operations marked outcome unknown",
    });
  });

  it("hides first-start projection setup but keeps meaningful recovery", () => {
    const empty = hostEvent(10, "recovery.completed", {
      wraith: "guest01",
      contextRevision: 0,
      contextTurn: 0,
      incident: {
        contextRebuilt: true,
        recoveredRunIds: [],
        outcomeUnknownOperationIds: [],
        atomicWriteResidues: [],
      },
    });
    const rebuiltConversation = hostEvent(11, "recovery.completed", {
      ...empty.payload,
      contextRevision: 4,
    });

    expect(visibleActivity([empty, rebuiltConversation])).toEqual([rebuiltConversation]);
  });
});

describe("conversation presentation", () => {
  it("adds durable artifact instructions without losing the human message", () => {
    const attachments: ConversationAttachment[] = [{
      fileName: "design notes.md",
      hash: "sha256:abc",
      length: 42,
      mediaType: "text/markdown",
    }];

    expect(conversationMessage("Please read this.", attachments)).toBe(
      "Please read this.\n\nRelevant files attached as durable artifacts:\n" +
      "- design notes.md (text/markdown, 42 bytes): sha256:abc\n\n" +
      "Use Get-DwArtifact with the hash to read an attachment; add -AsText for text files.",
    );
  });

  it("renders durable statuses as words", () => {
    expect(statusLabel("awaitingInput")).toBe("awaiting input");
    expect(statusLabel("outcome_unknown")).toBe("outcome unknown");
    expect(statusLabel("model-error")).toBe("model error");
  });

  it("offers a provider recovery action only for blocked credentials", () => {
    expect(providerNeedsAttention("missing")).toBe(true);
    expect(providerNeedsAttention("expired")).toBe(true);
    expect(providerNeedsAttention("rejected")).toBe(true);
    expect(providerNeedsAttention("error")).toBe(true);
    expect(providerNeedsAttention("refreshing")).toBe(false);
    expect(providerNeedsAttention("expiring")).toBe(false);
    expect(providerNeedsAttention("ready")).toBe(false);
  });
});

describe("snapshot refresh classification", () => {
  it("refreshes for command completion and stateful lifecycle events", () => {
    expect(eventChangesSnapshot(hostEvent(1, "host.request.completed", { kind: "command" }))).toBe(true);
    expect(eventChangesSnapshot(hostEvent(2, "host.request.completed", { kind: "query" }))).toBe(false);
    expect(eventChangesSnapshot(hostEvent(3, "model.completed"))).toBe(true);
    expect(eventChangesSnapshot(hostEvent(4, "model.text-delta"))).toBe(false);
  });

  it("replaces stale provider readiness with the host's latest authentication state", () => {
    const ready = {
      providerId: "openai-codex-subscription",
      capabilities: {
        streaming: true,
        nativeToolCalling: true,
        images: false,
        reasoningControls: true,
        conversationContinuation: false,
      },
      authentication: {
        providerId: "openai-codex-subscription",
        displayName: "OpenAI · ChatGPT subscription",
        accessKind: "subscription" as const,
        state: "ready" as const,
        message: "Stored credentials exist.",
        expiresAt: "2026-09-03T07:35:14Z",
        accountLabel: "sera@example.test",
        credentialSource: null,
      },
    };
    const rejected = {
      ...ready,
      authentication: {
        ...ready.authentication,
        state: "rejected" as const,
        message: "Reconnect the account.",
      },
    };

    expect(reconcileProviderAccess([ready], [ready], [rejected])).toEqual([rejected]);
    expect(reconcileProviderAccess([ready], [rejected], null)).toEqual([rejected]);
  });
});
