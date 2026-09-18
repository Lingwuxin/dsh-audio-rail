/**
 * @local/dsh-audio-rail — host half: spawns the WASAPI loopback capture
 * helper (bin/AudioRailCapture.exe) on demand and fans its FFT band frames
 * out to browser clients over SSE at /audio-rail/events. The helper runs only
 * while at least one page is subscribed; it stops 15 s after the last client
 * disconnects and is restarted with exponential backoff if it crashes while
 * subscribers remain (e.g. audio device hot-plug, exit code 3).
 *
 * Routes (same-origin, read-only):
 *   GET /audio-rail/events  — text/event-stream, `event: bands` frames
 *   GET /audio-rail/status  — {"active":bool,"clients":n,"failures":n,...}
 * @module @local/dsh-audio-rail
 */

import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'

/** Required services: the shared webserver route registry. */
export const inject = ['webServer']

const SSE_EVENT = 'bands'
const IDLE_STOP_MS = 15_000
const HEARTBEAT_MS = 15_000
const INITIAL_BACKOFF_MS = 2_000
const MAX_BACKOFF_MS = 30_000

/**
 * Mount the capture lifecycle and routes.
 * @param ctx - context carrying webServer.
 */
export function apply(ctx) {
  const exe = fileURLToPath(new URL('./bin/AudioRailCapture.exe', import.meta.url))

  ctx.effect(() => {
    /** @type {Set<import('node:http').ServerResponse>} */
    const subscribers = new Set()
    let child = null
    let stdoutTail = ''
    let stderrTail = ''
    let failures = 0
    let restarts = 0
    let lastFrame = null
    let startedAt = 0
    let backoffMs = INITIAL_BACKOFF_MS
    let restartTimer
    let idleTimer
    let disposed = false

    const warn = (message) => ctx.logger.warn(`dsh-audio-rail: ${message}`)

    const broadcast = (line) => {
      for (const res of subscribers) {
        try { res.write(`event: ${SSE_EVENT}\ndata: ${line}\n\n`) } catch { /* closing */ }
      }
    }

    function startCapture() {
      if (disposed || child || subscribers.size === 0) return
      let proc
      try {
        proc = spawn(exe, [], { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true })
      } catch (error) {
        failures++
        warn(`spawn failed: ${error.message}`)
        scheduleRestart()
        return
      }
      child = proc
      restarts++
      startedAt = Date.now()
      stdoutTail = ''
      stderrTail = ''
      proc.stdout.setEncoding('utf8')
      proc.stdout.on('data', (chunk) => {
        stdoutTail += chunk
        let idx
        while ((idx = stdoutTail.indexOf('\n')) >= 0) {
          const line = stdoutTail.slice(0, idx).trim()
          stdoutTail = stdoutTail.slice(idx + 1)
          if (!line.startsWith('{') || !line.endsWith('}')) continue
          lastFrame = line
          broadcast(line)
        }
      })
      proc.stderr.on('data', (chunk) => {
        stderrTail = (stderrTail + chunk).slice(-2048)
      })
      proc.on('error', (error) => warn(`helper error: ${error.message}`))
      proc.on('exit', (code, signal) => {
        if (child === proc) child = null
        if (disposed) return
        const uptime = Date.now() - startedAt
        if (uptime > 10_000) backoffMs = INITIAL_BACKOFF_MS
        failures++
        const detail = stderrTail.trim()
        warn(`capture helper exited code=${code} signal=${signal} after ${uptime}ms${detail ? ` — ${detail}` : ''}`)
        scheduleRestart()
      })
      ctx.logger.info('dsh-audio-rail: capture helper started')
    }

    const scheduleRestart = () => {
      if (disposed || subscribers.size === 0) return
      clearTimeout(restartTimer)
      restartTimer = setTimeout(startCapture, backoffMs)
      backoffMs = Math.min(backoffMs * 2, MAX_BACKOFF_MS)
    }

    const stopCapture = () => {
      clearTimeout(restartTimer)
      const proc = child
      child = null
      if (proc) { try { proc.kill() } catch { /* already gone */ } }
    }

    const disposeEvents = ctx.webServer.register({
      kind: 'exact',
      path: '/audio-rail/events',
      handler(req, res) {
        if (req.method !== 'GET') { res.writeHead(405); res.end(); return }
        res.writeHead(200, {
          'content-type': 'text/event-stream; charset=utf-8',
          'cache-control': 'no-cache, no-transform',
          'x-accel-buffering': 'no',
        })
        res.write(':ok\n\n')
        subscribers.add(res)
        clearTimeout(idleTimer)
        if (lastFrame) { try { res.write(`event: ${SSE_EVENT}\ndata: ${lastFrame}\n\n`) } catch { /* closing */ } }
        startCapture()
        req.on('close', () => {
          subscribers.delete(res)
          if (subscribers.size === 0) {
            clearTimeout(idleTimer)
            idleTimer = setTimeout(() => { if (subscribers.size === 0) stopCapture() }, IDLE_STOP_MS)
          }
        })
      },
    })

    const disposeStatus = ctx.webServer.register({
      kind: 'exact',
      path: '/audio-rail/status',
      handler(req, res) {
        res.writeHead(200, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-cache' })
        res.end(JSON.stringify({ active: child !== null, clients: subscribers.size, failures, restarts }))
      },
    })

    const heartbeat = setInterval(() => {
      for (const res of subscribers) { try { res.write(':hb\n\n') } catch { /* closing */ } }
    }, HEARTBEAT_MS)

    return () => {
      disposed = true
      disposeEvents()
      disposeStatus()
      clearInterval(heartbeat)
      clearTimeout(idleTimer)
      stopCapture()
      for (const res of subscribers) { try { res.end() } catch { /* closing */ } }
      subscribers.clear()
    }
  }, 'dsh-audio-rail: /audio-rail routes + capture helper')
}
