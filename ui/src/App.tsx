import * as Dialog from "@radix-ui/react-dialog";
import * as ScrollArea from "@radix-ui/react-scroll-area";
import * as Tabs from "@radix-ui/react-tabs";
import clsx from "clsx";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  assertProtocolCompatible,
  BridgeError,
  command,
  deleteStoredProviderApiKey,
  disconnectOpenAiSession,
  importExistingOpenAiSession,
  pickConversationAttachments,
  pickDeckFolder,
  pickProjectFolder,
  query,
  readProviderSnapshot,
  readProviderSnapshots,
  selectDeckPath,
  setProviderApiKey,
  signInOpenAiSession,
  setThemePreference,
  subscribe,
} from "./ipc/bridge";
import type { ConversationAttachment, ThemePreference } from "./ipc/bridge";
import type {
  ArchivePage,
  CheckpointSummary,
  ContextItem,
  DeckSnapshot,
  DeckbookCell,
  DeckbookSnapshot,
  HauntProjectPolicy,
  HostEvent,
  IdentityDocument,
  ProviderAuthenticationStatus,
  ProviderSnapshot,
  RunDocument,
  WraithSnapshot,
} from "./state/types";

type AsyncAction = () => Promise<void>;

export function App() {
  const [initialized, setInitialized] = useState<boolean | null>(null);
  const [deck, setDeck] = useState<DeckSnapshot | null>(null);
  const [selectedWraith, setSelectedWraith] = useState("");
  const [selectedHaunt, setSelectedHaunt] = useState("");
  const [wraith, setWraith] = useState<WraithSnapshot | null>(null);
  const [deckbook, setDeckbook] = useState<DeckbookSnapshot | null>(null);
  const [archive, setArchive] = useState<ArchivePage | null>(null);
  const [checkpoints, setCheckpoints] = useState<CheckpointSummary[]>([]);
  const [events, setEvents] = useState<HostEvent[]>([]);
  const [deckPath, setDeckPath] = useState("");
  const [deckPathDraft, setDeckPathDraft] = useState("");
  const [theme, setTheme] = useState<ThemePreference["theme"]>("system");
  const [themeTokens, setThemeTokens] = useState<Record<string, string>>({});
  const [providerAccess, setProviderAccess] = useState<ProviderSnapshot[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [turnWraith, setTurnWraith] = useState("");
  const [turnStopping, setTurnStopping] = useState(false);
  const selectedWraithRef = useRef("");
  const selectedHauntRef = useRef("");
  const turnController = useRef<AbortController | null>(null);
  const eventRefreshTimer = useRef(0);
  const activeRun = wraith ? [...wraith.runs].reverse().find((run) => !isTerminalRun(run)) : undefined;
  const conversationHaunt = activeRun?.haunt ?? selectedHaunt;
  const conversationDefaultPath = deck?.haunts.find((item) => item.name === conversationHaunt)
    ?.project?.projectPath ?? deckPath;

  const selectWraith = useCallback((name: string) => {
    selectedWraithRef.current = name;
    setSelectedWraith(name);
  }, []);

  const selectHaunt = useCallback((name: string) => {
    selectedHauntRef.current = name;
    setSelectedHaunt(name);
  }, []);

  const refresh = useCallback(async (dismissError = false) => {
    try {
      const nextDeck = await query<DeckSnapshot>("deck.snapshot");
      setInitialized(true);
      setDeck(nextDeck);
      setProviderAccess((current) => nextDeck.providers.map((provider) => ({
        ...provider,
        authentication: current.find((item) => item.providerId === provider.providerId)
          ?.authentication ?? null,
      })));
      const activeWraiths = nextDeck.wraiths.filter((item) => !item.archivedAt);
      const preferredWraith = selectedWraithRef.current;
      const preferredHaunt = selectedHauntRef.current;
      const nextWraith = preferredWraith && activeWraiths.some((item) => item.name === preferredWraith)
        ? preferredWraith
        : activeWraiths[0]?.name ?? "";
      const nextHaunt = preferredHaunt && nextDeck.haunts.some((item) => item.name === preferredHaunt)
        ? preferredHaunt
        : nextDeck.haunts[0]?.name ?? "";
      selectWraith(nextWraith);
      selectHaunt(nextHaunt);

      if (nextWraith) {
        const [nextWraithSnapshot, nextArchive, nextCheckpoints] = await Promise.all([
          query<WraithSnapshot>("wraith.snapshot", { wraith: nextWraith }),
          query<ArchivePage>("archive.snapshot", {
            wraith: nextWraith,
            afterSequence: 0,
            limit: 1000,
          }),
          query<CheckpointSummary[]>("checkpoint.snapshot", { limit: 150 }),
        ]);
        setWraith(nextWraithSnapshot);
        setArchive(nextArchive);
        setCheckpoints(nextCheckpoints);
        if (nextHaunt) {
          setDeckbook(await query<DeckbookSnapshot>("deckbook.snapshot", {
            wraith: nextWraith,
            haunt: nextHaunt,
          }));
        } else {
          setDeckbook(null);
        }
      } else {
        setWraith(null);
        setArchive(null);
        setDeckbook(null);
        setCheckpoints([]);
      }
      if (dismissError) setError("");
    } catch (reason) {
      if (reason instanceof BridgeError && reason.code === "state-conflict") {
        setInitialized(false);
        setDeck(null);
      } else {
        setError(messageOf(reason));
      }
    }
  }, [selectHaunt, selectWraith]);

  const scheduleEventRefresh = useCallback(() => {
    globalThis.clearTimeout(eventRefreshTimer.current);
    eventRefreshTimer.current = globalThis.setTimeout(() => {
      eventRefreshTimer.current = 0;
      void refresh();
    }, 80);
  }, [refresh]);

  const updateProviderAuthentication = useCallback((authentication: ProviderAuthenticationStatus) => {
    setProviderAccess((current) => current.map((provider) =>
      provider.providerId === authentication.providerId
        ? { ...provider, authentication }
        : provider));
  }, []);

  const beginModelTurn = useCallback((wraith: string) => {
    if (turnController.current) return null;
    const controller = new AbortController();
    turnController.current = controller;
    setTurnWraith(wraith);
    setTurnStopping(false);
    return controller;
  }, []);

  const finishModelTurn = useCallback((controller: AbortController) => {
    if (turnController.current !== controller) return;
    turnController.current = null;
    setTurnWraith("");
    setTurnStopping(false);
  }, []);

  const stopModelTurn = useCallback(() => {
    const controller = turnController.current;
    if (!controller || controller.signal.aborted) return;
    setTurnStopping(true);
    controller.abort();
  }, []);

  useEffect(() => {
    void assertProtocolCompatible().then((status) => {
      setDeckPath(status.deckPath);
      setDeckPathDraft(status.deckPath);
      setTheme(status.theme);
      setThemeTokens(status.themeTokens);
      return refresh(true);
    }).catch((reason: unknown) => {
      setError(messageOf(reason));
    });
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => applyTheme(theme, themeTokens), [theme, themeTokens]);

  useEffect(() => {
    if (!initialized || !deck) return;
    const unsubscribe = subscribe(
      deck.eventCursor,
      (event) => {
        setEvents((current) => appendHostEvent(current, event));
        if (eventChangesSnapshot(event)) scheduleEventRefresh();
      },
      refresh,
    );
    return () => {
      unsubscribe();
      globalThis.clearTimeout(eventRefreshTimer.current);
      eventRefreshTimer.current = 0;
    };
  }, [initialized, refresh, scheduleEventRefresh]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (initialized) void refresh(true);
  }, [selectedWraith, selectedHaunt]); // eslint-disable-line react-hooks/exhaustive-deps

  const mutate = useCallback(async (action: AsyncAction) => {
    setBusy(true);
    setError("");
    try {
      await action();
      await refresh();
    } catch (reason) {
      if (isAbortError(reason)) {
        await new Promise((resolve) => globalThis.setTimeout(resolve, 120));
      }
      try {
        await refresh();
      } catch {
        // Preserve the original mutation failure; a later event reconnect can recover the view.
      }
      const handledInline = reason instanceof BridgeError &&
        reason.code === "provider-access-required";
      setError(isAbortError(reason) || handledInline ? "" : messageOf(reason));
    } finally {
      setBusy(false);
    }
  }, [refresh]);

  const chooseDeckFolder = useCallback(async () => {
    setBusy(true);
    setError("");
    try {
      const selected = await pickDeckFolder(deckPathDraft);
      if (selected) setDeckPathDraft(selected);
    } catch (reason) {
      setError(messageOf(reason));
    } finally {
      setBusy(false);
    }
  }, [deckPathDraft]);

  if (initialized === null) {
    if (error) {
      return <CenteredState eyebrow="Host unavailable" title="Deckwraith cannot connect." detail={error} />;
    }
    return <CenteredState eyebrow="Waking the deck" title="Rebuilding the durable view…" />;
  }

  if (!initialized) {
    return (
      <DeckOnboarding
        path={deckPathDraft}
        busy={busy}
        error={error}
        onPathChange={setDeckPathDraft}
        onChooseFolder={chooseDeckFolder}
        onInitialize={() => mutate(async () => {
          const selected = await selectDeckPath(deckPathDraft);
          setDeckPath(selected.deckPath);
          setDeckPathDraft(selected.deckPath);
          if (!selected.initialized) await command("deck.initialize");
        })}
      />
    );
  }

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-mark">DW</div>
          <div><b>Deckwraith</b><span>durable collaborator runtime</span></div>
        </div>
        <div className="section-label">Wraiths</div>
        <nav className="wraith-list">
          {deck?.wraiths.filter((item) => !item.archivedAt).map((item) => (
            <button
              key={item.name}
              className={clsx("wraith-button", item.name === selectedWraith && "selected")}
              onClick={() => selectWraith(item.name)}
            >
              <span className="status-dot" />
              <span><b>{item.displayLabel ?? item.name}</b><small>{item.name}</small></span>
            </button>
          ))}
        </nav>
        <CreateEntityDialog
          label="Invite a wraith"
          title="Begin a durable collaboration"
          placeholder="vesper"
          onCreate={(name) => mutate(async () => {
            await command("wraith.create", { name });
            selectWraith(name.toLowerCase());
          })}
        />
        {deck?.wraiths.some((item) => item.archivedAt) && <>
          <div className="section-label archived-label">Archived</div>
          <nav className="wraith-list archived-list">
            {deck.wraiths.filter((item) => item.archivedAt).map((item) => (
              <button key={item.name} className="wraith-button" disabled={busy} onClick={() => void mutate(async () => {
                await command("wraith.restore", { wraith: item.name });
                selectWraith(item.name);
              })}>
                <span className="status-dot" />
                <span><b>{item.displayLabel ?? item.name}</b><small>restore identity</small></span>
              </button>
            ))}
          </nav>
        </>}
        <div className="sidebar-spacer" />
        <div className="section-label">Haunt</div>
        <select value={selectedHaunt} onChange={(event) => selectHaunt(event.target.value)}>
          <option value="">No haunt selected</option>
          {deck?.haunts.map((item) => <option key={item.name}>{item.name}</option>)}
        </select>
        <CreateEntityDialog
          label="New haunt"
          title="Create a workspace"
          placeholder="compiler-lab"
          onCreate={(name) => mutate(async () => {
            await command("haunt.create", { name });
            selectHaunt(name.toLowerCase());
          })}
        />
        {selectedHaunt && <HauntProjectDialog
          haunt={selectedHaunt}
          policy={deck?.haunts.find((item) => item.name === selectedHaunt)?.project ?? null}
          defaultPath={deckPath}
          busy={busy}
          onSave={async (settings) => {
            setBusy(true);
            setError("");
            try {
              await command("haunt.configure-project", { haunt: selectedHaunt, ...settings });
              await refresh();
            } catch (reason) {
              setError(messageOf(reason));
              throw reason;
            } finally {
              setBusy(false);
            }
          }}
        />}
        <div className="deck-location" title={deckPath}>
          <span>Deck folder</span>
          <code>{deckPath}</code>
        </div>
        <ThemeDialog
          theme={theme}
          tokens={themeTokens}
          busy={busy}
          onSave={async (nextTheme, nextTokens) => {
            setBusy(true);
            setError("");
            try {
              const saved = await setThemePreference(nextTheme, nextTokens);
              setTheme(saved.theme);
              setThemeTokens(saved.tokens);
            } catch (reason) {
              setError(messageOf(reason));
              throw reason;
            } finally {
              setBusy(false);
            }
          }}
        />
        <ProviderDialog
          providers={providerAccess}
          busy={busy}
          onRefresh={async () => {
            setProviderAccess(await readProviderSnapshots());
          }}
          onSignIn={async () => {
            setBusy(true);
            setError("");
            try {
              updateProviderAuthentication(await signInOpenAiSession());
            } catch (reason) {
              setError(messageOf(reason));
              throw reason;
            } finally {
              setBusy(false);
            }
          }}
          onImport={async () => {
            setBusy(true);
            setError("");
            try {
              updateProviderAuthentication(await importExistingOpenAiSession());
            } catch (reason) {
              setError(messageOf(reason));
              throw reason;
            } finally {
              setBusy(false);
            }
          }}
          onDisconnect={async () => {
            setBusy(true);
            setError("");
            try {
              setProviderAccess(await disconnectOpenAiSession());
            } catch (reason) {
              setError(messageOf(reason));
              throw reason;
            } finally {
              setBusy(false);
            }
          }}
          onSetApiKey={async (providerId, apiKey) => {
            setBusy(true);
            setError("");
            try {
              updateProviderAuthentication(await setProviderApiKey(providerId, apiKey));
            } catch (reason) {
              setError(messageOf(reason));
              throw reason;
            } finally {
              setBusy(false);
            }
          }}
          onDeleteApiKey={async (providerId) => {
            setBusy(true);
            setError("");
            try {
              updateProviderAuthentication(await deleteStoredProviderApiKey(providerId));
            } catch (reason) {
              setError(messageOf(reason));
              throw reason;
            } finally {
              setBusy(false);
            }
          }}
        />
        <div className="sensitivity">
          <span>Local & sensitive</span>
          Archive and Git history may contain secrets. Deckwraith never publishes them automatically.
        </div>
      </aside>

      <main>
        <header className="topbar">
          <div>
            <span className="eyebrow">Wraith</span>
            <h1>{wraith?.identity.name ?? "No wraith selected"}</h1>
          </div>
          <div className="topbar-meta">
            <Metric label="Context turns" value={String(wraith?.context?.turn ?? 0)} />
            <Metric label="Runs" value={String(wraith?.runs.length ?? 0)} />
            <Metric label="Deckbook rev" value={String(deckbook?.deckbook.revision ?? 0)} />
          </div>
        </header>

        {error && <div className="error-banner"><b>Something snagged.</b> {error}</div>}

        {wraith ? (
          <Tabs.Root
            key={`${selectedWraith}:${selectedHaunt}`}
            className="workspace-tabs"
            defaultValue="conversation"
          >
            <Tabs.List className="tab-list">
              {[
                ["conversation", "Conversation"],
                ["runs", "Runs"],
                ["deckbook", "Deckbook"],
                ["archive", "Archive"],
                ["checkpoints", "Checkpoints"],
                ["identity", "Identity file"],
              ].map(([value, label]) => (
                <Tabs.Trigger key={value} className="tab-trigger" value={value}>{label}</Tabs.Trigger>
              ))}
            </Tabs.List>
            <Tabs.Content forceMount className="tab-content conversation-tab" value="conversation">
              <ConversationPanel
                context={wraith.context}
                identity={wraith.identity}
                runs={wraith.runs}
                providers={providerAccess}
                wraith={selectedWraith}
                haunt={selectedHaunt}
                defaultPath={conversationDefaultPath}
                busy={busy}
                mutate={mutate}
                onProviderAuthentication={updateProviderAuthentication}
                turnActive={turnWraith === selectedWraith}
                turnStopping={turnStopping && turnWraith === selectedWraith}
                beginTurn={() => beginModelTurn(selectedWraith)}
                finishTurn={finishModelTurn}
                stopTurn={stopModelTurn}
              />
            </Tabs.Content>
            <Tabs.Content className="tab-content" value="identity">
              <IdentityRecord
                identity={wraith.identity}
                busy={busy}
                onArchive={() => mutate(async () => {
                  await command("wraith.archive", { wraith: selectedWraith });
                  selectWraith("");
                })}
              />
            </Tabs.Content>
            <Tabs.Content forceMount className="tab-content" value="runs">
              <RunsPanel
                runs={wraith.runs}
                providers={deck?.providers.map((provider) => provider.providerId) ?? []}
                wraith={selectedWraith}
                haunt={selectedHaunt}
                busy={busy}
                mutate={mutate}
              />
            </Tabs.Content>
            <Tabs.Content forceMount className="tab-content" value="deckbook">
              <DeckbookPanel
                snapshot={deckbook}
                wraith={selectedWraith}
                haunt={selectedHaunt}
                busy={busy}
                mutate={mutate}
              />
            </Tabs.Content>
            <Tabs.Content className="tab-content" value="archive">
              <ArchivePanel archive={archive} />
            </Tabs.Content>
            <Tabs.Content className="tab-content" value="checkpoints">
              <CheckpointPanel checkpoints={checkpoints} busy={busy} mutate={mutate} />
            </Tabs.Content>
          </Tabs.Root>
        ) : (
          <div className="empty-panel">
            <h2>Invite the first wraith</h2>
            <p>A wraith is a collaborator who owns their identity, archive, tools, notebook, and continuity across model shells.</p>
          </div>
        )}
      </main>

      <LiveRail events={events} />
      {busy && <div className="busy-line" />}
    </div>
  );
}

