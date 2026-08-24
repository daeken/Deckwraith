import * as Dialog from "@radix-ui/react-dialog";
import * as ScrollArea from "@radix-ui/react-scroll-area";
import * as Tabs from "@radix-ui/react-tabs";
import clsx from "clsx";
import { useCallback, useEffect, useMemo, useState } from "react";
import {
  assertProtocolCompatible,
  BridgeError,
  command,
  pickDeckFolder,
  query,
  selectDeckPath,
  subscribe,
} from "./ipc/bridge";
import type {
  ArchivePage,
  CheckpointSummary,
  DeckSnapshot,
  DeckbookCell,
  DeckbookSnapshot,
  HostEvent,
  IdentityDocument,
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
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const refresh = useCallback(async () => {
    try {
      const nextDeck = await query<DeckSnapshot>("deck.snapshot");
      setInitialized(true);
      setDeck(nextDeck);
      const activeWraiths = nextDeck.wraiths.filter((item) => !item.archivedAt);
      const nextWraith = selectedWraith && activeWraiths.some((item) => item.name === selectedWraith)
        ? selectedWraith
        : activeWraiths[0]?.name ?? "";
      const nextHaunt = selectedHaunt && nextDeck.haunts.some((item) => item.name === selectedHaunt)
        ? selectedHaunt
        : nextDeck.haunts[0]?.name ?? "";
      setSelectedWraith(nextWraith);
      setSelectedHaunt(nextHaunt);

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
      setError("");
    } catch (reason) {
      if (reason instanceof BridgeError && reason.code === "state-conflict") {
        setInitialized(false);
        setDeck(null);
      } else {
        setError(messageOf(reason));
      }
    }
  }, [selectedHaunt, selectedWraith]);

  useEffect(() => {
    void assertProtocolCompatible().then((status) => {
      setDeckPath(status.deckPath);
      setDeckPathDraft(status.deckPath);
      return refresh();
    }).catch((reason: unknown) => {
      setError(messageOf(reason));
    });
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!initialized || !deck) return;
    return subscribe(
      deck.eventCursor,
      (event) => setEvents((current) => [...current.slice(-79), event]),
      refresh,
    );
  }, [initialized]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (initialized) void refresh();
  }, [selectedWraith, selectedHaunt]); // eslint-disable-line react-hooks/exhaustive-deps

  const mutate = useCallback(async (action: AsyncAction) => {
    setBusy(true);
    setError("");
    try {
      await action();
      await refresh();
    } catch (reason) {
      setError(messageOf(reason));
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
          <div><b>Deckwraith</b><span>durable agent runtime</span></div>
        </div>
        <div className="section-label">Wraiths</div>
        <nav className="wraith-list">
          {deck?.wraiths.filter((item) => !item.archivedAt).map((item) => (
            <button
              key={item.name}
              className={clsx("wraith-button", item.name === selectedWraith && "selected")}
              onClick={() => setSelectedWraith(item.name)}
            >
              <span className="status-dot" />
              <span><b>{item.displayLabel ?? item.name}</b><small>{item.name}</small></span>
            </button>
          ))}
        </nav>
        <CreateEntityDialog
          label="New wraith"
          title="Create a durable identity"
          placeholder="vesper"
          onCreate={(name) => mutate(async () => {
            await command("wraith.create", { name });
            setSelectedWraith(name.toLowerCase());
          })}
        />
        {deck?.wraiths.some((item) => item.archivedAt) && <>
          <div className="section-label archived-label">Archived</div>
          <nav className="wraith-list archived-list">
            {deck.wraiths.filter((item) => item.archivedAt).map((item) => (
              <button key={item.name} className="wraith-button" disabled={busy} onClick={() => void mutate(async () => {
                await command("wraith.restore", { wraith: item.name });
                setSelectedWraith(item.name);
              })}>
                <span className="status-dot" />
                <span><b>{item.displayLabel ?? item.name}</b><small>restore identity</small></span>
              </button>
            ))}
          </nav>
        </>}
        <div className="sidebar-spacer" />
        <div className="section-label">Haunt</div>
        <select value={selectedHaunt} onChange={(event) => setSelectedHaunt(event.target.value)}>
          <option value="">No haunt selected</option>
          {deck?.haunts.map((item) => <option key={item.name}>{item.name}</option>)}
        </select>
        <CreateEntityDialog
          label="New haunt"
          title="Create a workspace"
          placeholder="compiler-lab"
          onCreate={(name) => mutate(async () => {
            await command("haunt.create", { name });
            setSelectedHaunt(name.toLowerCase());
          })}
        />
        <div className="deck-location" title={deckPath}>
          <span>Deck folder</span>
          <code>{deckPath}</code>
        </div>
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
          <Tabs.Root className="workspace-tabs" defaultValue="runs">
            <Tabs.List className="tab-list">
              {[
                ["runs", "Runs"],
                ["deckbook", "Deckbook"],
                ["archive", "Archive"],
                ["checkpoints", "Checkpoints"],
                ["identity", "Identity file"],
              ].map(([value, label]) => (
                <Tabs.Trigger key={value} className="tab-trigger" value={value}>{label}</Tabs.Trigger>
              ))}
            </Tabs.List>
            <Tabs.Content className="tab-content" value="identity">
              <IdentityRecord
                identity={wraith.identity}
                busy={busy}
                onArchive={() => mutate(async () => {
                  await command("wraith.archive", { wraith: selectedWraith });
                  setSelectedWraith("");
                })}
              />
            </Tabs.Content>
            <Tabs.Content className="tab-content" value="runs">
              <RunsPanel
                runs={wraith.runs}
                providers={deck?.providers.map((provider) => provider.providerId) ?? []}
                wraith={selectedWraith}
                haunt={selectedHaunt}
                busy={busy}
                mutate={mutate}
              />
            </Tabs.Content>
            <Tabs.Content className="tab-content" value="deckbook">
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
            <h2>Create the first wraith</h2>
            <p>A wraith owns its identity, archive, tools, notebook, and continuity across model shells.</p>
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
  const [message, setMessage] = useState("");
  const active = [...runs].reverse().find((run) => !["completed", "cancelled", "failed"].includes(run.status));
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
      <section className="panel">
        <PanelHeading eyebrow="Continue" title={active ? active.objective : "No active run"} />
        <textarea value={message} disabled={!active} onChange={(event) => setMessage(event.target.value)} placeholder="Give the next input…" />
        <button className="primary" disabled={busy || !active || !message} onClick={() => void mutate(async () => {
          await command("run.turn", { wraith, runId: active!.runId, message });
          setMessage("");
        })}>Send turn</button>
      </section>
      <section className="panel wide">
        <PanelHeading eyebrow="Durable history" title="Runs & shell epochs" />
        <div className="run-list">
          {[...runs].reverse().map((run) => {
            const shell = run.shells.at(-1)!;
            return <article className="run-card" key={run.runId}>
              <div><StatusPill value={run.status} /><h3>{run.objective}</h3><p>{shell.provider} / {shell.model}</p></div>
              <div className="mono faint">{shortId(run.runId)} · {run.shells.length} shell{run.shells.length === 1 ? "" : "s"}</div>
              {!["completed", "cancelled", "failed"].includes(run.status) && <div className="button-cluster">
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
  const activity = useMemo(() => events.filter((event) => ACTIVITY_EVENTS.has(event.name)), [events]);
  const latestModelStart = lastEventCursor(events, "model.started");
  const latestModelEnd = Math.max(lastEventCursor(events, "model.completed"), lastEventCursor(events, "model.error"));
  const modelIsActive = latestModelStart > latestModelEnd;
  const activeDelta = useMemo(() => modelIsActive
    ? events.filter((event) => event.name === "model.text-delta" && event.cursor > latestModelStart)
      .map((event) => payloadString(event, "delta")).join("")
    : "", [events, latestModelStart, modelIsActive]);
  const kernelIsActive = lastEventCursor(events, "kernel.started") > lastEventCursor(events, "kernel.completed");
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
  "model.started",
  "model.tool-call",
  "model.usage",
  "model.completed",
  "model.error",
  "kernel.started",
  "kernel.error",
  "kernel.completed",
]);

function describeActivity(event: HostEvent): { title: string; detail: string; tone?: string } {
  const wraith = payloadString(event, "wraith");
  const run = payloadString(event, "runId");
  const subject = [wraith, run && shortId(run)].filter(Boolean).join(" · ");
  switch (event.name) {
    case "host.request.failed":
      return { title: "Request failed", detail: joinDetail(payloadString(event, "name"), payloadString(event, "message")), tone: "failed" };
    case "recovery.completed":
      return { title: "Recovered durable state", detail: wraith || "Startup reconciliation completed" };
    case "model.started":
      return { title: "Model turn started", detail: subject || "Preparing inference", tone: "active" };
    case "model.tool-call":
      return { title: `Tool · ${payloadString(event, "name") || "unnamed"}`, detail: subject || "The model requested a tool" };
    case "model.usage":
      return { title: "Model usage", detail: `${payloadNumber(event, "inputTokens")} in · ${payloadNumber(event, "outputTokens")} out` };
    case "model.completed":
      return { title: "Model turn finished", detail: joinDetail(subject, payloadString(event, "finishReason")) };
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

function lastEventCursor(events: HostEvent[], name: string) {
  for (let index = events.length - 1; index >= 0; index -= 1) {
    if (events[index].name === name) return events[index].cursor;
  }
  return 0;
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
function shortId(value: string) { return value.slice(0, 10); }
function formatDate(value: string) { return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value)); }
function messageOf(value: unknown) { return value instanceof Error ? value.message : String(value); }
