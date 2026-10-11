// The collector's JSON (src/CodeAtlas.Collector/Analysis/TraceViews.cs, Store/SpanRecord.cs, Source/PdbSourceMap.cs).
// Nanosecond times are strings: they exceed JavaScript's exact integer range.

export type Attributes = Record<string, unknown>

export interface SpanEvent {
  name: string
  timeUnixNano: string
  attributes: Attributes
}

export interface SpanRecord {
  traceId: string
  spanId: string
  parentSpanId: string | null
  name: string
  kind: string
  service: string
  scope: string
  startUnixNano: string
  endUnixNano: string
  status: string
  statusMessage: string | null
  attributes: Attributes
  events: SpanEvent[]
  resource: Attributes
  durationMs: number
}

/** A method identity. `evidence` is "executed" (a span of that method ran) or "routing" (route metadata only). */
export interface CodeRef {
  assembly: string
  mvid: string
  token: number
  function: string
  evidence: string
}

export interface Gap {
  kind: string
  message: string
}

export interface NodeView {
  span: SpanRecord
  kind: string
  evidence: string
  correlation: string | null
  code: CodeRef | null
  gaps: Gap[]
  children: NodeView[]
}

export interface TraceSummary {
  traceId: string
  startUnixNano: string
  durationMs: number
  interaction: string | null
  correlation: string | null
  method: string | null
  route: string | null
  statusCode: number | null
  hasError: boolean
  spanCount: number
  sampling: string
}

export interface ExplainLine {
  kind: string
  text: string
  spanIds: string[]
}

export interface TraceView {
  summary: TraceSummary
  roots: NodeView[]
  truncated: boolean
  explanation: ExplainLine[]
}

export interface SourceLocation {
  found: boolean
  reason: string | null
  file: string | null
  relativePath: string | null
  displayStart: number
  displayEnd: number
  navigationLine: number
  executableLines: number[]
  kind: string | null
  stateMachine: boolean
  mapping: string | null
}

export interface SourceResponse {
  location: SourceLocation
  text: string | null
}

export interface CollectorInfo {
  version: string
  sourceRoots: string[]
  editor: { kind: string; available: boolean }
}

export interface OpenResult {
  message: string
  file: string
  line: number
}
