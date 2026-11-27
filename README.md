# dsh-taskbar-dot

![preview](docs/preview.png)

DSH 回合跑完时，在 Windows 任务栏的 DSH 图标右上角点一个小蓝点，并播一声短促提示音；你切回窗口，点自己消失。

**English** — a tiny DeepSeek Harness plugin: a small blue dot on the Windows taskbar button when a turn finishes while the app is in the background, plus one short synthesized tone. No resident process, no polling, no token cost — explorer draws the overlay through `ITaskbarList3::SetOverlayIcon`, and a 12.8 KB helper clears it when you come back.

- **零 token**：不注册工具、不往 system prompt 加任何字。
- **零空闲占用**：没有常驻进程、没有轮询。每个回合只拉起一次 helper（约 20 MB，点亮期间），清点后退出。
- **系统绘制**：图标由 explorer 画，不占我们的内存，也没有多余的置顶窗口。
- **声音现场合成**：提示音在 helper 里生成 WAV 播放，不带音频文件、不多开进程。

## 行为

| 场景 | 结果 |
| --- | --- |
| 回合完成，DSH 在后台 | 响一声 + 点亮蓝点，直到你切回窗口 |
| 回合完成，你正在 DSH 里 | 只响一声，不点亮（结果就在眼前） |
| 你切回窗口 | 点立刻清除，helper 退出 |
| 一直不回来看 | 最多保留 30 分钟 |
| 非 Windows / 浏览器版 | 加载后什么都不做 |

## 设置

DSH → **插件 → dsh-taskbar-dot**，详情页里的配置卡：

- **启用**：关掉之后回合结束既不点亮也不响；
- **点的大小**：很小 6px / 小 7px / **中 8px（默认）** / 大 9px / 很大 10px；
- **提示音**：关闭 / 嗒（短促）/ 哔（单声）/ 叮咚（两音，默认）/ 啵（闷响）/ 嗯（柔和），带试听。

改动即时写进 `%LOCALAPPDATA%\DSH\taskbar-dot\settings.json`。默认值也可以在 profile 的 `cordis.patch.yml` 里覆盖：

```yaml
- id: taskbar-dot
  name: ./index.js
  config:
    enabled: true
    size: 3                          # 1..5
    sound: chime                     # off|tick|blip|chime|pop|soft
    windowTitle: DeepSeek Harness    # 用来找窗口的标题片段
```

## 安装

```powershell
dsh plugin --profile desktop add dsh-taskbar-dot
```

装完在插件管理里启用。PNPM 装好后**重启一次 DSH**，配置卡才会出现在插件详情页（客户端半边在启动时装配）。

手工安装：把包放进 profile 的 `node_modules`，在 `package.json` 的 `dsh.profile.bundles` 里加上 `dsh-taskbar-dot`。

## 卸载

禁用/卸载插件时宿主会顺手清掉点并结束 helper；再删 `%LOCALAPPDATA%\DSH\taskbar-dot` 就干净了。

## 已知边界

- Windows 10/11 专用；overlay 由 explorer 画，explorer 重启后要等下一个回合才会重新出现。
- 同时开多个 DSH 窗口时，只贴标题匹配、面积最大的那个。
- helper 是未签名的 .NET Framework exe（12.8 KB）。不放心就自己编：`scripts/build.ps1`，用 Windows 自带的 `csc.exe`。
- 日志在 `%LOCALAPPDATA%\DSH\taskbar-dot\taskbar-dot.log`，超过 256 KB 自动清掉。

## 更新记录

- **0.2.1**：修正子代理/后台运行误点亮；点已亮时后续回合仍会响一声；30 分钟超时会撤点；关掉总开关立刻清点；瞬时 running 抖动不再计数；配置里 `enabled:false` 不再吞掉设置接口。
- **0.2.0**：五档点大小；五种短促提示音（可关闭、可试听）；配置卡移到插件详情页；声音现场合成；helper 12.8 KB。
- **0.1.0**：首版，回合完成时在任务栏图标右上角点一个固定 8px 蓝点。

MIT。