function IdentityRecord({
  identity,
  busy,
  onArchive,
}: {
  identity: IdentityDocument;
  busy: boolean;
  onArchive: () => Promise<void>;
}) {
  return (
    <div className="content-grid identity-record">
      <section className="panel wide identity-intro">
        <PanelHeading
          eyebrow="Durable system truth"
          title="Identity record"
          detail="Inspect this here. Deliberate changes belong in the JSON file and its Git history, not in a casual form."
        />
        <code className="identity-path">agents/{identity.name}/identity.json</code>
      </section>
      <section className="panel wide">
        <PanelHeading eyebrow="The whole person" title="Personality" detail="Broad identity, not a narrow style prompt." />
        <IdentityCopy value={identity.personality} />
      </section>
      <section className="panel">
        <PanelHeading eyebrow="Self account" title="Description" />
        <IdentityCopy value={identity.selfDescription} />
        <div className="identity-subfield"><b>Pronouns</b><IdentityList values={identity.pronouns} /></div>
      </section>
      <section className="panel">
        <PanelHeading eyebrow="Operational calibration" title="Voice & boundaries" />
        {Object.entries(identity.calibration).length ? (
          <dl className="calibration-list">
            {Object.entries(identity.calibration).sort(([left], [right]) => left.localeCompare(right)).map(([key, value]) => (
              <div key={key}><dt>{key}</dt><dd className={clsx(!value && "empty-copy")}>{value || "Not yet written."}</dd></div>
            ))}
          </dl>
        ) : <IdentityCopy value="" />}
      </section>
      <section className="panel">
        <PanelHeading eyebrow="Patterns" title="Known tendencies" />
        <IdentityList values={identity.knownTendencies} />
      </section>
      <section className="panel">
        <PanelHeading eyebrow="Still becoming" title="Open questions" />
        <IdentityList values={identity.openQuestions} />
      </section>
      <div className="action-row wide identity-actions">
        <span>Schema {identity.schemaVersion} · last updated {formatDate(identity.updatedAt)}</span>
        <button className="danger" disabled={busy} onClick={() => {
          if (window.confirm(`Archive ${identity.name}? Its history remains local and restorable.`)) {
            void onArchive();
          }
        }}>Archive wraith</button>
      </div>
    </div>
  );
}

