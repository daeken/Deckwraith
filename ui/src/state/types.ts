export type ProviderCapabilities = {
  streaming: boolean;
  nativeToolCalling: boolean;
  images: boolean;
  reasoningControls: boolean;
  conversationContinuation: boolean;
};

export type ProviderSnapshot = {
  providerId: string;
  capabilities: ProviderCapabilities;
};

export type WraithDocument = {
  name: string;
  displayLabel: string | null;
  aliases: string[];
  createdAt: string;
};

export type HauntDocument = WraithDocument;

export type DeckSnapshot = {
  wraiths: WraithDocument[];
  haunts: HauntDocument[];
  providers: ProviderSnapshot[];
  eventCursor: number;
};

export type IdentityDocument = {
  schemaVersion: number;
  name: string;
  personality: string;
  calibration: Record<string, string>;
  pronouns: string[];
  selfDescription: string;
  knownTendencies: string[];
  openQuestions: string[];
  updatedAt: string;
};

export type ShellDocument = {
  shellId: string;
  provider: string;
  model: string;
  startedAt: string;
  endedAt: string | null;
  endReason: string | null;
};

export type RunDocument = {
  runId: string;
  agent: string;
  haunt: string | null;
  objective: string;
  status: string;
  statusReason: string | null;
  shells: ShellDocument[];
  createdAt: string;
  updatedAt: string;
};

export type WraithSnapshot = {
  identity: IdentityDocument;
  context: { turn: number; revision: number; items: unknown[] } | null;
  runs: RunDocument[];
  deckbooks: { haunt: string; revision: number; cellCount: number }[];
  eventCursor: number;
};

export type DeckbookCell = {
  cell: {
    name: string;
    kind: string;
    kernel: string | null;
    revision: number;
    isStale: boolean;
    contextPolicy: string;
    lastExecution: { status: string; outputHash: string } | null;
  };
  source: string;
  output: {
    status: string;
    values: unknown[];
    standardOutput: string[];
    standardError: string[];
    errors: string[];
  } | null;
};

export type DeckbookSnapshot = {
  deckbook: { agent: string; haunt: string; revision: number };
  cells: DeckbookCell[];
};

export type ArchiveRecord = {
  sequence: number;
  eventId: string;
  timestamp: string;
  kind: string;
  payload: unknown;
  runId: string | null;
  shellId: string | null;
};

export type ArchivePage = {
  records: ArchiveRecord[];
  hasMore: boolean;
};

export type CheckpointSummary = {
  commitId: string;
  parents: string[];
  timestamp: string;
  subject: string;
};

export type HostEvent = {
  protocolVersion: number;
  cursor: number;
  name: string;
  timestamp: string;
  payload: Record<string, unknown>;
};
