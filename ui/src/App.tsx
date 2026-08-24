import * as Dialog from "@radix-ui/react-dialog";
import * as ScrollArea from "@radix-ui/react-scroll-area";
import * as Tabs from "@radix-ui/react-tabs";
import clsx from "clsx";
import { useCallback, useEffect, useMemo, useState } from "react";
import { assertProtocolCompatible, BridgeError, command, query, subscribe } from "./ipc/bridge";
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
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const refresh = useCallback(async () => {
    try {
      const nextDeck = await query<DeckSnapshot>("deck.snapshot");
      setInitialized(true);
      setDeck(nextDeck);
      const nextWraith = selectedWraith && nextDeck.wraiths.some((item) => item.name === selectedWraith)
        ? selectedWraith
        : nextDeck.wraiths[0]?.name ?? "";
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
    void assertProtocolCompatible().then(refresh).catch((reason: unknown) => {
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

  if (initialized === null) {
    if (error) {
      return <CenteredState eyebrow="Host unavailable" title="Deckwraith cannot connect." detail={error} />;
    }
    return <CenteredState eyebrow="Waking the deck" title="Rebuilding the durable view…" />;
  }

  if (!initialized) {
    return (
      <CenteredState
        eyebrow="A new deck"
        title="Nothing is haunting this machine yet."
        detail="Initialize a private Git-backed deck, then create the first durable identity."
        action={
          <button className="primary" disabled={busy} onClick={() => void mutate(async () => {
            await command("deck.initialize");
          })}>
            Initialize Deckwraith
          </button>
        }
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
          {deck?.wraiths.map((item) => (
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
        <div className="sensitivity">
          <span>Local & sensitive</span>
          Archive and Git history may contain secrets. Deckwraith never publishes them automatically.
        </div>
      </aside>

      <main>
        <header className="topbar">
          <div>
            <span className="eyebrow">Persistent identity</span>
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
          <Tabs.Root className="workspace-tabs" defaultValue="identity">
            <Tabs.List className="tab-list">
              {[
                ["identity", "Identity"],
                ["runs", "Runs"],
                ["deckbook", "Deckbook"],
                ["archive", "Archive"],
                ["checkpoints", "Checkpoints"],
              ].map(([value, label]) => (
                <Tabs.Trigger key={value} className="tab-trigger" value={value}>{label}</Tabs.Trigger>
              ))}
            </Tabs.List>
            <Tabs.Content className="tab-content" value="identity">
              <IdentityEditor identity={wraith.identity} busy={busy} onSave={(identity) => mutate(async () => {
                await command("identity.update", { wraith: selectedWraith, identity });
              })} />
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

function IdentityEditor({
  identity,
  busy,
  onSave,
}: {
  identity: IdentityDocument;
  busy: boolean;
  onSave: (identity: IdentityDocument) => Promise<void>;
}) {
  const [draft, setDraft] = useState(identity);
  useEffect(() => setDraft(identity), [identity]);
  const set = <K extends keyof IdentityDocument>(key: K, value: IdentityDocument[K]) =>
    setDraft((current) => ({ ...current, [key]: value }));
  const setCalibration = (key: string, value: string) =>
    setDraft((current) => ({
      ...current,
      calibration: { ...current.calibration, [key]: value },
    }));

  return (
    <div className="content-grid identity-grid">
      <section className="panel wide">
        <PanelHeading eyebrow="The whole person" title="Personality" detail="Broad identity, not a narrow style prompt." />
        <textarea className="large-text" value={draft.personality} onChange={(event) => set("personality", event.target.value)} />
      </section>
      <section className="panel">
        <PanelHeading eyebrow="Self account" title="Description" />
        <textarea value={draft.selfDescription} onChange={(event) => set("selfDescription", event.target.value)} />
        <label>Pronouns<input value={draft.pronouns.join(", ")} onChange={(event) => set("pronouns", commaList(event.target.value))} /></label>
      </section>
      <section className="panel">
        <PanelHeading eyebrow="Operational calibration" title="Voice & boundaries" />
        <label>Register<textarea value={draft.calibration.register ?? ""} onChange={(event) => setCalibration("register", event.target.value)} /></label>
        <label>Opsec<textarea value={draft.calibration.opsec ?? ""} onChange={(event) => setCalibration("opsec", event.target.value)} /></label>
      </section>
      <section className="panel">
        <PanelHeading eyebrow="Patterns" title="Known tendencies" />
        <textarea value={draft.knownTendencies.join("\n")} onChange={(event) => set("knownTendencies", lineList(event.target.value))} />
      </section>
      <section className="panel">
        <PanelHeading eyebrow="Still becoming" title="Open questions" />
        <textarea value={draft.openQuestions.join("\n")} onChange={(event) => set("openQuestions", lineList(event.target.value))} />
      </section>
      <div className="action-row wide"><button className="primary" disabled={busy} onClick={() => void onSave(draft)}>Checkpoint identity</button></div>
    </div>
  );
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
  const activeDelta = useMemo(() => events.filter((event) => event.name === "model.text-delta").slice(-12).map((event) => String(event.payload.delta ?? "")).join(""), [events]);
  return <aside className="live-rail"><div className="live-heading"><span className="pulse" /><div><b>Live activity</b><small>host event stream</small></div></div>
    {activeDelta && <div className="streaming-text">{activeDelta}</div>}
    <ScrollArea.Root className="event-scroll"><ScrollArea.Viewport><div className="event-list">{[...events].reverse().map((event) => <div className="event" key={event.cursor}><span>{event.cursor}</span><b>{event.name}</b><small>{formatDate(event.timestamp)}</small></div>)}</div></ScrollArea.Viewport><ScrollArea.Scrollbar className="scrollbar" orientation="vertical"><ScrollArea.Thumb className="thumb" /></ScrollArea.Scrollbar></ScrollArea.Root>
  </aside>;
}

function CreateEntityDialog({ label, title, placeholder, onCreate }: { label: string; title: string; placeholder: string; onCreate: (name: string) => Promise<void> }) {
  const [name, setName] = useState("");
  const [open, setOpen] = useState(false);
  return <Dialog.Root open={open} onOpenChange={setOpen}><Dialog.Trigger asChild><button className="quiet add-button">＋ {label}</button></Dialog.Trigger><Dialog.Portal><Dialog.Overlay className="dialog-overlay" /><Dialog.Content className="dialog-content"><Dialog.Title>{title}</Dialog.Title><Dialog.Description>Use a portable canonical name. It can be changed later without breaking history.</Dialog.Description><input autoFocus value={name} placeholder={placeholder} onChange={(event) => setName(event.target.value)} /><div className="action-row"><Dialog.Close asChild><button>Cancel</button></Dialog.Close><button className="primary" disabled={!name} onClick={() => { void onCreate(name).then(() => { setName(""); setOpen(false); }); }}>Create</button></div></Dialog.Content></Dialog.Portal></Dialog.Root>;
}

function CenteredState({ eyebrow, title, detail, action }: { eyebrow: string; title: string; detail?: string; action?: React.ReactNode }) {
  return <div className="centered-state"><div className="sigil">◈</div><span className="eyebrow">{eyebrow}</span><h1>{title}</h1>{detail && <p>{detail}</p>}{action}</div>;
}

function PanelHeading({ eyebrow, title, detail }: { eyebrow: string; title: string; detail?: string }) {
  return <div className="panel-heading"><span className="eyebrow">{eyebrow}</span><h2>{title}</h2>{detail && <p>{detail}</p>}</div>;
}

function Metric({ label, value }: { label: string; value: string }) { return <div className="metric"><span>{label}</span><b>{value}</b></div>; }
function StatusPill({ value }: { value: string }) { return <span className={clsx("status-pill", value)}>{value}</span>; }
function lineList(value: string) { return value.split("\n").map((item) => item.trim()).filter(Boolean); }
function commaList(value: string) { return value.split(",").map((item) => item.trim()).filter(Boolean); }
function shortId(value: string) { return value.slice(0, 10); }
function formatDate(value: string) { return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value)); }
function messageOf(value: unknown) { return value instanceof Error ? value.message : String(value); }
