/**
 * @local/dsh-audio-rail — browser half: subscribes to the host's SSE band
 * stream and makes the conversation turn-rail marks (the quick-jump anchors
 * on the right edge of the chat page) pulse with the music, like a spectrum
 * analyzer rotated 90°. Each mark owns one frequency band; bass sits at the
 * bottom. Bars grow via transform scaleX on the existing ::before artwork, so
 * every host state (active/preview/busy/unloaded) keeps its own styling.
 *
 * Pure DOM effect: no slots, no services. Degrades to a no-op when the host
 * route is unreachable, the tab has no turn rail, or the user prefers reduced
 * motion.
 */
window.__ModuleLoader__.load({
  id: '@local/dsh-audio-rail',
  factory() {
    const SCALE_VAR = '--dsh-audio-rail-scale'
    const MAX_BAR_PX = 26 // rail frame is 28px wide; keep the bar inside
    const GROW = 1.6
    const ATTACK = 0.55
    const RELEASE = 0.16
    const CSS =
      '[class*="_marks"] [class*="_mark"]::before{' +
      'transform:translateY(-50%) scaleX(var(--dsh-audio-rail-scale,1));' +
      'transform-origin:right center;' +
      'will-change:transform;' +
      '}'

    return {
      apply(ctx) {
        ctx.effect(() => {
          if (typeof window.matchMedia === 'function'
            && window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
            return () => {}
          }

          const style = document.createElement('style')
          style.dataset.plugin = '@local/dsh-audio-rail'
          style.textContent = CSS
          document.head.appendChild(style)

          /** @type {{el: HTMLElement, cap: number, v: number, applied: number}[]} */
          let marks = []
          /** @type {number[] | null} */
          let bands = null
          let rafId = 0
          let refreshTimer = 0

          const source = new EventSource('/audio-rail/events')
          source.addEventListener('bands', (event) => {
            try {
              const frame = JSON.parse(/** @type {MessageEvent} */(event).data)
              if (frame && Array.isArray(frame.b)) bands = frame.b
            } catch { /* malformed push */ }
          })

          function refreshMarks() {
            /** @type {HTMLElement[]} */
            const found = []
            document.querySelectorAll('[class*="_markPosition"]').forEach((pos) => {
              const el = pos.firstElementChild
              if (!(el instanceof HTMLElement)) return
              const classes = typeof el.className === 'string' ? el.className.split(/\s+/) : []
              if (!classes.some((name) => name.endsWith('_mark'))) return
              found.push(el)
            })
            // Top-to-bottom order; band 0 (bass) goes to the bottom mark.
            found.sort((a, b) => a.getBoundingClientRect().top - b.getBoundingClientRect().top)
            const previous = new Map(marks.map((m) => [m.el, m]))
            marks = found.map((el) => {
              const old = previous.get(el)
              if (old) return old
              let base = 12
              try {
                const width = parseFloat(window.getComputedStyle(el, '::before').width)
                if (Number.isFinite(width) && width > 0) base = width
              } catch { /* detached mid-refresh */ }
              return { el, cap: MAX_BAR_PX / base, v: 0, applied: 1 }
            })
          }

          function tick() {
            rafId = requestAnimationFrame(tick)
            const count = marks.length
            if (count === 0) return
            const bandCount = bands ? bands.length : 0
            for (let i = 0; i < count; i++) {
              const m = marks[i]
              const target = bandCount > 0
                ? bands[Math.floor(((count - 1 - i) / count) * bandCount)]
                : 0
              m.v += (target - m.v) * (target > m.v ? ATTACK : RELEASE)
              const scale = Math.min(1 + m.v * GROW, m.cap)
              if (Math.abs(scale - m.applied) > 0.004) {
                m.applied = scale
                m.el.style.setProperty(SCALE_VAR, scale.toFixed(3))
              }
            }
          }

          const observer = new MutationObserver(() => {
            if (refreshTimer !== 0) return
            refreshTimer = window.setTimeout(() => { refreshTimer = 0; refreshMarks() }, 400)
          })
          observer.observe(document.body, { childList: true, subtree: true })
          const backstop = window.setInterval(refreshMarks, 2500)
          refreshMarks()
          rafId = requestAnimationFrame(tick)

          return () => {
            cancelAnimationFrame(rafId)
            window.clearTimeout(refreshTimer)
            window.clearInterval(backstop)
            observer.disconnect()
            source.close()
            for (const m of marks) m.el.style.removeProperty(SCALE_VAR)
            marks = []
            style.remove()
          }
        }, 'dsh-audio-rail: turn-rail animation')
      },
    }
  },
})