function IdentityCopy({ value }: { value: string }) {
  return <p className={clsx("identity-copy", !value && "empty-copy")}>{value || "Not yet written."}</p>;
}

function IdentityList({ values }: { values: string[] }) {
  return values.length
    ? <ul className="identity-list">{values.map((value) => <li key={value}>{value}</li>)}</ul>
    : <p className="identity-copy empty-copy">None recorded.</p>;
}

function ConversationPanel({ context, identity, runs, providers, wraith, haunt, defaultPath, busy, mutate, onProviderAuthentication, turnActive, turnStopping, beginTurn, finishTurn, stopTurn }: {
  context: WraithSnapshot["context"];
  identity: IdentityDocument;
  runs: RunDocument[];
  providers: ProviderSnapshot[];
  wraith: string;
  haunt: string;
  defaultPath: string;
  busy: boolean;
  mutate: (action: AsyncAction) => Promise<void>;
  onProviderAuthentication: (authentication: ProviderAuthenticationStatus) => void;
  turnActive: boolean;
  turnStopping: boolean;
  beginTurn: () => AbortController | null;
  finishTurn: (controller: AbortController) => void;
  stopTurn: () => void;
}) {
  const [message, setMessage] = useState("");
  const [provider, setProvider] = useState("openai-codex-subscription");
  const [model, setModel] = useState("gpt-5.6-sol");
  const [attachments, setAttachments] = useState<ConversationAttachment[]>([]);
  const [attachmentError, setAttachmentError] = useState("");
  const [picking, setPicking] = useState(false);
  const conversationViewport = useRef<HTMLDivElement | null>(null);
  const followConversation = useRef(true);
  const active = [...runs].reverse().find((run) => !isTerminalRun(run));
  const focusedHaunt = active?.haunt ?? haunt;
  const items = context?.items ?? [];
  const sendable = !!message.trim() || attachments.length > 0;
  const providerLabel = active?.shells.at(-1)?.provider ?? provider;
  const modelLabel = active?.shells.at(-1)?.model ?? model;
  const displayName = identity.name;
  const authentication = providers.find((item) => item.providerId === provider)?.authentication;
  const lastItemId = items.at(-1)?.itemId;

  useEffect(() => {
    const viewport = conversationViewport.current;
    if (!viewport || !followConversation.current) return;
    const frame = globalThis.requestAnimationFrame(() => {
      viewport.scrollTop = viewport.scrollHeight;
    });
    return () => globalThis.cancelAnimationFrame(frame);
  }, [items.length, lastItemId]);

  const send = () => {
    const controller = beginTurn();
    if (!controller) return Promise.resolve();
    followConversation.current = true;
    return mutate(async () => {
      const text = conversationMessage(message.trim(), attachments);
      let runId = active?.runId;
      if (!runId) {
        const checked = await readProviderSnapshot(provider, controller.signal);
        if (checked.authentication) {
          onProviderAuthentication(checked.authentication);
          if (["missing", "rejected", "error"].includes(checked.authentication.state)) {
            throw new BridgeError("provider-access-required", checked.authentication.message);
          }
        }
        const objective = message.trim().split("\n")[0]?.slice(0, 120) ||
          `Review attached files in ${focusedHaunt ?? "the current context"}`;
        const started = await command<{ run: RunDocument }>("run.start", {
          wraith,
          haunt: focusedHaunt || null,
          objective,
          provider,
          model,
        }, { signal: controller.signal });
        runId = started.run.runId;
      }

      try {
        await command("run.turn", { wraith, runId, message: text }, { signal: controller.signal });
      } finally {
        setMessage("");
        setAttachments([]);
        setAttachmentError("");
      }
    }).finally(() => {
      finishTurn(controller);
    });
  };

  return <div className="conversation-layout">
    <div className="conversation-presence">
      <div>
        <span className={clsx("pulse", active && "active")} />
        <span><b>{active ? "In conversation" : "Ready to talk"}</b><small>
          {active
            ? `${focusedHaunt ? `Focused in ${focusedHaunt} · ` : ""}${providerLabel} / ${modelLabel}`
            : `${context?.turn ?? 0} durable turn${context?.turn === 1 ? "" : "s"}`}
        </small></span>
      </div>
      {active && <StatusPill value={active.status} />}
    </div>

    <ScrollArea.Root className="conversation-scroll">
      <ScrollArea.Viewport ref={conversationViewport} onScroll={(event) => {
        const viewport = event.currentTarget;
        followConversation.current =
          viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight < 80;
      }}>
        <div className="conversation-thread" aria-live="polite">
          {items.length ? items.map((item) => <ContextItemView
            key={item.itemId}
            item={item}
            wraithLabel={displayName}
          />) : <div className="conversation-empty">
            <div className="sigil">◈</div>
            <h2>Talk to {displayName}</h2>
            <p>Say hello, ask a question, steer their attention, or bring in files. Their working context lives here across disposable model shells.</p>
          </div>}
        </div>
      </ScrollArea.Viewport>
      <ScrollArea.Scrollbar className="scrollbar" orientation="vertical"><ScrollArea.Thumb className="thumb" /></ScrollArea.Scrollbar>
    </ScrollArea.Root>

    <section className="conversation-composer">
      {attachments.length > 0 && <div className="attachment-list">{attachments.map((attachment) =>
        <span className="attachment-chip" key={attachment.hash}>
          <span><b>{attachment.fileName}</b><small>{formatBytes(attachment.length)}</small></span>
          <button aria-label={`Remove ${attachment.fileName}`} onClick={() => setAttachments((current) =>
            current.filter((item) => item.hash !== attachment.hash))}>×</button>
        </span>)}</div>}
      <textarea
        value={message}
        disabled={busy}
        onChange={(event) => setMessage(event.target.value)}
        placeholder={active ? `Say something to ${displayName}…` : `Start a conversation with ${displayName}…`}
        onKeyDown={(event) => {
          if (event.key === "Enter" && (event.metaKey || event.ctrlKey) && sendable && !busy) {
            event.preventDefault();
            void send();
          }
        }}
      />
      <div className="composer-actions">
        <div className="composer-tools">
          <button className="quiet" disabled={busy || picking || !focusedHaunt} title={focusedHaunt ? "Store selected files as durable artifacts in this haunt" : "Choose a haunt before attaching files"} onClick={() => {
            if (!focusedHaunt) return;
            setPicking(true);
            setAttachmentError("");
            void pickConversationAttachments(wraith, focusedHaunt, defaultPath)
              .then((selected) => setAttachments((current) => [
                ...current,
                ...selected.filter((candidate) => !current.some((item) => item.hash === candidate.hash)),
              ]))
              .catch((reason: unknown) => setAttachmentError(messageOf(reason)))
              .finally(() => setPicking(false));
          }}>{picking ? "Attaching…" : "Attach files…"}</button>
          {!active && <details className="connection-settings">
            <summary>{providerLabel} · {modelLabel}</summary>
            <div className="connection-fields">
              <label>Provider<select value={provider} onChange={(event) => setProvider(event.target.value)}>
                {providers.map((item) => <option key={item.providerId}>{item.providerId}</option>)}
              </select></label>
              <label>Model<input value={model} onChange={(event) => setModel(event.target.value)} /></label>
            </div>
          </details>}
        </div>
        <div className="send-cluster"><small>{turnActive ? "The current turn can be stopped safely" : "⌘↵ to send"}</small>{turnActive
          ? <button className="danger conversation-send" disabled={turnStopping} onClick={stopTurn}>{turnStopping ? "Stopping…" : "Stop turn"}</button>
          : <button className="primary conversation-send" disabled={busy || !sendable} onClick={() => void send()}>{active ? "Send" : "Start conversation"}</button>}</div>
      </div>
      {!active && authentication && <div className={clsx("provider-inline-status", authentication.state)}>
        <StatusPill value={authentication.state} /><span>{authentication.message}</span>
      </div>}
      {attachmentError && <div className="setup-error"><b>Couldn’t attach that.</b> {attachmentError}</div>}
    </section>
  </div>;
}

