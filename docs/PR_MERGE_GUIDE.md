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
- [合并前置条件（分支保护）](#合并前置条件分支保护)
- [安全红线](#安全红线)
- [异常处理](#异常处理)
- [审批节点速查](#审批节点速查)

---

## 角色与边界

| 角色 | 负责内容 |
|:-----|:---------|
| **AI 助手** | 读需求、创建分支、编码、运行 build/test/format、拆分提交、推送、创建草稿 PR、盯 CI、按反馈修改、同步分支。 |
| **人工（维护者）** | 代码审查（Approving Review）、解决讨论、最终合并、删除远程分支。 |

**AI 禁止执行的动作**（除非人工下达明确指令）：

- 直接 `git push` 到 `main`。
- 直接合并 PR（`gh pr merge`、点 merge 按钮）。
- 对 `main` 或远端仍存在的已合并分支使用 `git push --force`。对未合并的 PR 分支，rebase 后 force push 更新远程是允许的。
- 绕过 pre-commit hook（`--no-verify`）或关闭签名校验（`--no-gpg-sign`）。
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
                            [5c 代码审查]（CI 绿 + 至少 1 个 Approve）
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

> 分支名用 `/` 分隔多级时（如 `fix/red_dot`），注意本项目曾出现 git 写命令误删 `.git/refs/heads/<前缀>/` 目录的环境异常，处理办法见 [异常处理](#异常处理)。

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
- 非交互/后台运行 `gh` 时必须带 `-h github.com`，否则报 `--hostname required when not running interactively`。

### 4.2 创建草稿 PR

**所有 PR 默认以草稿创建**，避免半成品打扰 reviewer。按 [PR 正文规范](#pr-标题与正文规范) 填写标题与正文。由于正文通常包含多行 markdown（含 checkbox、代码块），直接在 `--body` 参数中传值容易转义出错，应使用 `--body-file`：

```bash
cat > /tmp/pr_body.md << 'EOF'
<正文>
EOF
gh pr create --base main --title "<标题>" --body-file /tmp/pr_body.md --draft
```

- 标题遵循 Conventional Commits 风格并带 scope。
- 正文必须包含：变更说明、关联 Issue、改动类型、自测项（build/test/手动验证）、reviewer 注意点；XAML 或窗口布局改动附截图/GIF。
- `cat > file` 写入的文件在沙箱外进程可见；Write 工具写入的文件在沙箱文件系统，`gh --body-file` 读取不到。
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
   | **CI 全绿** | `build-and-test` 检查全部通过。若 CI 因 `paths-ignore`（如纯文档改动）未触发，视为"CI 条件自动满足"，但 reviewer 仍需确认改动不影响编译。 |
   | **自测完成** | AI 已完成所有自动化验证；手动验证项（如有）已记录在 PR 正文。 |
   | **正文完整** | PR 正文所有板块已填写，截图（如有）已附上。 |

   ```bash
   gh pr ready <PR编号>
   ```

5. 转正后，满足 [合并前置条件](#合并前置条件分支保护) 后，**停下来，交由人工审批**（⛔）：
   - 人工给出至少一次 Approving Review；
   - 人工确认所有 Review 讨论已 Resolved；
   - 人工确认 PR 分支与 `main` 无冲突、CI 全绿。

6. 若审查被拒绝（Request Changes），AI 在原分支继续修改、提交、推送，PR 自动更新。若改动方向完全推翻（如换了实现方案），经人工确认后可关闭旧 PR、另开新分支。

---

## 阶段六：合并与清理

**⛔ 人工审批节点**：以下动作仅由人工执行，或由人工下达明确指令后 AI 才可执行。

### 6.1 合并

默认使用 **squash merge**（合并提交汇总为一个干净的 squash commit）：

```bash
gh pr merge <PR编号> --squash --delete-branch
```

### 6.2 清理本地

```bash
git switch main
git pull --ff-only
git fetch --prune
git branch -d <已合并分支>
```

> 版本发布（打 tag、建 GitHub Release、更新 `installer/version.xml`）不在本文档范围，按 `docs/RELEASE.md` 由维护者执行。发布 PR 为简化流程：直接开正式 PR，CI 绿即合并，无需草稿和审查。

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

## 合并前置条件（分支保护）

`main` 分支受 GitHub Branch Protection 保护，合并前必须**同时满足**：

| 检查项 | 说明 |
|:-------|:-----|
| **Build & Test 通过** | 所有 `build-and-test` 检查绿色。若 CI 因 `paths-ignore`（如纯 `*.md`、`docs/**`、`LICENSE` 改动）未触发，视为"CI 条件自动满足"，但 reviewer 仍需确认改动不影响编译。 |
| **PR 审查** | 至少一次 Approving Review。 |
| **分支保持最新** | PR 分支与 `main` 同步（无冲突）。 |
| **对话已解决** | 所有 Review 讨论已标记 Resolved。 |

> 合并后忽略这些规则的风险自负。CI 会在 PR 页面自动展示测试结果与代码覆盖率摘要。

---

## 安全红线

以下行为在 PR 流程中**一律禁止**：

- 提交 `settings.json`、真实 API Key、服务凭据、数据库文件、日志文件、`bin/`、`obj/`、`publish/`。
- 在提交消息、PR 正文、Issue、Review 讨论中粘贴真实选中原文、翻译/解析输出、系统/自定义 Prompt、Authorization 头、完整 Provider 响应体、异常消息。
- 对 `main` 或远端仍存在的已合并分支 `push --force`。对未合并的 PR 分支，rebase 后 `push --force-with-lease` 更新远程是允许的。
- 绕过 pre-commit hook 或关闭签名校验。
- 在未获人工明确指令时执行合并、删除远程分支、打 tag 或创建 Release。

---

## 异常处理

### 1. 推送 workflow 文件被拒（`workflow scope`）

- 现象：推送含 `.github/workflows/*.yml` 的提交时报 `refusing to allow an OAuth App to create or update workflow ... without workflow scope`。
- 处理：`gh auth refresh -h github.com -s workflow`（设备码流程，需人工在浏览器完成一次授权），完成后重推。

### 2. git 写命令误删 `.git/refs/heads/<前缀>/` 目录

- 现象：`git commit` / `git update-ref` / `git reset` 返回 0（成功），但 `.git/refs/heads/<分支>/` 目录被删，HEAD 指向不存在的 ref，`git status` 把所有文件显示为 `new file` staged。
- 安全：提交对象和 reflog 完好，数据不丢。
- 恢复（绕过 git 写 ref）：

```bash
# 1. 确认提交对象仍在
git reflog
git cat-file -t YOUR_COMMIT_HASH

# 2. 手动写回 ref 文件（将 YOUR_COMMIT_HASH / YOUR_PREFIX / YOUR_BRANCH 替换为实际值）
mkdir -p .git/refs/heads/YOUR_PREFIX
printf 'YOUR_COMMIT_HASH\n' > .git/refs/heads/YOUR_PREFIX/YOUR_BRANCH

# 3. 用 read-tree 重置脏 index（不要用 git reset，会再删 ref）
git read-tree HEAD

# 4. 推送正常，不删本地 ref
git push
```

> 这些 git 写命令需在**关闭沙箱**的环境中执行；通过 Write 工具写入的文件在沙箱文件系统，沙箱外进程（如 `gh --body-file`）看不到，需用 `bash` 的 `cat > file` 写。

### 3. 陈旧 `.git/index.lock`

- 现象：git 操作报 `index.lock` 已存在。
- 处理：确认无 git 进程运行后 `rm -f .git/index.lock`。

### 4. 非交互运行 `gh` 报 hostname 缺失

- 现象：`--hostname required when not running interactively`。
- 处理：所有 `gh` 命令显式加 `-h github.com`。

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
| 阶段五 ⛔ | Approving Review / Resolve 讨论 | **是** |
| 阶段六 ⛔ | squash 合并 / 删远程分支 | **是** |
| 阶段六 | 清理本地分支、同步 main | 否（AI，合并完成后） |

> 本文档按需演进；若分支保护规则或 CI 检查项发生变化，请同步更新本表与「合并前置条件」一节。
