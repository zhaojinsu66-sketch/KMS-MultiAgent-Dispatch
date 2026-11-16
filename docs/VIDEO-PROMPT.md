# 宣传视频生成提示词

给视频生成工具（如 LibTV / HyperFrames / Sora 类）用的提示词。目标：16 秒，讲清「一个窗口一个账号，同时开工」。

## 一段话版（直接粘贴）

> 一支 16 秒、16:9、1080p 的极简科技产品短片，介绍 Windows 桌面工具「KMS · 多 Agent 调度」。画面风格干净、克制：米白背景 #F7F7F5、深灰文字 #1A1A1A、克制的橙色 #D97757 作强调色，大量留白，细腻的投影和 0.3 秒的缓入缓出动画，不要花哨特效。内容按顺序：① 两个几乎一样的聊天窗口互相顶掉登录，画面轻微抖动、变灰，配文字「一个应用，只能登一个账号？」；② 左侧滑出一个列表，三条「Claude · 工作 / Claude · 个人 / Codex · 项目」，一条被点击后像被吸入一样滑到右边并铺满整个区域，配文字「每个窗口，一套独立登录」；③ 点击「登录」，一个小浏览器在这块区域内部展开，登录进度条走完，浏览器收起、回到应用，配文字「登录也在窗口里完成」；④ 三个窗口在屏幕上并排，各自有光标在动、有内容在滚动，配文字「同时开工，互不打扰」；⑤ 收尾：纯色底、居中 Logo 和「KMS · 多 Agent 调度」，下方小字「Windows · 开源 · MIT」。镜头之间用 0.2 秒的柔和叠化，不要转场花活；整体像一支苹果风格的功能演示，而不是广告。

## 分镜脚本

| 时间 | 画面 | 屏幕文字 | 备注 |
|---|---|---|---|
| 0:00–0:03 | 两个简化聊天窗口叠在一起，一个弹出「已在别处登录」的灰条，另一窗口变灰 | 一个应用，只能登一个账号？ | 轻微抖动 + 去饱和 |
| 0:03–0:07 | 左侧滑出条目列表（Claude · 工作 / Claude · 个人 / Codex · 项目），点一条，它「吸」进右侧并铺满 | 每个窗口，一套独立登录 | 强调色描边 0.4 秒 |
| 0:07–0:11 | 点击「登录」，区域内展开小浏览器 → 进度条 → 收起 → 回到应用 | 登录也在窗口里完成 | 浏览器只在格子内部，不飘出去 |
| 0:11–0:14 | 三个窗口并排，各自滚动、光标闪动 | 同时开工，互不打扰 | 三分屏，轻微视差 |
| 0:14–0:16 | 纯色收尾：Logo + 名称 + 小字 | KMS · 多 Agent 调度 / Windows · 开源 · MIT | 淡出 |

## 配音 / 字幕文案（可直接当旁白）

> 一个桌面应用，只能登一个账号。
> 想同时用工作号和个人号，就得反复退出、重新登录。
> KMS 给每个窗口一份独立的配置——每个窗口，一套独立登录。
> 登录也在窗口里完成，不跳出去。
> 三个窗口同时开工，互不打扰。
> KMS · 多 Agent 调度。Windows，开源，MIT。

## 参数建议

- 比例 `16:9`，分辨率 `1920×1080`，帧率 `30fps`，时长 `16s`
- 风格关键词：`minimal, off-white #F7F7F5, soft shadows, subtle motion, 0.3s ease-in-out, product demo, apple-style`
- 避免：渐变紫、霓虹、股票K线、机器人形象、夸张弹跳
- 声音：轻钢琴 / 极简电子，音量 −18 dB；关键点击配一条轻微「咔」的音效

## English one-paragraph version

> A 16-second, 16:9, 1080p minimal product film for a Windows utility called "KMS · Multi-Agent Dispatch". Clean off-white (#F7F7F5) background, dark gray text, one restrained orange accent, generous whitespace, soft shadows, 0.3s ease-in-out motion, no flashy effects. Beats: (1) two identical chat windows collide and one goes gray — "One app. One account?"; (2) a sidebar list slides in with three entries, one gets sucked into the right pane and fills it — "One window, one login"; (3) an in-pane browser opens, signs in, collapses back into the app — "Sign in without leaving the window"; (4) three panes run side by side — "Work in parallel, independently"; (5) end card with logo and "KMS · Multi-Agent Dispatch — Windows · Open source · MIT". Soft 0.2s cross-dissolves, Apple-style feature demo, not an ad.
