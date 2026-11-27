# 界面证据

- `demo/`：通过 `--demo` 生成的界面预览。硬件、Codex、Gemini、Claude、DeepSeek 数值均为演示数据。
- `real/`：从本机真实运行捕获。硬件和 Codex 额度为真实数据；DeepSeek 未配置 Key。

当前 1.7.1 为 **360 × 360** 的 1:1 方形挂件：顶部标签与窗口按钮无外框；
左上角与任务栏共用霓虹 77 Logo；双主题（性能优先 / 额度优先）切换；
额度区采用 2×2 四宫格（上排 Codex / Gemini 双窗口与独立重置时间，下排 Claude / DeepSeek 统一基线余额）；
检测台支持核显/独显两路 GPU 读数与 1 / 2 / 5 / 10 秒硬件刷新；顶栏图钉左侧为短透明度滑条。

截图：

- `demo/01-monitor.png` 检测台（性能优先），540×540（150% DPI）
- `demo/02-settings.png` 设置页，540×540
- `demo/03-monitor-2x.png` 检测台（性能优先）2×，720×720
- `demo/04-monitor-1x.png` 检测台（性能优先）1×，360×360
- `demo/05-settings-1x.png` 设置页 1×，360×360
- `demo/06-monitor-quota-2x.png` 额度优先主题 2×，720×720
- `demo/07-monitor-quota-1x.png` 额度优先主题 1×，360×360
