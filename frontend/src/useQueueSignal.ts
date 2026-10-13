import { useEffect, useRef } from 'react'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'

/**
 * Calls onChange whenever the clinic's queue changes (live via SignalR),
 * plus a slow polling fallback in case the socket drops unnoticed.
 *
 * PLAN.md 6.1: onMessage fires on the payload-free "messageSent" signal — a note landed on today's
 * front-desk ↔ clinical team thread. Both callbacks refetch through the caller's own endpoints.
 */
export function useQueueSignal(slug: string | undefined, onChange: () => void, pollMs = 30000, onMessage?: () => void) {
  const callback = useRef(onChange)
  const messageCallback = useRef(onMessage)
  useEffect(() => {
    callback.current = onChange
  }, [onChange])
  useEffect(() => {
    messageCallback.current = onMessage
  }, [onMessage])

  useEffect(() => {
    if (!slug) return
    const conn = new HubConnectionBuilder()
      .withUrl('/hubs/queue')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    let active = true
    conn.on('queueChanged', () => callback.current())
    conn.on('messageSent', () => messageCallback.current?.())
    conn.onreconnected(() => {
      conn.invoke('JoinClinic', slug).catch(() => {})
      callback.current()
    })
    conn
      .start()
      .then(() => (active ? conn.invoke('JoinClinic', slug) : undefined))
      .catch(() => {})

    const timer = window.setInterval(() => callback.current(), pollMs)
    return () => {
      active = false
      window.clearInterval(timer)
      conn.stop().catch(() => {})
    }
  }, [slug, pollMs])
}
