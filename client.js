/**
 * @local/dsh-audio-rail — browser half: subscribes to the host's SSE band
 * stream and makes the conversation turn-rail marks (the quick-jump anchors
 * on the right edge of the chat page) pulse with the music, like a spectrum
 * analyzer rotated 90°. Each mark owns one frequency band; bass sits at the
 * bottom.
 *
 * Host DOM contract (dsh-client-ui-chat TurnNavigator): buttons carrying a
 * class token ending in `_mark` live directly inside a container whose class
 * token ends in `_marks`, ordered by their `data-index`. The host draws each
 * anchor as a fixed 20px ::before bar whose STATE length is a scaleX factor
 * (.6 default, .4 unloaded, .9 preview, 1 active) on the transform, so this
 * plugin multiplies that state factor by the audio pulse instead of
 * overriding widths — host states keep their exact look between beats.
 *
 * Pure DOM effect: no slots, no services. Degrades to a no-op when the host
 * route is unreachable, the page has no turn rail, or the user prefers
 * reduced motion.
 *
 * DOM contract verified against dsh-client-ui-chat 0.1.6-alpha.6 (fixed-pitch
 * virtual rail, 20px bar, scaleX state factors).
 */
window.__ModuleLoader__.load({
  id: '@local/dsh-audio-rail',
  factory() {
    const SCALE_VAR = '--dsh-audio-rail-scale'
    const PRODUCT_CAP = 1.3 // 20px bar × 1.3 = 26px stays inside the 28px frame
    const GROW = 1.6
    const ATTACK = 0.5
    const RELEASE = 0.2
    const CSS =
      '[class*="_marks"] [class*="_mark"]::before{' +
      'transform:translateY(-50%) scaleX(var(--dsh-audio-rail-scale,.6));' +
      'transform-origin:100%;' +
      '}'

    /** The host's state scaleX for one mark button, read from its classes. */
    function stateBase(el) {
      const tokens = typeof el.className === 'string' ? el.className.split(/\s+/) : []
      let base = 0.6
      for (const token of tokens) {
        if (token.endsWith('_markActive')) return 1.0
        if (token.endsWith('_markPreview')) base = 0.9
        else if (token.endsWith('_markUnloaded')) base = 0.4
      }
      return base
    }

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

          /** @type {{el: HTMLElement, base: number, index: number, count: number, v: number, applied: number}[]} */
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
            /** @type {{el: HTMLElement, index: number, count: number}[]} */
            const found = []
            document.querySelectorAll('[class*="_marks"]').forEach((container) => {
              /** @type {HTMLElement[]} */
              const rail = []
              container.querySelectorAll('[class*="_mark"]').forEach((el) => {
                if (!(el instanceof HTMLElement)) return
                const tokens = typeof el.className === 'string' ? el.className.split(/\s+/) : []
                if (!tokens.some((name) => name.endsWith('_mark'))) return
                rail.push(el)
              })
              // The rail's own virtual index orders marks top -> bottom.
              rail.sort((a, b) => parseInt(a.dataset.index || '0', 10) - parseInt(b.dataset.index || '0', 10))
              rail.forEach((el, index) => found.push({ el, index, count: rail.length }))
            })
            const previous = new Map(marks.map((m) => [m.el, m]))
            marks = found.map(({ el, index, count }) => {
              const base = stateBase(el)
              const old = previous.get(el)
              if (old) { old.base = base; old.index = index; old.count = count; return old }
              return { el, base, index, count, v: 0, applied: -1 }
            })
          }

          function tick() {
            rafId = requestAnimationFrame(tick)
            const bandCount = bands ? bands.length : 0
            for (const m of marks) {
              // Band 0 (bass) goes to the bottom mark of its rail.
              const target = bandCount > 0 && m.count > 0
                ? bands[Math.floor(((m.count - 1 - m.index) / m.count) * bandCount)]
                : 0
              m.v += (target - m.v) * (target > m.v ? ATTACK : RELEASE)
              const scale = Math.min(m.base * (1 + m.v * GROW), PRODUCT_CAP)
              if (Math.abs(scale - m.applied) > 0.004) {
                m.applied = scale
                m.el.style.setProperty(SCALE_VAR, scale.toFixed(3))
              }
            }
          }

          const observer = new MutationObserver(() => {
            if (refreshTimer !== 0) return
            refreshTimer = window.setTimeout(() => { refreshTimer = 0; refreshMarks() }, 300)
          })
          observer.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['class'],
          })
          const backstop = window.setInterval(refreshMarks, 2000)
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
