/**
 * dsh-taskbar-dot — client half.
 *
 * Registers a configuration card for this bundle on the Plugins page
 * ("plugins.bundle.config" keyed by package name), so the settings live where
 * every other plugin's settings live: DSH → Plugins → dsh-taskbar-dot.
 *
 * The form only edits host state through /api/dsh-taskbar-dot/*; nothing is
 * kept in the renderer and no tool/prompt surface is added.
 */
window.__ModuleLoader__.load({
  id: 'dsh-taskbar-dot',
  factory: (require) => {
    const React = require('react')
    const h = React.createElement

    const API = '/api/dsh-taskbar-dot'
    const SIZES = [
      { value: 1, label: '很小', px: 6 },
      { value: 2, label: '小', px: 7 },
      { value: 3, label: '中', px: 8 },
      { value: 4, label: '大', px: 9 },
      { value: 5, label: '很大', px: 10 }
    ]
    const SOUND_LABELS = {
      off: '关闭',
      tick: '嗒（短促）',
      blip: '哔（单声）',
      chime: '叮咚（两音）',
      pop: '啵（闷响）',
      soft: '嗯（柔和）'
    }

    async function getSettings() {
      const res = await fetch(`${API}/settings`, { headers: { accept: 'application/json' } })
      return res.json()
    }

    async function saveSettings(patch) {
      const res = await fetch(`${API}/settings`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(patch)
      })
      return res.json()
    }

    async function preview(preset) {
      await fetch(`${API}/preview`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ preset })
      })
    }

    function Row(props) {
      return h('div', { style: { marginBottom: 18 } },
        h('div', { style: { fontSize: 13, fontWeight: 600, marginBottom: 8 } }, props.label),
        props.children,
        props.hint ? h('p', { style: { margin: '6px 0 0', fontSize: 12, opacity: 0.6 } }, props.hint) : null
      )
    }

    function Card(props) {
      const [settings, setSettings] = React.useState(null)
      const [busy, setBusy] = React.useState(false)
      const [error, setError] = React.useState('')

      React.useEffect(() => {
        let alive = true
        getSettings()
          .then((doc) => { if (alive) setSettings(doc?.settings || null) })
          .catch((e) => { if (alive) setError(String(e?.message || e)) })
        return () => { alive = false }
      }, [])

      if (props && props.view === 'summary') return '任务栏蓝点：完成提示的形状、大小与提示音。'

      const update = async (patch) => {
        setBusy(true)
        setError('')
        try {
          const doc = await saveSettings(patch)
          if (doc?.settings) setSettings(doc.settings)
        } catch (e) {
          setError(String(e?.message || e))
        } finally {
          setBusy(false)
        }
      }

      if (!settings) {
        return h('div', { style: { padding: '8px 0', fontSize: 13, opacity: 0.7 } },
          error ? `读取设置失败：${error}` : '读取设置……')
      }

      const pill = (active, label, onClick, disabled) => h('button', {
        key: label,
        type: 'button',
        disabled: disabled || busy,
        onClick,
        style: {
          padding: '6px 12px', borderRadius: 8, font: 'inherit', fontSize: 13,
          cursor: disabled ? 'default' : 'pointer',
          color: 'inherit',
          border: '1px solid ' + (active ? 'currentColor' : 'rgba(128,128,128,0.35)'),
          background: active ? 'rgba(128,128,128,0.16)' : 'transparent'
        }
      }, label)

      return h('div', { style: { maxWidth: 560 } },
        h(Row, { label: '启用', hint: '关掉之后回合结束不再点亮，也不会响。' },
          h('div', { style: { display: 'flex', gap: 8 } },
            pill(settings.enabled, '开', () => update({ enabled: true })),
            pill(!settings.enabled, '关', () => update({ enabled: false }))
          )
        ),
        h(Row, { label: '点的大小', hint: '中间是现在这个尺寸，左右各两档微调。' },
          h('div', { style: { display: 'flex', gap: 8, flexWrap: 'wrap' } },
            SIZES.map((s) => pill(settings.size === s.value, `${s.label} · ${s.px}px`, () => update({ size: s.value })))
          )
        ),
        h(Row, { label: '提示音', hint: '回合结束时播一次；声音现场合成，不带音频文件。' },
          h('div', { style: { display: 'flex', gap: 8, flexWrap: 'wrap' } },
            Object.keys(SOUND_LABELS).map((key) =>
              pill(settings.sound === key, SOUND_LABELS[key], () => update({ sound: key }))
            ),
            h('button', {
              type: 'button',
              disabled: busy || settings.sound === 'off',
              onClick: () => preview(settings.sound),
              style: {
                padding: '6px 12px', borderRadius: 8, font: 'inherit', fontSize: 13,
                cursor: 'pointer', color: 'inherit', background: 'transparent',
                border: '1px dashed rgba(128,128,128,0.5)'
              }
            }, '试听')
          )
        ),
        error ? h('p', { style: { fontSize: 12, color: '#e5484d' } }, error) : null,
        h('p', { style: { marginTop: 22, fontSize: 12, opacity: 0.55 } },
          busy ? '保存中……' : '改动即时生效，写在本机 %LOCALAPPDATA%\\DSH\\taskbar-dot\\settings.json')
      )
    }

    return {
      name: 'dsh-taskbar-dot',
      inject: ['slots'],
      apply(ctx) {
        const slots = (typeof ctx.get === 'function' ? ctx.get('slots') : undefined) || ctx.slots
        if (!slots || typeof slots.inject !== 'function' || typeof slots.register !== 'function') return
        slots.inject('plugins.bundle.config', () =>
          slots.register({ name: 'plugins.bundle.config', key: 'dsh-taskbar-dot' }, Card)
        )
      }
    }
  }
})
