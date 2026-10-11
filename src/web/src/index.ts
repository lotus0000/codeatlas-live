// The part of CodeAtlas that app code imports. Without the development runtime it is plain fetch / plain Axios,
// so it is safe (and tiny) in production bundles.
import { currentRuntime, type AxiosLikeConfig, type InteractionHandle } from './registry.js'

export type { InteractionHandle } from './registry.js'

export interface Interaction {
  readonly name: string
  /** fetch that carries this interaction's trace context. Without the runtime it is plain fetch. */
  fetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response>
  /** Pass as (or spread into) the config of a request made with an instance given to instrumentAxios. */
  axiosConfig(): { codeatlas?: InteractionHandle }
}

/**
 * Links every request made through `ix` to an interaction named `name`, across any number of awaits.
 * Errors thrown by `fn` propagate unchanged.
 */
export async function trackInteraction<T>(name: string, fn: (ix: Interaction) => Promise<T>): Promise<T> {
  const runtime = currentRuntime()
  if (!runtime) return fn({ name, fetch: (input, init) => fetch(input, init), axiosConfig: () => ({}) })

  const handle = runtime.startInteraction(name)
  try {
    const result = await fn({ name, fetch: (input, init) => runtime.fetch(handle, input, init), axiosConfig: () => ({ codeatlas: handle }) })
    runtime.endInteraction(handle)
    return result
  } catch (error) {
    runtime.endInteraction(handle, error)
    throw error
  }
}

// `any` because Axios's interceptor generics differ between versions; a real AxiosInstance must fit.
type Callback = (value: any) => any
type Interceptors = {
  request: { use(onFulfilled: Callback): unknown }
  response: { use(onFulfilled: Callback, onRejected?: Callback): unknown }
}

/** The part of an Axios instance CodeAtlas needs; a real `AxiosInstance` fits. */
export interface AxiosLikeInstance {
  interceptors: Interceptors
}

/** Adds trace context to requests made with `ix.axiosConfig()`. Other requests, and everything without the runtime, are untouched. */
export function instrumentAxios(instance: AxiosLikeInstance): void {
  instance.interceptors.request.use((config: AxiosLikeConfig) => currentRuntime()?.beforeAxiosRequest(config) ?? config)
  instance.interceptors.response.use(
    (response: { status: number; config?: AxiosLikeConfig }) => {
      currentRuntime()?.afterAxiosResponse(response.config, response.status)
      return response
    },
    (error: { config?: AxiosLikeConfig; response?: { status: number } }) => {
      currentRuntime()?.afterAxiosResponse(error?.config, error?.response?.status ?? 0, error)
      return Promise.reject(error)
    },
  )
}
