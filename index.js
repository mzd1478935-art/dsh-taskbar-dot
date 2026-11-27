/**
 * dsh-taskbar-dot — host half.
 *
 * On running -> idle it spawns a ~10 KB helper that (optionally) plays a short
 * synthesized tone and paints a small blue dot on the Windows taskbar button.
 * The dot is drawn by explorer via ITaskbarList3, so there is no resident
 * process, no polling loop and no token cost.
 *
 * Settings live in %LOCALAPPDATA%\DSH\taskbar-dot\settings.json and are edited
 * from the in-app panel (client half). Defaults keep the original behaviour.
 */
import { spawn } from 'node:child_process'
import { appendFileSync, copyFileSync, existsSync, mkdirSync, readFileSync, renameSync, statSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

export const name = 'dsh-taskbar-dot'

const here = (relative) => fileURLToPath(new URL(relative, import.meta.url))

const SOUNDS = ['off', 'tick', 'blip', 'chime', 'pop', 'soft']
const DEFAULTS = { enabled: true, size: 3, sound: 'chime', windowTitle: 'DeepSeek Harness' }

function dataDir() {
  const local = process.env.LOCALAPPDATA
  if (!local) throw new Error('LOCALAPPDATA not set')
  return join(local, 'DSH', 'taskbar-dot')
}

function settingsPath() {
  return join(dataDir(), 'settings.json')
}

function clampSize(value) {
  const n = Number(value)
  if (!Number.isFinite(n)) return DEFAULTS.size
  return Math.max(1, Math.min(5, Math.round(n)))
}

function normalize(raw) {
  const input = raw && typeof raw === 'object' ? raw : {}
  const sound = SOUNDS.includes(input.sound) ? input.sound : DEFAULTS.sound
  const title = typeof input.windowTitle === 'string' && input.windowTitle.trim() ? input.windowTitle.trim() : DEFAULTS.windowTitle
  return { enabled: input.enabled !== false, size: clampSize(input.size), sound, windowTitle: title }
}

function readSettings() {
  try {
    return normalize(JSON.parse(readFileSync(settingsPath(), 'utf8')))
  } catch {
    return normalize(DEFAULTS)
  }
}

function writeSettings(patch) {
  const next = normalize({ ...readSettings(), ...(patch && typeof patch === 'object' ? patch : {}) })
  const dir = dataDir()
  mkdirSync(dir, { recursive: true })
  const tmp = `${settingsPath()}.tmp`
  writeFileSync(tmp, JSON.stringify(next, null, 2), 'utf8')
  renameSync(tmp, settingsPath())
  return next
}

/** Copy the helper out of the package into %LOCALAPPDATA%, where a spawned
 *  process may call Win32 (a workspace path can be sandboxed). */
function deployHelper() {
  const src = here('./TaskbarDot.exe')
  if (!existsSync(src)) return null
  const dir = dataDir()
  mkdirSync(dir, { recursive: true })
  const dst = join(dir, 'TaskbarDot.exe')
  const stale = !existsSync(dst) || statSync(dst).size !== statSync(src).size
  if (stale) copyFileSync(src, dst)
  return dst
}

function readBody(req) {
  return new Promise((resolve) => {
    let data = ''
    req.on('data', (chunk) => {
      data += chunk
      if (data.length > 1e6) req.destroy()
    })
    req.on('end', () => {
      try {
        resolve(data ? JSON.parse(data) : undefined)
      } catch {
        resolve(undefined)
      }
    })
    req.on('error', () => resolve(undefined))
  })
}

function isLoopback(req) {
  const addr = req.socket?.remoteAddress ?? ''
  return addr === '127.0.0.1' || addr === '::1' || addr === '::ffff:127.0.0.1'
}

function sendJson(res, code, body) {
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8' })
  res.end(JSON.stringify(body))
}

export function apply(ctx, rawConfig) {
  const boot = normalize({ ...DEFAULTS, ...(rawConfig && typeof rawConfig === 'object' ? rawConfig : {}) })
  if (process.platform !== 'win32') return

  let exe = null
  try {
    exe = deployHelper()
  } catch {
    return
  }
  if (!exe) return

  try {
    appendFileSync(join(dataDir(), 'host.log'), `${new Date().toISOString()} apply v0.2.1 pid=${process.pid} size=${boot.size} sound=${boot.sound}\n`)
  } catch {
    /* logging is optional */
  }

  let child = null
  const lastStatus = new Map()
  const runningSince = new Map()
  // 子代理 / 后台运行的 id 是裸 UUID，不是侧栏里的会话；它们的 running->idle
  // 只是主回合中间的一步，绝不能因此点亮。主会话是 session-<uuid>。
  const BARE_UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
  const isChildRun = (id) => BARE_UUID.test(String(id))

  function logLine(message) {
    try {
      appendFileSync(join(dataDir(), 'host.log'), `${new Date().toISOString()} ${message}\n`)
    } catch {
      /* logging is optional */
    }
  }

  function spawnHelper(extra) {
    try {
      const proc = spawn(exe, extra, { windowsHide: true, stdio: 'ignore' })
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
    const settings = readSettings()
    if (!settings.enabled) return
    if (child && child.exitCode === null) {
      // 点已经亮着：这一回合只补一声提示音，不叠新的点/进程
      if (settings.sound !== 'off') spawnHelper([`soundonly=${settings.sound}`])
      return
    }
    const args = [`title=${settings.windowTitle}`, `size=${settings.size}`, `sound=${settings.sound}`]
    child = spawnHelper(args)
  }

  ctx.on('agent/status', (payload) => {
    const id = payload?.agent?.id
    const next = payload?.status
    if (!id || !next) return
    const sid = String(id)
    const prev = lastStatus.get(sid)
    lastStatus.set(sid, next)
    if (next === 'running') {
      if (prev !== 'running') runningSince.set(sid, Date.now())
      return
    }
    if (next !== 'idle' || prev !== 'running') return
    if (isChildRun(sid)) {
      logLine(`idle ${sid} child run -> skipped`)
      return
    }
    const agent = payload?.agent || {}
    if (agent.parentId || agent.origin === 'subagent' || agent.kind === 'subagent') {
      logLine(`idle ${sid} subagent metadata -> skipped`)
      return
    }
    // 一闪而过的 running（加载/重连留下的瞬时状态）不算一回合
    const since = runningSince.get(sid) || 0
    const elapsed = since ? Date.now() - since : 0
    if (elapsed < 600) {
      logLine(`idle ${sid} ran ${elapsed}ms -> skipped (too short)`)
      return
    }
    logLine(`idle ${sid} main session (${elapsed}ms) -> show`)
    show()
  })

  // Settings + preview API for the in-app panel.
  let disposeRoute = null
  ctx.inject(['webServer'], (sub) => {
    const server = sub.webServer
    if (!server || typeof server.register !== 'function') return
    sub.effect(
      () =>
        server.register({
          kind: 'prefix',
          path: '/api/dsh-taskbar-dot',
          handler: (req, res) => {
            const run = async () => {
              if (!isLoopback(req)) return sendJson(res, 403, { ok: false, error: 'loopback only' })
              const url = new URL(req.url || '/', 'http://placeholder')
              const path = url.pathname.replace(/^\/api\/dsh-taskbar-dot/, '') || '/'
              if (path === '/settings' && req.method === 'GET') {
                return sendJson(res, 200, { ok: true, settings: readSettings(), sounds: SOUNDS })
              }
              if (path === '/settings' && req.method === 'POST') {
                const body = await readBody(req)
                const saved = writeSettings(body)
                // 关掉总开关时立刻把已经亮着的点撤掉（否则要等用户切回窗口）
                if (!saved.enabled) spawnHelper(['clear'])
                return sendJson(res, 200, { ok: true, settings: saved })
              }
              if (path === '/preview' && req.method === 'POST') {
                const body = await readBody(req)
                const preset = SOUNDS.includes(body?.preset) ? body.preset : 'chime'
                if (preset !== 'off') spawnHelper([`soundonly=${preset}`])
                return sendJson(res, 200, { ok: true, preset })
              }
              return sendJson(res, 404, { ok: false, error: 'not found' })
            }
            Promise.resolve(run()).catch((error) => {
              try {
                sendJson(res, 500, { ok: false, error: error?.message || String(error) })
              } catch { /* already sent */ }
            })
          },
        }),
      'dsh-taskbar-dot: settings route',
    )
  })

  ctx.effect(
    () => () => {
      try {
        child?.kill()
      } catch { /* already gone */ }
      try {
        spawnHelper(['clear'])
      } catch { /* best effort */ }
    },
    'dsh-taskbar-dot: helper',
  )
}
