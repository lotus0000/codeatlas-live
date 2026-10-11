// Types only: `import type {} from '@codeatlas/web/axios'` lets `ix.axiosConfig()` be passed as an Axios config.
import type { InteractionHandle } from './registry'
import type {} from 'axios'

declare module 'axios' {
  interface AxiosRequestConfig {
    codeatlas?: InteractionHandle
  }
}

export {}
