# 贡献指南 (CONTRIBUTING)

感谢你考虑为 **QuickTranslate** 做贡献！这个项目欢迎 Issue、建议和 PR。

## 如何参与

- **报 Bug / 提建议**：请使用仓库的 Issue 模板（Bug 报告 / 功能建议）。
- **使用问答**：安装、配置、使用问题请到 [Discussions](https://github.com/YAHU2024/myTool/discussions) 交流。
- **提交代码**：Fork → 新建分支 → 提交 PR。

## 开发环境

- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 用 Visual Studio 2022+ 或 `dotnet` CLI 打开 `QuickTranslate/QuickTranslate.csproj`

```powershell
dotnet restore QuickTranslate/QuickTranslate.csproj
dotnet build   QuickTranslate/QuickTranslate.csproj
dotnet test    QuickTranslate.Tests/QuickTranslate.Tests.csproj
```

## 提交规范

提交信息采用 Conventional Commits 风格，建议带 scope：

```
feat(settings): 新增翻译触发模式
fix(red_dot): 修复红点消失时机
docs(readme): 更新下载入口
refactor(App): 重构更新调度
```

## 代码风格

- C# 使用 4 空格缩进，`PascalCase` 类型/方法/属性，`camelCase` 局部变量/参数，`_camelCase` 私有字段。
- 提交前可运行 `dotnet format QuickTranslate/QuickTranslate.csproj`。
- 不要提交：`settings.json`、密钥、数据库、日志、`bin/`、`obj/`、`publish/`。

## 项目结构维护

README 的「项目结构 / Project Structure」区块由脚本自动生成，请勿手工编辑：

- 新增或删除源文件后，运行 `python scripts/update-readme-tree.py --write` 同步中英 README 的结构块。
- 新增文件的注释在 `scripts/update-readme-tree.py` 顶部的映射表中补充（中文/英文各一份）；脚本会提示「未映射的新条目」。
- 仓库 CI（Verify README Tree）会在 PR 中校验结构一致性，`python scripts/update-readme-tree.py --check` 失败即需重新生成。

## 分支保护规则

`main` 分支受 GitHub Branch Protection 保护，合并 PR 前必须满足以下条件：

| 检查项 | 说明 |
|--------|------|
| **Build & Test 通过** | 所有 `build-and-test` 检查必须绿色。 |
| **PR 审查** | 至少一次 Approving Review。 |
| **分支保持最新** | PR 分支必须与 `main` 保持同步（无冲突）。 |
| **对话已解决** | 所有 Review 讨论已标记 Resolved。 |

CI 会在 PR 页面自动展示测试结果与代码覆盖率摘要。合并后忽略这些规则的风险自负。

## 隐私约定

QuickTranslate 的应用日志是**隐私敏感**的：日志不记录选中原文、翻译/解析输出、API Key 或 Prompt 正文。改动日志相关代码时请保持这一边界，也不要在 Issue / PR 中粘贴真实原文或密钥。

## 许可证与贡献者权利

项目当前的新版本采用 [Mozilla Public License 2.0 (MPL-2.0)](LICENSE)。贡献者保留其原创贡献的版权；合并到项目的代码将按 MPL-2.0 授权。提交贡献前，请确认你拥有该贡献的必要权利，并避免提交第三方未获授权的代码或内容。MPL-2.0 的专利授权、免责声明和文件级源码共享义务以 [LICENSE](LICENSE) 正文为准。

## 发布流程

版本发布由维护者按 `docs/RELEASE.md` 的流程执行，社区贡献一般只需聚焦代码与文档即可。