function ContextItemView({ item, wraithLabel }: { item: ContextItem; wraithLabel: string }) {
  if (item.kind === "message") {
    const label = item.role === "user" ? "You" : item.role === "assistant" ? wraithLabel : "Context";
    return <article className={clsx("conversation-message", item.role)}>
      <div className="message-label"><b>{label}</b><small>#{item.archiveLastSequence}</small></div>
      <p>{item.text}</p>
    </article>;
  }

  if (item.kind === "compaction") {
    return <article className="context-marker"><b>Earlier context, carried forward</b><p>{item.text}</p></article>;
  }

  return <details className="tool-context">
    <summary><StatusPill value={item.status ?? item.kind} /><span>{item.tool ?? (item.kind === "toolElision" ? "Older tool activity elided" : "Tool activity")}</span></summary>
    {item.input != null && <div><b>Input</b><pre>{JSON.stringify(item.input, null, 2)}</pre></div>}
    {item.output != null && <div><b>Result</b><pre>{JSON.stringify(item.output, null, 2)}</pre></div>}
  </details>;
}

function isTerminalRun(run: RunDocument) {
  return ["completed", "cancelled", "failed"].includes(run.status);
}

function conversationMessage(message: string, attachments: ConversationAttachment[]) {
  if (attachments.length === 0) return message;
  const references = attachments.map((attachment) =>
    `- ${attachment.fileName} (${attachment.mediaType ?? "application/octet-stream"}, ${attachment.length} bytes): ${attachment.hash}`).join("\n");
  return `${message}${message ? "\n\n" : ""}Relevant files attached as durable artifacts:\n${references}\n\nUse Get-DwArtifact with the hash to read an attachment; add -AsText for text files.`;
}

function RunsPanel({ runs, providers, wraith, haunt, busy, mutate }: {
  runs: RunDocument[];
  providers: string[];
  wraith: string;
  haunt: string;
  busy: boolean;
  mutate: (action: AsyncAction) => Promise<void>;
}) {
  const [objective, setObjective] = useState("");
  const [provider, setProvider] = useState("openai-codex-subscription");
  const [model, setModel] = useState("gpt-5.6-sol");
  const active = [...runs].reverse().find((run) => !isTerminalRun(run));
  return (
    <div className="content-grid runs-grid">
      <section className="panel">
        <PanelHeading eyebrow="New objective" title="Wake a shell" detail="The shell is disposable. The wraith is not." />
        <label>Objective<textarea value={objective} onChange={(event) => setObjective(event.target.value)} /></label>
        <div className="two-up">
          <label>Provider<select value={provider} onChange={(event) => setProvider(event.target.value)}>{providers.map((item) => <option key={item}>{item}</option>)}</select></label>
          <label>Model<input value={model} onChange={(event) => setModel(event.target.value)} /></label>
        </div>
        <button className="primary" disabled={busy || !objective || !!active} onClick={() => void mutate(async () => {
          await command("run.start", { wraith, haunt: haunt || null, objective, provider, model });
          setObjective("");
        })}>Start run</button>
        {active && <p className="hint">Complete or cancel the active run before starting another.</p>}
      </section>
      <section className="panel wide">
        <PanelHeading eyebrow="Durable history" title="Runs & shell epochs" />
        <div className="run-list">
          {[...runs].reverse().map((run) => {
            const shell = run.shells.at(-1)!;
            return <article className="run-card" key={run.runId}>
              <div><StatusPill value={run.status} /><h3>{run.objective}</h3><p>{shell.provider} / {shell.model}</p></div>
              <div className="mono faint">{shortId(run.runId)} · {run.shells.length} shell{run.shells.length === 1 ? "" : "s"}</div>
              {!isTerminalRun(run) && <div className="button-cluster">
                <button disabled={busy} onClick={() => void mutate(async () => { await command("run.complete", { wraith, runId: run.runId, reason: "completed in desktop" }); })}>Complete</button>
                <button className="danger" disabled={busy} onClick={() => void mutate(async () => { await command("run.cancel", { wraith, runId: run.runId, reason: "cancelled in desktop" }); })}>Cancel</button>
              </div>}
            </article>;
          })}
        </div>
      </section>
    </div>
  );
}

