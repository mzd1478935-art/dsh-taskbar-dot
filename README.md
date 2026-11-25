# dsh-taskbar-dot

![preview](docs/preview.png)

DSH 回合跑完时，在 Windows 任务栏的 DSH 图标右上角点一个小蓝点；你切回窗口，它自己消失。

**English** — a tiny DeepSeek Harness plugin that puts a small blue dot on the Windows taskbar button when a turn finishes while the app is in the background. No resident process, no polling, no token cost: the overlay is drawn by explorer through `ITaskbarList3::SetOverlayIcon`, and a 9.5 KB helper clears it the moment you come back.

- **零 token**：不注册工具、不往 system prompt 加任何字。
- **零空闲占用**：没有常驻进程、没有轮询。点在的时候才有一个 9.5 KB 的 helper（约 21 MB 工作集），清点后立刻退出。
- **系统绘制**：用 `ITaskbarList3::SetOverlayIcon`，图标由 explorer 画，不占我们的内存，也没有多余的置顶窗口。
- **不打扰**：你正看着 DSH 窗口时回合结束，不点亮（结果就在眼前）。

## 行为

| 场景 | 结果 |
| --- | --- |
| 回合完成，DSH 在后台 | 点亮蓝点，直到你切回窗口 |
| 回合完成，你正在 DSH 里 | 不点亮 |
| 你切回窗口 | 点立刻清除，helper 退出 |
| 一直不回来看 | 最多保留 30 分钟 |
| 非 Windows / 浏览器版 | 加载后什么都不做 |

## 安装

```powershell
dsh plugin --profile desktop add dsh-taskbar-dot
```

装完在插件管理里启用即可（宿主半边热加载，不需要重启）。

手工安装：把包放进 profile 的 `node_modules`，在 `package.json` 的 `dsh.profile.bundles` 里加上 `dsh-taskbar-dot`。

## 配置

在 profile 的 `cordis.patch.yml` 里覆盖：

```yaml
- id: taskbar-dot
  name: ./index.js
  config:
    enabled: true
    windowTitle: DeepSeek Harness   # 用来找窗口的标题片段
```

## 卸载

禁用/卸载插件时宿主会顺手清掉点并结束 helper；再删 `%LOCALAPPDATA%\DSH\taskbar-dot` 就干净了。

## 已知边界

- Windows 10/11 专用；overlay 由 explorer 画，explorer 重启后要等下一个回合才会重新出现。
- 同时开多个 DSH 窗口时，只贴标题匹配、面积最大的那个。
- helper 是未签名的 .NET Framework exe（9.5 KB）。不放心就自己编：`scripts/build.ps1`，用 Windows 自带的 `csc.exe`。
- 日志在 `%LOCALAPPDATA%\DSH\taskbar-dot\taskbar-dot.log`，超过 256 KB 自动清掉。

MIT。
