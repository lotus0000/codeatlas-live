import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { apiUrl } from './client'
import type { TraceSummary } from './types'

export type LiveState = 'connecting' | 'live' | 'offline'

/** Pushes summaries of traces that changed. `start` returns a stop function. */
export interface LiveSource {
  start(onUpdate: (summaries: TraceSummary[]) => void, onState: (state: LiveState) => void): () => void
}

export const signalRLive: LiveSource = {
  start(onUpdate, onState) {
    const connection = new HubConnectionBuilder()
      .withUrl(apiUrl('live'))
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: () => 2000 })
      .configureLogging(LogLevel.None)
      .build()
    connection.on('traces-updated', onUpdate)
    connection.onreconnecting(() => onState('connecting'))
    connection.onreconnected(() => onState('live'))
    let stopped = false
    let retry: ReturnType<typeof setTimeout> | undefined
    // Automatic reconnect covers dropped connections; a failed first start (collector restarting) is retried here.
    const begin = async () => {
      onState('connecting')
      try {
        await connection.start()
        if (!stopped) onState('live')
      } catch {
        if (stopped) return
        onState('offline')
        retry = setTimeout(() => void begin(), 2000)
      }
    }
    connection.onclose(() => {
      if (stopped) return
      onState('offline')
      retry = setTimeout(() => void begin(), 2000)
    })
    void begin()
    return () => {
      stopped = true
      clearTimeout(retry)
      void connection.stop()
    }
  },
}
