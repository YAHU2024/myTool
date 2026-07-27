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

## 隐私约定

QuickTranslate 的应用日志是**隐私敏感**的：日志不记录选中原文、翻译/解析输出、API Key 或 Prompt 正文。改动日志相关代码时请保持这一边界，也不要在 Issue / PR 中粘贴真实原文或密钥。

## 发布流程

版本发布由维护者按 `docs/RELEASE.md` 的流程执行，社区贡献一般只需聚焦代码与文档即可。
