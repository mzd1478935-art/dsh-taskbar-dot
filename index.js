/**
 * dsh-taskbar-dot — host half.
 *
 * One job: when a DSH session goes running -> idle (a turn finished) and the
 * DSH window is NOT in the foreground, put a small blue dot on the taskbar
 * button. The dot is drawn by the Windows shell (ITaskbarList3 overlay), so we
 * own no window, no polling loop and no resident process: a 9.5 KB helper is
 * spawned per completion and exits as soon as you look at the window again.
 *
 * Zero token cost on purpose: no tool, no system prompt section.
 */
import { spawn } from 'node:child_process'
import { copyFileSync, existsSync, mkdirSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

export const name = 'dsh-taskbar-dot'

const here = (relative) => fileURLToPath(new URL(relative, import.meta.url))

function resolveConfig(raw) {
  const input = raw && typeof raw === 'object' ? raw : {}
  const title = typeof input.windowTitle === 'string' ? input.windowTitle.trim() : ''
  return {
    enabled: input.enabled !== false,
    windowTitle: title || 'DeepSeek Harness',
  }
}

/** Copy the helper out of the package (possibly a sandboxed workspace path)
 *  into %LOCALAPPDATA%, which is where a spawned process may call Win32. */
function deployHelper() {
  const local = process.env.LOCALAPPDATA
  if (!local) return null
  const dir = join(local, 'DSH', 'taskbar-dot')
  mkdirSync(dir, { recursive: true })
  const src = here('./TaskbarDot.exe')
  if (!existsSync(src)) return null
  const dst = join(dir, 'TaskbarDot.exe')
  const stale = !existsSync(dst) || statSync(dst).size !== statSync(src).size
  if (stale) copyFileSync(src, dst)
  return dst
}

export function apply(ctx, rawConfig) {
  const config = resolveConfig(rawConfig)
  if (!config.enabled) return
  if (process.platform !== 'win32') return

  let exe = null
  try {
    exe = deployHelper()
  } catch {
    return
  }
  if (!exe) return

  let child = null
  const lastStatus = new Map()
  const args = [`title=${config.windowTitle}`]

  function spawnHelper(extra) {
    try {
      const proc = spawn(exe, extra ? extra.concat(args) : args, { windowsHide: true, stdio: 'ignore' })
      proc.on('exit', () => {
        if (child === proc) child = null
      })
      proc.unref()
      return proc
    } catch {
      return null
    }
  }

  function show() {
    // One dot at a time: a second completion while the dot is up changes
    // nothing. The helper decides by itself whether the window is in front.
    if (child && child.exitCode === null) return
    child = spawnHelper([])
  }

  ctx.on('agent/status', (payload) => {
    const id = payload?.agent?.id
    const next = payload?.status
    if (!id || !next) return
    const sid = String(id)
    const prev = lastStatus.get(sid)
    lastStatus.set(sid, next)
    if (next === 'idle' && prev === 'running') show()
  })

  ctx.effect(
    () => () => {
      // Unloading the bundle (or disabling it) must take the dot with it,
      // otherwise a cleared-looking uninstall leaves a badge behind.
      try {
        child?.kill()
      } catch {
        /* already gone */
      }
      try {
        spawnHelper(['clear'])
      } catch {
        /* best effort */
      }
    },
    'dsh-taskbar-dot: helper',
  )
}