function DeckbookPanel({ snapshot, wraith, haunt, busy, mutate }: {
  snapshot: DeckbookSnapshot | null;
  wraith: string;
  haunt: string;
  busy: boolean;
  mutate: (action: AsyncAction) => Promise<void>;
}) {
  const [name, setName] = useState("");
  const [kernel, setKernel] = useState("powershell");
  const [source, setSource] = useState("");
  if (!haunt) return <div className="empty-panel"><h2>Select a haunt</h2><p>Deckbooks live at the meeting point between a wraith and its workspace.</p></div>;
  return (
    <div className="deckbook-layout">
      <section className="panel add-cell">
        <PanelHeading eyebrow={`Revision ${snapshot?.deckbook.revision ?? 0}`} title="Add a cell" />
        <label>Name<input value={name} onChange={(event) => setName(event.target.value)} placeholder="inspect-archive" /></label>
        <label>Kernel<select value={kernel} onChange={(event) => setKernel(event.target.value)}><option>powershell</option><option>csharp</option></select></label>
        <label>Source<textarea className="code-input" value={source} onChange={(event) => setSource(event.target.value)} /></label>
        <button className="primary" disabled={busy || !name || !source} onClick={() => void mutate(async () => {
          await command("deckbook.insert", { wraith, haunt, name, kind: "code", source, kernel, contextPolicy: "whenRelevant" });
          setName(""); setSource("");
        })}>Insert cell</button>
      </section>
      <ScrollArea.Root className="cell-scroll"><ScrollArea.Viewport>
        <div className="cell-stack">
          {snapshot?.cells.length ? snapshot.cells.map((cell) => (
            <CellCard key={cell.cell.name} cell={cell} busy={busy} onSave={(nextSource) => mutate(async () => {
              await command("deckbook.edit", { wraith, haunt, name: cell.cell.name, source: nextSource });
            })} onRun={() => mutate(async () => {
              await command("deckbook.run-cell", { wraith, haunt, name: cell.cell.name, runId: null, input: {} });
            })} />
          )) : <div className="empty-panel compact"><h2>The deckbook is empty.</h2><p>Cells are mutable working context; execution remains append-only.</p></div>}
        </div>
      </ScrollArea.Viewport><ScrollArea.Scrollbar className="scrollbar" orientation="vertical"><ScrollArea.Thumb className="thumb" /></ScrollArea.Scrollbar></ScrollArea.Root>
    </div>
  );
}

function CellCard({ cell, busy, onSave, onRun }: { cell: DeckbookCell; busy: boolean; onSave: (source: string) => Promise<void>; onRun: () => Promise<void> }) {
  const [source, setSource] = useState(cell.source);
  useEffect(() => setSource(cell.source), [cell.source]);
  return <article className={clsx("cell-card", cell.cell.isStale && "stale")}>
    <div className="cell-gutter"><span>{cell.cell.kind}</span><b>{cell.cell.name}</b><small>{cell.cell.kernel ?? "context"} · r{cell.cell.revision}</small></div>
    <div className="cell-body">
      <textarea className="code-input" value={source} onChange={(event) => setSource(event.target.value)} />
      <div className="button-cluster"><button disabled={busy || source === cell.source} onClick={() => void onSave(source)}>Save</button><button className="primary" disabled={busy} onClick={() => void onRun()}>Run cell</button></div>
      {cell.output && <pre className="output">{JSON.stringify({ values: cell.output.values, stdout: cell.output.standardOutput, errors: cell.output.errors }, null, 2)}</pre>}
    </div>
  </article>;
}

function ArchivePanel({ archive }: { archive: ArchivePage | null }) {
  return <section className="panel archive-panel"><PanelHeading eyebrow="Append-only evidence" title="Private archive" detail={`${archive?.records.length ?? 0} records loaded`} />
    <div className="archive-list">{[...(archive?.records ?? [])].reverse().map((record) => <details key={record.eventId}>
      <summary><span className="mono faint">#{record.sequence}</span><b>{record.kind}</b><time>{formatDate(record.timestamp)}</time></summary>
      <pre>{JSON.stringify(record.payload, null, 2)}</pre>
    </details>)}</div>
  </section>;
}

function CheckpointPanel({ checkpoints, busy, mutate }: { checkpoints: CheckpointSummary[]; busy: boolean; mutate: (action: AsyncAction) => Promise<void> }) {
  return <section className="panel checkpoint-panel"><PanelHeading eyebrow="Reversibility over restriction" title="Git checkpoints" detail="Reversal creates new history and preserves the commit being inverted." />
    {checkpoints.map((checkpoint, index) => <article className="checkpoint" key={checkpoint.commitId}>
      <span className="timeline-node" /><div><b>{checkpoint.subject}</b><p className="mono">{shortId(checkpoint.commitId)} · {formatDate(checkpoint.timestamp)}</p></div>
      {index > 0 && <button className="danger" disabled={busy} onClick={() => {
        if (window.confirm(`Reverse “${checkpoint.subject}”? External side effects cannot be reversed.`)) {
          void mutate(async () => { await command("checkpoint.reverse", { commit: checkpoint.commitId }); });
        }
      }}>Reverse</button>}
    </article>)}
  </section>;
}

function LiveRail({ events }: { events: HostEvent[] }) {
  const activeModels = useMemo(() => activeLifecycleStarts(
    events, MODEL_START_EVENTS, MODEL_TERMINAL_EVENTS, modelLifecycleKey), [events]);
  const activeKernels = useMemo(() => activeLifecycleStarts(
    events, KERNEL_START_EVENTS, KERNEL_TERMINAL_EVENTS, kernelLifecycleKey), [events]);
  const activity = useMemo(() => visibleActivity(events), [events]);
  const activeModel = activeModels.at(-1);
  const modelIsActive = activeModels.length > 0;
  const activeDelta = useMemo(() => activeModel
    ? events.filter((event) => event.name === "model.text-delta" &&
        event.cursor > activeModel.cursor &&
        modelLifecycleKey(event) === modelLifecycleKey(activeModel))
      .map((event) => payloadString(event, "delta")).join("")
    : "", [activeModel, events]);
  const kernelIsActive = activeKernels.length > 0;
  const isActive = modelIsActive || kernelIsActive;
  return <aside className="live-rail"><div className="live-heading"><span className={clsx("pulse", isActive && "active")} /><div><b>Activity</b><small>{isActive ? "Work in progress" : activity.length ? "Recent work" : "Nothing running"}</small></div></div>
    {activeDelta && <div className="streaming-text" aria-live="polite">{activeDelta}</div>}
    {activity.length ? <ScrollArea.Root className="event-scroll"><ScrollArea.Viewport><div className="event-list">{[...activity].reverse().map((event) => {
      const description = describeActivity(event);
      return <div className={clsx("event", description.tone)} key={event.cursor}>
        <span className="event-mark" />
        <div><b>{description.title}</b><p>{description.detail}</p><small>{formatDate(event.timestamp)}</small></div>
      </div>;
    })}</div></ScrollArea.Viewport><ScrollArea.Scrollbar className="scrollbar" orientation="vertical"><ScrollArea.Thumb className="thumb" /></ScrollArea.Scrollbar></ScrollArea.Root>
      : <div className="activity-empty"><b>The deck is quiet.</b><p>Model turns, tool calls, notebook execution, recovery, and failures will appear here.</p></div>}
  </aside>;
}

const ACTIVITY_EVENTS = new Set([
  "host.request.failed",
  "recovery.completed",
  "model.requested",
  "model.started",
  "model.tool-call",
  "model.usage",
  "model.completed",
  "model.error",
  "kernel.started",
  "kernel.error",
  "kernel.completed",
]);
const MODEL_START_EVENTS = new Set(["model.requested", "model.started"]);
const MODEL_TERMINAL_EVENTS = new Set(["model.completed", "model.error"]);
const KERNEL_START_EVENTS = new Set(["kernel.started"]);
const KERNEL_TERMINAL_EVENTS = new Set(["kernel.completed"]);
const LIFECYCLE_START_EVENTS = new Set([...MODEL_START_EVENTS, ...KERNEL_START_EVENTS]);

function describeActivity(event: HostEvent): { title: string; detail: string; tone?: string } {
  const wraith = payloadString(event, "wraith");
  const run = payloadString(event, "runId");
  const subject = [wraith, run && shortId(run)].filter(Boolean).join(" · ");
  switch (event.name) {
    case "host.request.failed":
      return { title: "Request failed", detail: joinDetail(payloadString(event, "name"), payloadString(event, "message")), tone: "failed" };
    case "recovery.completed":
      return { title: "Recovered durable state", detail: wraith || "Startup reconciliation completed" };
    case "model.requested":
      return { title: "Contacting model", detail: subject || "Waiting for the provider", tone: "active" };
    case "model.started":
      return { title: "Model responding", detail: subject || "Response stream opened", tone: "active" };
    case "model.tool-call":
      return { title: `Tool · ${payloadString(event, "name") || "unnamed"}`, detail: subject || "The model requested a tool" };
    case "model.usage":
      return { title: "Model usage", detail: `${payloadNumber(event, "inputTokens")} in · ${payloadNumber(event, "outputTokens")} out` };
    case "model.completed":
      return payloadString(event, "finishReason") === "cancelled"
        ? { title: "Model turn stopped", detail: subject || "Cancelled safely" }
        : { title: "Model turn finished", detail: joinDetail(subject, payloadString(event, "finishReason")) };
    case "model.error":
      return { title: "Model error", detail: joinDetail(payloadString(event, "code"), payloadString(event, "message")), tone: "failed" };
    case "kernel.started":
      return { title: `Running · ${payloadString(event, "cellName") || "cell"}`, detail: joinDetail(payloadString(event, "kernelVersion"), payloadString(event, "haunt")), tone: "active" };
    case "kernel.error":
      return { title: `Cell error · ${payloadString(event, "cellName") || "cell"}`, detail: payloadString(event, "message") || "Execution failed", tone: "failed" };
    case "kernel.completed":
      return { title: `Cell finished · ${payloadString(event, "cellName") || "cell"}`, detail: payloadString(event, "status") || "Execution completed" };
    default:
      return { title: event.name, detail: subject };
  }
}

