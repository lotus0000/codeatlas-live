// Shared between the pass-through API and the runtime. Nothing here imports OpenTelemetry.

export interface InteractionHandle {
  readonly name: string
}

export type AxiosLikeConfig = {
  url?: string
  baseURL?: string
  method?: string
  headers?: unknown
  codeatlas?: InteractionHandle
  [key: string]: unknown
}

export interface Runtime {
  startInteraction(name: string): InteractionHandle
  endInteraction(handle: InteractionHandle, error?: unknown): void
  fetch(handle: InteractionHandle, input: RequestInfo | URL, init?: RequestInit): Promise<Response>
  beforeAxiosRequest<C extends AxiosLikeConfig>(config: C): C
  afterAxiosResponse(config: AxiosLikeConfig | undefined, status: number, error?: unknown): void
}

const slot = Symbol.for('codeatlas.web.runtime')
type Slot = { [slot]?: Runtime }

export function currentRuntime(): Runtime | undefined {
  return (globalThis as Slot)[slot]
}

export function setRuntime(runtime: Runtime | undefined): void {
  ;(globalThis as Slot)[slot] = runtime
}
