# PR 合并流程指南（AI 自动化 + 人工审批）

> **定位**：本文档是 QuickTranslate 仓库的 PR 全流程操作手册，主要面向**执行自动化流程的 AI 助手**，并明确哪些节点必须由**人工审批**介入。
> **目标**：AI 负责从分支创建到 PR 就绪的全部机械操作；合并动作与代码审查由人工把关。
> **约束**：本文档只描述 PR 流程，不涉及发布（发布另见 `docs/RELEASE.md`）。

---

## 目录

- [角色与边界](#角色与边界)
- [流程总览](#流程总览)
- [阶段一：准备与分支](#阶段一准备与分支)
- [阶段二：编码与验证](#阶段二编码与验证)
- [阶段三：提交](#阶段三提交)
- [阶段四：推送并创建草稿 PR](#阶段四推送并创建草稿-pr)
- [阶段五：CI、草稿转正与代码审查](#阶段五ci草稿转正与代码审查)
- [阶段六：合并与清理](#阶段六合并与清理)
- [提交规范](#提交规范)
- [PR 标题与正文规范](#pr-标题与正文规范)
- [合并前置条件（仓库流程政策）](#合并前置条件仓库流程政策)
- [安全红线](#安全红线)
- [异常处理](#异常处理)
- [审批节点速查](#审批节点速查)

---

## 角色与边界

| 角色 | 负责内容 |
|:-----|:---------|
| **AI 助手** | 读需求、创建分支、编码、运行 build/test/format、拆分提交、推送、创建草稿 PR、盯 CI、按反馈修改、同步分支。 |
| **人工（维护者）** | 审核变更与验证结果、解决讨论、明确批准转正与合并；可自行执行合并，也可明确授权 AI 代为执行。 |

**AI 禁止执行的动作**（除非人工下达明确指令）：

- 直接 `git push` 到 `main`。
- 直接合并 PR（`gh pr merge`、点 merge 按钮）。
- 对 `main` 或远端仍存在的已合并分支使用 `git push --force`。对未合并的 PR 分支，rebase 后 force push 更新远程是允许的。
- 使用 `--no-verify` 绕过 pre-commit hook。
- 提交 `settings.json`、密钥、数据库、日志、`bin/`、`obj/`、`publish/`。

> 一句话：**AI 可以把 PR 准备到"只差按合并按钮"，但"按合并按钮"这步属于人工。**

---

## 流程总览

```
[1 准备/分支] → [2 编码/验证] → [3 提交] → [4 推送/开草稿 PR]
                                                    │
                                                    ▼
                                      [5 CI 绿 + 自测完成 + 正文完整]
                                                    │
                                                    ▼
                                    [5b 草稿转正]（⛔ 人工确认后执行）
                                                    │
                                                    ▼
                            [5c 人工审查]（检查结果完整 + 明确合并授权）
                                                    │
                                                    ▼
                            [6 合并/清理]（⛔ 人工审批节点）
```

- 每个阶段完成后，AI 应向人工汇报产出与验证结果。
- 标有 **⛔** 的阶段含人工审批节点，AI 不得跳过。
- 所有 PR 默认以**草稿**创建，三项转正条件全部满足并经人工确认后才转正式。

---

## 阶段一：准备与分支

1. 同步基线并切出分支：

```bash
git switch main
git pull --ff-only
git switch -c <类型>/<scope>
```

2. 分支命名规范（与提交 scope 对应）：

| 前缀 | 用途 | 示例 |
|:-----|:-----|:-----|
| `feat/` | 新功能 | `feat/settings` |
| `fix/` | 缺陷修复 | `fix/red_dot` |
| `refactor/` | 重构 | `refactor/app` |
| `docs/` | 文档 | `docs/readme` |
| `chore/` | 发布/构建等杂项 | `chore/release` |

> 如果 Git 写操作后出现 HEAD、ref 或 index 异常，立即停止变更并按
> [异常处理](#异常处理) 收集证据，不要直接修改 `.git` 内部文件。

---

## 阶段二：编码与验证

编码完成后，在提交前必须依次通过三项校验：

```bash
dotnet build  QuickTranslate/QuickTranslate.csproj
dotnet test   QuickTranslate.Tests/QuickTranslate.Tests.csproj
dotnet format QuickTranslate/QuickTranslate.csproj --verify-no-changes
```

- `format --verify-no-changes` 失败时，先 `dotnet format QuickTranslate/QuickTranslate.csproj` 自动修复，再重新验证。
- 若本地有正在运行的 QuickTranslate 实例锁住输出目录，不要终止它，改用仓库内隔离输出路径：

```powershell
dotnet build .\QuickTranslate\QuickTranslate.csproj --no-restore -p:BaseOutputPath=.build-output\
dotnet test  .\QuickTranslate.Tests\QuickTranslate.Tests.csproj --no-restore -p:BaseOutputPath=.build-output\
```

- 验证结束删除 `.build-output/`。

> 自动化测试只覆盖纯逻辑（分类、生命周期、缓存、prompt、日志、指标、解析、清理），**不能证明**真实 Provider 响应、WPF 渲染、DWM 合成、混合 DPI 定位、托盘交互、文件锁、剪贴板行为。涉及这些的改动必须**人工手动验证**，并在 PR 正文中把「自动化验证」与「人工验证」分开说明，未执行的真实场景不得写成已通过。

---

## 阶段三：提交

### 3.1 拆分提交

多个语义独立的改动拆成多个提交，每个提交遵循 [提交规范](#提交规范)。禁止把无关改动混进一个无 scope 的提交。

### 3.2 提交命令

```bash
git add <具体文件...>
git commit -m "feat(settings): 新增翻译触发模式"
```

- 每次 `commit` 前确认没有把 `settings.json`、密钥、数据库、日志、`bin/`、`obj/`、`publish/` 加进去（`git status` 逐项核对）。
- 提交消息为中文描述 + 必带 scope，详见 [提交规范](#提交规范)。

---

## 阶段四：推送并创建草稿 PR

### 4.1 推送

```bash
git push -u origin HEAD
```

- 若本次改动新增/修改了 `.github/workflows/*.yml`，gh 令牌必须具备 `workflow` 作用域，否则 GitHub 拒绝推送。补全：`gh auth refresh -h github.com -s workflow`（设备码流程，需人工在浏览器授权）。
- 在仓库外或非交互环境运行 `gh` 时，若无法推断仓库或主机，应显式传入
  `--repo YAHU2024/myTool` 或 `--hostname github.com`，不要把特定环境报错当作所有环境的固定要求。

### 4.2 创建草稿 PR

**所有 PR 默认以草稿创建**，避免半成品打扰 reviewer。按 [PR 正文规范](#pr-标题与正文规范) 填写标题与正文。由于正文通常包含多行 markdown（含 checkbox、代码块），直接在 `--body` 参数中传值容易转义出错，应使用 `--body-file`：

```powershell
$prBodyPath = Join-Path $env:TEMP "quicktranslate-pr-body.md"
@'
<正文>
'@ | Set-Content -LiteralPath $prBodyPath -Encoding utf8

gh pr create --base main --title "<标题>" --body-file $prBodyPath --draft
```

- 标题遵循 Conventional Commits 风格并带 scope。
- 正文必须包含：变更说明、关联 Issue、改动类型、自测项（build/test/手动验证）、reviewer 注意点；XAML 或窗口布局改动附截图/GIF。
- PR 正文临时文件放在系统临时目录，不要留在仓库根目录或加入提交。
- 草稿 PR 不会触发 reviewer 通知，CI 仍会运行，但草稿状态不可合并，CI 结果仅供参考。

---

## 阶段五：CI、草稿转正与代码审查

1. AI 监控 CI 状态：

```bash
gh pr checks
gh pr view --json statusCheckRollup
```

2. 若 CI 失败，AI 读取失败日志、定位并修复，回到 [阶段二](#阶段二编码与验证)，提交后重新推送。
3. 若 PR 分支落后于 `main`，同步分支。优先 merge 更新（简单安全）；若希望保持线性历史，可 rebase 后 force push（对未合并的 PR 分支是允许的）：

```bash
# 方式一：merge（推荐，无需 force push）
git fetch origin
git merge origin/main
git push

# 方式二：rebase（保持线性历史，需 force push）
git fetch origin
git rebase origin/main
git push --force-with-lease
```

4. **草稿转正**（⛔ 人工确认节点）：以下三项条件全部满足后，AI 向人工汇报转正准备情况，经人工确认后执行：

   | 转正条件 | 说明 |
   |:---------|:-----|
   | **CI 状态明确** | 已触发的 `build-and-test` 检查全部通过。若因 `paths-ignore`（如纯文档改动）未触发，必须明确报告“未运行”，执行适用的文档/差异检查，并由人工判断是否足够。 |
   | **自测完成** | AI 已完成所有自动化验证；手动验证项（如有）已记录在 PR 正文。 |
   | **正文完整** | PR 正文所有板块已填写，截图（如有）已附上。 |

   ```bash
   gh pr ready <PR编号>
   ```

5. 转正后，满足 [合并前置条件](#合并前置条件仓库流程政策) 后，**停下来，交由人工审批**（⛔）：
   - 人工在对话或 GitHub 上明确表示允许合并；有独立 reviewer 时可用 Approving Review 留痕；
   - 人工确认所有 Review 讨论已 Resolved；
   - 人工确认 PR 分支与 `main` 无冲突，且 CI 或未运行原因已经说明。

6. 若审查被拒绝（Request Changes），AI 在原分支继续修改、提交、推送，PR 自动更新。若改动方向完全推翻（如换了实现方案），经人工确认后可关闭旧 PR、另开新分支。

---

## 阶段六：合并与清理

**⛔ 人工审批节点**：以下动作仅由人工执行，或由人工下达明确指令后 AI 才可执行。

### 6.1 合并

默认使用 **squash merge**（合并提交汇总为一个干净的 squash commit）：

```powershell
$prHeadSha = gh pr view <PR编号> --json headRefOid --jq .headRefOid
gh pr merge <PR编号> --squash --delete-branch
```

### 6.2 清理本地

```powershell
$prHeadSha = gh pr view <PR编号> --json headRefOid --jq .headRefOid
git switch main
git pull --ff-only
git fetch --prune

$mergeSha = gh pr view <PR编号> --json mergeCommit --jq .mergeCommit.oid
git diff --exit-code $prHeadSha $mergeSha --
if ($LASTEXITCODE -ne 0) { throw "Squash commit differs from the approved PR head." }

# `gh pr merge --delete-branch` 通常已完成清理；若本地分支仍存在，
# squash 后原提交不是 main 的祖先，-d 会拒绝。完成上述核验后才允许删除准确分支。
if (git branch --list <已合并分支>) { git branch -D <已合并分支> }
```

> release PR 同样默认使用 Draft PR 和 squash merge，不享有跳过审查或自动合并的例外。
> 版本号、产物、tag 和 GitHub Release 的额外确认门及文件树校验按
> [`docs/RELEASE.md`](RELEASE.md) 执行。

---

## 提交规范

采用 Conventional Commits 风格，格式：`类型(scope): 中文描述`。

**硬性要求**：

1. 描述使用**中文**。
2. **必须带 scope**。
3. 多个语义独立的改动拆成多个带独立 scope 的提交。

> 本指南的提交规范比公开的 `CONTRIBUTING.md` 更严格（"必须带 scope" 而非 "建议"），这是 AI 自动化流程的硬性要求。

| 类型 | 用途 | 示例 |
|:-----|:-----|:-----|
| `feat` | 新功能 | `feat(settings): 新增翻译触发模式` |
| `fix` | 缺陷修复 | `fix(red_dot): 修复红点消失时机` |
| `docs` | 文档 | `docs(readme): 更新下载入口` |
| `refactor` | 重构 | `refactor(App): 重构更新调度` |
| `chore` | 构建/发布/杂项 | `chore(release): 版本号升级到 1.8.0` |
| `ci` | CI/工作流 | `ci(github): 新增 GitHub Actions 构建测试工作流` |

历史提交如需重拆，可用 `git reset HEAD~`（改动仍保留在工作区——未暂存，不会丢）后重新暂存、按规范重提。

---

## PR 标题与正文规范

### 标题

`类型(scope): 中文描述`，与提交规范一致，如 `feat(settings): 新增翻译触发模式`。

### 正文

必须说明：**用户可见行为**、**实现影响**、**手动验证情况**，并覆盖以下板块（对齐 `.github/PULL_REQUEST_TEMPLATE.md`）：

```markdown
## 变更说明
<!-- 这个 PR 做了什么、为什么 -->

## 关联 Issue
<!-- 例如：Closes #123 -->

## 改动类型
- [ ] 新功能 (feat)
- [ ] 缺陷修复 (fix)
- [ ] 重构 (refactor)
- [ ] 文档 (docs)
- [ ] 其他

## 自测项
- [ ] dotnet build 通过
- [ ] dotnet test 通过
- [ ] 手动验证了相关 Windows 流程（如适用）

## 备注
<!-- reviewer 注意点，或截图/GIF -->
```

- XAML 或窗口布局改动必须附截图。
- 「自动化验证」与「人工验证」分开陈述；未执行的真实 Provider、安装升级、混合 DPI、辅助功能验证**不得**写成已通过。

---

## 合并前置条件（仓库流程政策）

截至 2026-08-17，GitHub 上的 `main` 没有 Branch Protection 或 Ruleset；下列要求是
仓库流程政策，而不是平台自动拦截。AI 必须主动遵守，不能因为 GitHub 允许点击合并就
视为已经获得授权。以后若启用或调整分支保护，应重新核对并同步本文档。

| 检查项 | 说明 |
|:-------|:-----|
| **验证状态明确** | 已触发的检查全部通过；未触发或未执行的检查必须如实说明原因、替代检查和剩余风险。 |
| **人工确认** | 维护者明确批准合并。独立 Approving Review 是推荐留痕方式，但单人维护时不作为虚假的硬性前提。 |
| **分支保持最新** | PR 分支与 `main` 同步（无冲突）。 |
| **对话已解决** | 所有 Review 讨论已标记 Resolved。 |
| **发布附加门禁** | release PR 还必须满足 `docs/RELEASE.md` 中的合并前确认、squash 文件树校验和 Draft Release 发布确认。 |

> 在 GitHub 尚未配置技术强制前，这些规则完全依赖执行者遵守。启用 Branch Protection
> 或 Ruleset 应作为独立的仓库管理任务评估，不能只修改文档后宣称已经受保护。

---

## 安全红线

以下行为在 PR 流程中**一律禁止**：

- 提交 `settings.json`、真实 API Key、服务凭据、数据库文件、日志文件、`bin/`、`obj/`、`publish/`。
- 在提交消息、PR 正文、Issue、Review 讨论中粘贴真实选中原文、翻译/解析输出、系统/自定义 Prompt、Authorization 头、完整 Provider 响应体、异常消息。
- 对 `main` 或远端仍存在的已合并分支 `push --force`。对未合并的 PR 分支，rebase 后 `push --force-with-lease` 更新远程是允许的。
- 使用 `--no-verify` 绕过 pre-commit hook。
- 在未获人工明确指令时执行合并或删除远程分支；未经发布任务授权创建 Release；
  未经发布人的第二次明确确认将 Draft Release 转为正式或设置 Latest。

---

## 异常处理

### 1. 推送 workflow 文件被拒（`workflow scope`）

- 现象：推送含 `.github/workflows/*.yml` 的提交时报 `refusing to allow an OAuth App to create or update workflow ... without workflow scope`。
- 处理：`gh auth refresh -h github.com -s workflow`（设备码流程，需人工在浏览器完成一次授权），完成后重推。

### 2. Git ref 或 index 状态异常

- 现象：Git 写操作异常返回、HEAD 指向不存在的 ref，或 `git status` 突然把大量文件显示为新增/暂存。
- 处理：立即停止提交、reset、rebase、清理和推送。先用 `git status`、`git branch -vv`、
  `git reflog` 和 `git cat-file -t <提交>` 收集只读证据，再向维护者报告。
- 禁止在未确认原因、目标提交和恢复路径前手写 `.git/refs`、运行 `git read-tree`，
  或用 `git reset --hard`、`git checkout --` 尝试恢复。

### 3. 陈旧 `.git/index.lock`

- 现象：git 操作报 `index.lock` 已存在。
- 处理：先检查是否仍有 Git 进程，并核对锁文件的时间和大小。只有确认锁已失效且维护者
  明确同意后，才使用 PowerShell 对准确路径执行 `Remove-Item -LiteralPath <index.lock> -Force`。

### 4. 非交互运行 `gh` 报 hostname 缺失

- 现象：`--hostname required when not running interactively`。
- 处理：确认当前目录属于目标仓库；必要时显式传入 `--repo YAHU2024/myTool` 或
  `--hostname github.com`，不要盲目重试或改变认证状态。

---

## 审批节点速查

| 阶段 | 动作 | 是否需人工 |
|:-----|:-----|:---------|
| 阶段一 | 创建分支 | 否（AI） |
| 阶段二 | 编码 + build/test/format | 否（AI） |
| 阶段三 | 提交（拆分、规范） | 否（AI） |
| 阶段四 | 推送 + 创建草稿 PR | 否（AI） |
| 阶段五 | 盯 CI、改反馈、同步分支 | 否（AI） |
| 阶段五 ⛔ | 草稿转正（CI 绿 + 自测完成 + 正文完整，人工确认后执行） | **是** |
| 阶段五 | 审查拒绝后在原分支继续修改 | 否（AI） |
| 阶段五 | 关闭旧 PR 另开新分支（需人工确认） | **是** |
| 阶段五 ⛔ | 审查、Resolve 讨论并明确授权合并 | **是** |
| 阶段六 ⛔ | squash 合并 / 删远程分支 | **是** |
| 阶段六 | 清理本地分支、同步 main | 否（AI，合并完成后） |

> 本文档按需演进；若分支保护规则或 CI 检查项发生变化，请同步更新本表与「合并前置条件」一节。