function appendHostEvent(events: HostEvent[], event: HostEvent) {
  const next = [...events, event];
  const recent = next.slice(-80);
  if (recent.length === next.length) return next;
  const firstRecentCursor = recent[0]?.cursor ?? 0;
  const activeStarts = [
    ...activeLifecycleStarts(next, MODEL_START_EVENTS, MODEL_TERMINAL_EVENTS, modelLifecycleKey),
    ...activeLifecycleStarts(next, KERNEL_START_EVENTS, KERNEL_TERMINAL_EVENTS, kernelLifecycleKey),
  ].filter((start) => start.cursor < firstRecentCursor);
  return [...activeStarts, ...recent].sort((left, right) => left.cursor - right.cursor);
}

function visibleActivity(events: HostEvent[]) {
  const activeStartCursors = new Set([
    ...activeLifecycleStarts(events, MODEL_START_EVENTS, MODEL_TERMINAL_EVENTS, modelLifecycleKey),
    ...activeLifecycleStarts(events, KERNEL_START_EVENTS, KERNEL_TERMINAL_EVENTS, kernelLifecycleKey),
  ].map((event) => event.cursor));
  return events.filter((event) => ACTIVITY_EVENTS.has(event.name) &&
    (!LIFECYCLE_START_EVENTS.has(event.name) || activeStartCursors.has(event.cursor)));
}

function activeLifecycleStarts(
  events: HostEvent[],
  startedNames: Set<string>,
  terminalNames: Set<string>,
  keyOf: (event: HostEvent) => string,
) {
  const active = new Map<string, HostEvent>();
  for (const event of events) {
    const key = keyOf(event);
    if (startedNames.has(event.name)) {
      active.set(key, event);
    } else if (terminalNames.has(event.name)) {
      active.delete(key);
    }
  }
  return [...active.values()].sort((left, right) => left.cursor - right.cursor);
}

function modelLifecycleKey(event: HostEvent) {
  return payloadString(event, "shellId") ||
    joinDetail(payloadString(event, "wraith"), payloadString(event, "runId")) ||
    "model";
}

function kernelLifecycleKey(event: HostEvent) {
  return payloadString(event, "executionId") || joinDetail(
    payloadString(event, "wraith"),
    payloadString(event, "haunt"),
    payloadString(event, "cellName"),
  ) || "kernel";
}

function payloadString(event: HostEvent, key: string) {
  const value = event.payload[key];
  return typeof value === "string" ? value : "";
}

function payloadNumber(event: HostEvent, key: string) {
  const value = event.payload[key];
  return typeof value === "number" ? value.toLocaleString() : "0";
}

function joinDetail(...values: string[]) {
  return values.filter(Boolean).join(" · ");
}

function CreateEntityDialog({ label, title, placeholder, onCreate }: { label: string; title: string; placeholder: string; onCreate: (name: string) => Promise<void> }) {
  const [name, setName] = useState("");
  const [open, setOpen] = useState(false);
  return <Dialog.Root open={open} onOpenChange={setOpen}><Dialog.Trigger asChild><button className="quiet add-button">＋ {label}</button></Dialog.Trigger><Dialog.Portal><Dialog.Overlay className="dialog-overlay" /><Dialog.Content className="dialog-content"><Dialog.Title>{title}</Dialog.Title><Dialog.Description>Use a portable canonical name. It can be changed later without breaking history.</Dialog.Description><input autoFocus value={name} placeholder={placeholder} onChange={(event) => setName(event.target.value)} /><div className="action-row"><Dialog.Close asChild><button>Cancel</button></Dialog.Close><button className="primary" disabled={!name} onClick={() => { void onCreate(name).then(() => { setName(""); setOpen(false); }); }}>Create</button></div></Dialog.Content></Dialog.Portal></Dialog.Root>;
}

type HauntProjectSettings = {
  projectPath: string;
  autoCommitEnabled: boolean;
  author: { mode: "wraith" | "fixed"; name: string | null; email: string | null };
  allowedPaths: string[];
  allowDirtyWorkingTree: boolean;
};

function HauntProjectDialog({ haunt, policy, defaultPath, busy, onSave }: {
  haunt: string;
  policy: HauntProjectPolicy | null;
  defaultPath: string;
  busy: boolean;
  onSave: (settings: HauntProjectSettings) => Promise<void>;
}) {
  const [open, setOpen] = useState(false);
  const [path, setPath] = useState(policy?.projectPath ?? "");
  const [autoCommit, setAutoCommit] = useState(policy?.autoCommitEnabled ?? false);
  const [allowDirty, setAllowDirty] = useState(policy?.allowDirtyWorkingTree ?? false);
  const [allowedPaths, setAllowedPaths] = useState((policy?.allowedPaths ?? ["."]).join("\n"));
  const [authorMode, setAuthorMode] = useState<"wraith" | "fixed">(policy?.author.mode ?? "wraith");
  const [authorName, setAuthorName] = useState(policy?.author.name ?? "");
  const [authorEmail, setAuthorEmail] = useState(policy?.author.email ?? "");
  const [pickerBusy, setPickerBusy] = useState(false);
  const [localError, setLocalError] = useState("");

  useEffect(() => {
    if (open) return;
    setPath(policy?.projectPath ?? "");
    setAutoCommit(policy?.autoCommitEnabled ?? false);
    setAllowDirty(policy?.allowDirtyWorkingTree ?? false);
    setAllowedPaths((policy?.allowedPaths ?? ["."]).join("\n"));
    setAuthorMode(policy?.author.mode ?? "wraith");
    setAuthorName(policy?.author.name ?? "");
    setAuthorEmail(policy?.author.email ?? "");
    setLocalError("");
  }, [open, policy]);

  const scopes = allowedPaths.split(/[\n,]/).map((value) => value.trim()).filter(Boolean);
  const fixedAuthorIncomplete = authorMode === "fixed" && (!authorName.trim() || !authorEmail.trim());
  const canSave = !!path.trim() && scopes.length > 0 && !fixedAuthorIncomplete && !busy && !pickerBusy;
  return <Dialog.Root open={open} onOpenChange={setOpen}>
    <Dialog.Trigger asChild>
      <button className="quiet project-settings-button">
        <span>{policy ? "Project settings" : "Choose project folder"}</span>
        {policy && <small>{policy.autoCommitEnabled ? "Auto-commit on" : "Auto-commit off"}</small>}
      </button>
    </Dialog.Trigger>
    <Dialog.Portal>
      <Dialog.Overlay className="dialog-overlay" />
      <Dialog.Content className="dialog-content project-dialog">
        <Dialog.Title>{haunt} project</Dialog.Title>
        <Dialog.Description>Connect this haunt to a working directory. File edits stay inside the allowed scopes.</Dialog.Description>
        <div className="project-form">
          <label>Project folder<div className="path-picker">
            <input value={path} onChange={(event) => setPath(event.target.value)} spellCheck={false} />
            <button disabled={busy || pickerBusy} onClick={() => {
              setPickerBusy(true);
              setLocalError("");
              void pickProjectFolder(path || defaultPath)
                .then((selected) => { if (selected) setPath(selected); })
                .catch((reason: unknown) => setLocalError(messageOf(reason)))
                .finally(() => setPickerBusy(false));
            }}>Choose…</button>
          </div></label>
          <label>Allowed paths
            <textarea value={allowedPaths} onChange={(event) => setAllowedPaths(event.target.value)} placeholder={"src\ntests"} />
            <small>One project-relative file or folder per line. Use <code>.</code> for the whole project.</small>
          </label>
          <label className="check-row">
            <input type="checkbox" checked={autoCommit} onChange={(event) => setAutoCommit(event.target.checked)} />
            <span><b>Commit successful file edits</b><small>Requires the wraith to attach a commit subject. Deckwraith never pushes.</small></span>
          </label>
          <label>Commit attribution
            <select value={authorMode} onChange={(event) => setAuthorMode(event.target.value as "wraith" | "fixed")}>
              <option value="wraith">Current wraith</option>
              <option value="fixed">Fixed identity</option>
            </select>
          </label>
          {authorMode === "fixed" && <div className="two-up">
            <label>Name<input value={authorName} onChange={(event) => setAuthorName(event.target.value)} /></label>
            <label>Email<input type="email" value={authorEmail} onChange={(event) => setAuthorEmail(event.target.value)} /></label>
          </div>}
          <label className="check-row">
            <input type="checkbox" checked={allowDirty} onChange={(event) => setAllowDirty(event.target.checked)} />
            <span><b>Permit an already-dirty repository</b><small>Only files in the edit receipt are committed; unrelated staged and unstaged work is preserved.</small></span>
          </label>
          {localError && <div className="setup-error"><b>That didn’t work.</b> {localError}</div>}
        </div>
        <div className="action-row">
          <Dialog.Close asChild><button disabled={busy || pickerBusy}>Cancel</button></Dialog.Close>
          <button className="primary" disabled={!canSave} onClick={() => {
            void onSave({
              projectPath: path.trim(),
              autoCommitEnabled: autoCommit,
              author: {
                mode: authorMode,
                name: authorMode === "fixed" ? authorName.trim() : null,
                email: authorMode === "fixed" ? authorEmail.trim() : null,
              },
              allowedPaths: scopes,
              allowDirtyWorkingTree: allowDirty,
            }).then(() => setOpen(false)).catch((reason: unknown) => setLocalError(messageOf(reason)));
          }}>Save project settings</button>
        </div>
      </Dialog.Content>
    </Dialog.Portal>
  </Dialog.Root>;
}

