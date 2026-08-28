# QuickTranslate 发布说明（v1.9.3）

> 基线版本：v1.9.2 → 目标版本：v1.9.3
> 发布模式：咨询模式（安装包未签名，仅 SHA256 校验）
> 最近同步：2026-08-28

## 新增特性

- **应用图标全面替换**：彩色主图标升级为多尺寸 ico，覆盖 exe、窗口、托盘默认与安装包图标；新增白色副图标，托盘「暂停翻译」时自动切换显示，恢复后切回彩色。

## 优化改进

- **更新说明内嵌展示**：更新窗口的 changelog 区域改用 GitHub releases.atom 摘要配合 WebView2 渲染，并禁用内嵌页脚本执行；修复此前长期卡在「正在加载更新说明」、系统浏览器打开与「立即更新」按钮引导逻辑混乱的问题。

## 修复

- 设置窗口删除「当前生效的已保存配置」后，模型名称下拉框残留已删除名称、且保存时该配置被重新写回（复活）。
- 崩溃提示窗口按钮统一 UI 样式（主操作主色、次要操作描边，与反馈/设置窗口一致）。

## 仓库与站点

- GitHub Pages 项目站点上线：静态首页、导航 ScrollSpy 滚动高亮、Logo 与 favicon，新增部署工作流。
- README 项目结构改由脚本自动维护（`scripts/update-readme-tree.py`），新增 CI 校验（Verify README Tree）防止结构块漂移；测试目录标注为代表性文件精选子集。
- 站点素材单一来源（`docs/images/` + `scripts/sync-site-assets.py` CI 同步），素材统一英文文件名并移出 Git 跟踪。

## 依赖

- 无新增或升级依赖。安全约束保留：强制 `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12 以修复 SQLite GHSA-2m69-gcr7-jv3q（CVE）。

## 验证情况

- 自动化测试：`dotnet build` 0 error；`dotnet test` 775 通过 / 3 跳过 / 0 失败；`dotnet format --verify-no-changes` 通过（自动修复 MarkdownInteraction.cs 既有的缩进违规）；`git diff --check` 通过；两个安装包编译后完整性校验 PASS（ProductVersion=1.9.3、CompanyName=YaHu）。
- **未执行**（需人工验收）：真实 Provider 响应、WPF 渲染、混合 DPI 放置、托盘集成、安装 / 卸载、自动更新升级、辅助功能。
- **签名**：本期为咨询模式，安装包未签名；`version.xml` 的 `<checksum>` 已在构建完整版安装包后回填真实 SHA256。