const THEME_TOKEN_LABELS = {
  background: "Background",
  surface: "Surface",
  surfaceRaised: "Raised surface",
  text: "Text",
  muted: "Muted text",
  accent: "Accent",
  border: "Borders",
  danger: "Danger",
  success: "Success",
} as const;

type ThemeTokenName = keyof typeof THEME_TOKEN_LABELS;

const DEFAULT_THEME_TOKENS: Record<"dark" | "light", Record<ThemeTokenName, string>> = {
  dark: {
    background: "#090b10",
    surface: "#101219",
    surfaceRaised: "#1a1d25",
    text: "#e8e9ee",
    muted: "#777a88",
    accent: "#a394d1",
    border: "#2f323d",
    danger: "#d27f75",
    success: "#8fd7b0",
  },
  light: {
    background: "#f4f1f8",
    surface: "#ffffff",
    surfaceRaised: "#ece8f2",
    text: "#211f27",
    muted: "#6c6874",
    accent: "#67589a",
    border: "#d4cedd",
    danger: "#a9433b",
    success: "#2f7a52",
  },
};

function ThemeDialog({ theme, tokens, busy, onSave }: {
  theme: ThemePreference["theme"];
  tokens: Record<string, string>;
  busy: boolean;
  onSave: (theme: ThemePreference["theme"], tokens: Record<string, string>) => Promise<void>;
}) {
  const [open, setOpen] = useState(false);
  const [draftTheme, setDraftTheme] = useState(theme);
  const [draftTokens, setDraftTokens] = useState<Record<string, string>>(tokens);
  const [localError, setLocalError] = useState("");
  useEffect(() => {
    if (open) return;
    setDraftTheme(theme);
    setDraftTokens(tokens);
    setLocalError("");
  }, [open, theme, tokens]);

  useEffect(() => {
    if (open) applyTheme(draftTheme, draftTokens);
  }, [open, draftTheme, draftTokens]);

  const palette = DEFAULT_THEME_TOKENS[draftTheme === "light" ? "light" : "dark"];
  return <Dialog.Root open={open} onOpenChange={(nextOpen) => {
    if (!nextOpen) applyTheme(theme, tokens);
    setOpen(nextOpen);
  }}>
    <Dialog.Trigger asChild>
      <button className="quiet theme-settings-button">
        <span>Appearance</span><small>{draftTheme[0].toUpperCase() + draftTheme.slice(1)}</small>
      </button>
    </Dialog.Trigger>
    <Dialog.Portal>
      <Dialog.Overlay className="dialog-overlay" />
      <Dialog.Content className="dialog-content theme-dialog">
        <Dialog.Title>Appearance</Dialog.Title>
        <Dialog.Description>Follow the Mac, choose a built-in palette, or tune semantic colors for this installation.</Dialog.Description>
        <div className="project-form">
          <label>Mode<select value={draftTheme} onChange={(event) => setDraftTheme(event.target.value as ThemePreference["theme"])}>
            <option value="system">Follow system</option>
            <option value="dark">Dark</option>
            <option value="light">Light</option>
          </select></label>
          <div className="theme-token-grid">
            {(Object.keys(THEME_TOKEN_LABELS) as ThemeTokenName[]).map((name) => <label key={name}>
              <span>{THEME_TOKEN_LABELS[name]}</span>
              <input
                type="color"
                value={draftTokens[name] ?? palette[name]}
                onChange={(event) => setDraftTokens((current) => ({ ...current, [name]: event.target.value }))}
              />
            </label>)}
          </div>
          <button className="quiet reset-theme" disabled={Object.keys(draftTokens).length === 0} onClick={() => setDraftTokens({})}>Restore built-in colors</button>
          {localError && <div className="setup-error"><b>That didn’t work.</b> {localError}</div>}
        </div>
        <div className="action-row">
          <Dialog.Close asChild><button disabled={busy}>Cancel</button></Dialog.Close>
          <button className="primary" disabled={busy} onClick={() => {
            void onSave(draftTheme, draftTokens)
              .then(() => setOpen(false))
              .catch((reason: unknown) => setLocalError(messageOf(reason)));
          }}>Save appearance</button>
        </div>
      </Dialog.Content>
    </Dialog.Portal>
  </Dialog.Root>;
}

const API_PROVIDER_CARDS = [
  { providerId: "openai-api", name: "OpenAI", environment: "OPENAI_API_KEY" },
  { providerId: "anthropic", name: "Anthropic", environment: "ANTHROPIC_API_KEY" },
  { providerId: "xai-api", name: "xAI", environment: "XAI_API_KEY" },
  { providerId: "zai-api", name: "Z.AI", environment: "ZAI_API_KEY" },
] as const;

function ProviderDialog({ providers, busy, onRefresh, onSignIn, onImport, onDisconnect, onSetApiKey, onDeleteApiKey }: {
  providers: ProviderSnapshot[];
  busy: boolean;
  onRefresh: () => Promise<void>;
  onSignIn: () => Promise<void>;
  onImport: () => Promise<void>;
  onDisconnect: () => Promise<void>;
  onSetApiKey: (providerId: string, apiKey: string) => Promise<void>;
  onDeleteApiKey: (providerId: string) => Promise<void>;
}) {
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [localError, setLocalError] = useState("");
  const provider = providers.find((item) => item.providerId === "openai-codex-subscription");
  const authentication = provider?.authentication;
  const state = authentication?.state ?? "missing";
  const connected = ["ready", "expiring", "expired", "refreshing", "rejected"].includes(state);
  const managedIds = new Set([
    "openai-codex-subscription",
    ...API_PROVIDER_CARDS.map((item) => item.providerId),
  ]);
  const managed = providers.filter((item) => managedIds.has(item.providerId));
  const readyCount = managed.filter((item) =>
    item.authentication?.state === "ready" || item.authentication?.state === "expiring").length;
  useEffect(() => {
    if (!open) setLocalError("");
  }, [open]);

  return <Dialog.Root open={open} onOpenChange={(nextOpen) => {
    setOpen(nextOpen);
    if (nextOpen) {
      setLoading(true);
      setLocalError("");
      void onRefresh()
        .catch((reason: unknown) => setLocalError(messageOf(reason)))
        .finally(() => setLoading(false));
    }
  }}>
    <Dialog.Trigger asChild>
      <button className="quiet provider-settings-button">
        <span>Provider access</span>
        <small className={clsx("provider-state", readyCount === managedIds.size && "ready")}>
          {readyCount}/{managedIds.size} ready
        </small>
      </button>
    </Dialog.Trigger>
    <Dialog.Portal>
      <Dialog.Overlay className="dialog-overlay" />
      <Dialog.Content className="dialog-content provider-dialog">
        <Dialog.Title>Provider access</Dialog.Title>
        <Dialog.Description>Connections belong to this installation, outside every deck, snapshot, and Git history.</Dialog.Description>
        {loading && <div className="provider-loading">Checking installation credentials…</div>}
        <div className="provider-card">
          <div className="provider-card-heading">
            <div><b>OpenAI</b><span>ChatGPT subscription</span></div>
            <StatusPill value={state} />
          </div>
          <p>{authentication?.message ?? "Connect a ChatGPT account to use subscription access."}</p>
          {authentication?.accountLabel && <small>Account: {authentication.accountLabel}</small>}
          {authentication?.expiresAt && <small>Access token expires {formatDate(authentication.expiresAt)}</small>}
          <div className="provider-note">
            Deckwraith opens OpenAI in your browser, receives the private callback on localhost, and keeps the resulting session in the Mac Keychain. Inference does not start Codex or a local proxy.
          </div>
          <div className="button-cluster">
            <button className="primary" disabled={busy || loading} onClick={() => {
              void onSignIn().catch((reason: unknown) => setLocalError(messageOf(reason)));
            }}>{connected ? "Connect a different ChatGPT account" : "Connect with ChatGPT"}</button>
            <button className="quiet" disabled={busy || loading} onClick={() => {
              void onImport().catch((reason: unknown) => setLocalError(messageOf(reason)));
            }}>Import an existing Codex sign-in</button>
            {connected && <button className="danger" disabled={busy || loading} onClick={() => {
              void onDisconnect().catch((reason: unknown) => setLocalError(messageOf(reason)));
            }}>Disconnect</button>}
          </div>
        </div>
        <div className="provider-section-heading">
          <b>API access</b>
          <span>Stored keys take precedence over process environment variables.</span>
        </div>
        <div className="provider-api-grid">
          {API_PROVIDER_CARDS.map((configuration) => <ApiKeyProviderCard
            key={configuration.providerId}
            configuration={configuration}
            provider={providers.find((item) => item.providerId === configuration.providerId)}
            busy={busy || loading}
            onSave={onSetApiKey}
            onDelete={onDeleteApiKey}
          />)}
        </div>
        {localError && <div className="setup-error"><b>That didn’t work.</b> {localError}</div>}
        <div className="action-row"><Dialog.Close asChild><button disabled={busy}>Done</button></Dialog.Close></div>
      </Dialog.Content>
    </Dialog.Portal>
  </Dialog.Root>;
}

function ApiKeyProviderCard({ configuration, provider, busy, onSave, onDelete }: {
  configuration: typeof API_PROVIDER_CARDS[number];
  provider: ProviderSnapshot | undefined;
  busy: boolean;
  onSave: (providerId: string, apiKey: string) => Promise<void>;
  onDelete: (providerId: string) => Promise<void>;
}) {
  const [apiKey, setApiKey] = useState("");
  const [localError, setLocalError] = useState("");
  const authentication = provider?.authentication;
  const state = authentication?.state ?? "missing";
  const stored = !!authentication?.credentialSource &&
    authentication.credentialSource !== configuration.environment;
  return <div className="provider-card api-provider-card">
    <div className="provider-card-heading">
      <div><b>{configuration.name}</b><span>API key</span></div>
      <StatusPill value={state} />
    </div>
    <p>{authentication?.message ?? "Add an API key to use this provider."}</p>
    {authentication?.credentialSource && <small>Source: {authentication.credentialSource}</small>}
    <label className="api-key-entry">
      <span>{stored ? "Replace stored key" : "Store an API key"}</span>
      <input
        type="password"
        value={apiKey}
        autoComplete="off"
        spellCheck={false}
        placeholder="Paste key"
        onChange={(event) => setApiKey(event.target.value)}
      />
    </label>
    <div className="button-cluster">
      <button className="primary" disabled={busy || !apiKey.trim()} onClick={() => {
        setLocalError("");
        void onSave(configuration.providerId, apiKey.trim())
          .then(() => setApiKey(""))
          .catch((reason: unknown) => setLocalError(messageOf(reason)));
      }}>{stored ? "Replace key" : "Store key"}</button>
      {stored && <button className="danger" disabled={busy} onClick={() => {
        setLocalError("");
        void onDelete(configuration.providerId)
          .catch((reason: unknown) => setLocalError(messageOf(reason)));
      }}>Remove stored key</button>}
    </div>
    {localError && <div className="setup-error"><b>That didn’t work.</b> {localError}</div>}
  </div>;
}

function DeckOnboarding({ path, busy, error, onPathChange, onChooseFolder, onInitialize }: {
  path: string;
  busy: boolean;
  error: string;
  onPathChange: (value: string) => void;
  onChooseFolder: () => Promise<void>;
  onInitialize: () => Promise<void>;
}) {
  return <div className="deck-onboarding">
    <div className="sigil">◈</div>
    <span className="eyebrow">A new deck</span>
    <h1>Where should the deck live?</h1>
    <p>Deckwraith keeps identity, archives, notebooks, and Git history together in one private folder.</p>
    <section className="deck-setup-card">
      <label>Deck folder<div className="path-picker">
        <input value={path} onChange={(event) => onPathChange(event.target.value)} spellCheck={false} />
        <button disabled={busy} onClick={() => void onChooseFolder()}>Choose…</button>
      </div></label>
      <p>The default is <code>~/.deckwraith</code>. You can also open an existing deck.</p>
      {error && <div className="setup-error"><b>That didn’t work.</b> {error}</div>}
      <button className="primary" disabled={busy || !path.trim()} onClick={() => void onInitialize()}>
        Open or create deck
      </button>
    </section>
  </div>;
}

function CenteredState({ eyebrow, title, detail, action }: { eyebrow: string; title: string; detail?: string; action?: React.ReactNode }) {
  return <div className="centered-state"><div className="sigil">◈</div><span className="eyebrow">{eyebrow}</span><h1>{title}</h1>{detail && <p>{detail}</p>}{action}</div>;
}

function PanelHeading({ eyebrow, title, detail }: { eyebrow: string; title: string; detail?: string }) {
  return <div className="panel-heading"><span className="eyebrow">{eyebrow}</span><h2>{title}</h2>{detail && <p>{detail}</p>}</div>;
}

function Metric({ label, value }: { label: string; value: string }) { return <div className="metric"><span>{label}</span><b>{value}</b></div>; }
function StatusPill({ value }: { value: string }) { return <span className={clsx("status-pill", value)}>{value}</span>; }
function providerStateLabel(value: string) {
  return ({
    missing: "Not connected",
    ready: "Ready",
    expiring: "Refresh soon",
    expired: "Expired",
    refreshing: "Refreshing",
    rejected: "Reconnect",
    error: "Needs attention",
  } as Record<string, string>)[value] ?? value;
}
function shortId(value: string) { return value.slice(0, 10); }
function formatDate(value: string) { return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value)); }
function formatBytes(value: number) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(value < 10 * 1024 ? 1 : 0)} KB`;
  return `${(value / (1024 * 1024)).toFixed(1)} MB`;
}
function messageOf(value: unknown) { return value instanceof Error ? value.message : String(value); }
function isAbortError(value: unknown) {
  return value instanceof DOMException && value.name === "AbortError";
}

function eventChangesSnapshot(event: HostEvent) {
  if (["host.request.completed", "host.request.failed"].includes(event.name)) {
    return event.payload.kind === "command";
  }
  return [
    "model.started",
    "model.completed",
    "model.error",
    "kernel.completed",
    "kernel.error",
    "recovery.completed",
  ].includes(event.name);
}

function applyTheme(theme: ThemePreference["theme"], tokens: Record<string, string>) {
  const root = document.documentElement;
  root.dataset.theme = theme;
  for (const name of Object.keys(THEME_TOKEN_LABELS) as ThemeTokenName[]) {
    const property = `--dw-${name.replace(/[A-Z]/g, (character) => `-${character.toLowerCase()}`)}`;
    if (tokens[name]) root.style.setProperty(property, tokens[name]);
    else root.style.removeProperty(property);
  }
}
