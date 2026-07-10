# ThreadShelf

[English](README.en.md) · 简体中文

ThreadShelf 是一款面向 Codex 用户的本地 Windows 任务整理器。它把已经存在的 Codex 会话按项目、文件夹和标签组织起来，并提供搜索、收藏、备注、归档和重命名入口。它适合任务数量逐渐增多、又不希望把私人会话上传到其他服务的个人用户与开发者。

ThreadShelf 不会接管或改写 Codex 的会话文件。文件夹、标签、备注、收藏和项目显示别名保存在独立的 sidecar 中；归档、取消归档和任务标题重命名只会通过 Codex CLI 的公开 `app-server` 协议执行。

![ThreadShelf 主界面，使用完全虚构的演示数据](docs/assets/threadshelf-main.png)

## 主要功能

- 按真实工作区聚合 Codex 项目，并在项目内使用 ThreadShelf 文件夹整理任务。
- 将任务拖到文件夹；拖到“未分类”可立即清除文件夹。
- 搜索标题、项目、文件夹、标签、备注、ID、模型和归档状态。
- 管理带颜色和说明的全局标签；标签重命名时自动迁移任务引用。
- 编辑备注、收藏和 Codex 任务标题；从卡片快捷归档或取消归档。
- 为项目设置仅在 ThreadShelf 中显示的别名，或原子重命名当前项目内的文件夹。
- 点击右侧工作区链接可在文件资源管理器中打开真实目录。
- 从项目启动新的交互式 Codex 任务，或从任务卡片继续已有会话；桌面版优先，CLI 自动回退。
- 简体中文与 English 界面：默认跟随系统，也可手动选择并在重启后保留。
- 提供 CLI 和 MCP 自动化入口，供脚本与 AI 助手安全地整理任务。

## 环境要求

- Windows 10 1809（build 17763）或更高版本。
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（从源码构建时需要）。
- 可选：Codex 桌面版或已安装并可执行的 Codex CLI。交互式“继续/新建任务”至少需要其中一种；归档、取消归档和标题重命名仍需要 CLI 的 `app-server`。

应用使用自包含的 Windows App SDK 构建，因此不需要单独安装 Windows App Runtime。

## 安装和启动

```powershell
git clone https://github.com/ReneeGA2020/ThreadShelf.git
cd ThreadShelf
dotnet build ThreadShelf.App\ThreadShelf.App.csproj -p:Platform=x64
dotnet run --project ThreadShelf.App\ThreadShelf.App.csproj -p:Platform=x64
```

也可以在构建后直接运行：

```powershell
.\ThreadShelf.App\bin\x64\Debug\net10.0-windows10.0.22621.0\ThreadShelf.App.exe
```

ARM64 设备可将 `x64` 替换为 `ARM64`。

### 发布 win-x64 NativeAOT

仓库提供可直接使用的 Release NativeAOT profile：

```powershell
dotnet publish ThreadShelf.App\ThreadShelf.App.csproj -p:PublishProfile=WinX64NativeAot
```

完整产物位于 `ThreadShelf.App\bin\publish\win-x64\`。这是自包含、unpackaged 的 x64 目录发布；分发时需要压缩整个目录，不能只复制 `ThreadShelf.App.exe`。profile 保留中英文 globalization，并通过 `_CopyWinUIResourcesForPublish` 将 unpackaged WinUI 所需的 `ThreadShelf.App.pri`（以及项目生成 `.xbf` 时的 `.xbf`）复制到发布目录。

当前只提供 `win-x64` profile；ARM64 可从源码构建，但尚未提供 Release publish profile。Release/AOT 不包含 Reactor devtools。上游 Reactor 包当前可能输出汇总 `IL2104`，ThreadShelf 自身不产生 IL2026/IL3050，且发布产物已通过下述 UI smoke test。

## 第一次使用

1. 启动 ThreadShelf。它会优先尝试 `codex app-server`，失败时自动使用本地 JSONL 只读导入。
2. 在左上角选择“跟随系统”“English”或“简体中文”。手动选择会保存到独立偏好文件。
3. 在左侧选择项目和文件夹，在中间选择任务，在右侧编辑文件夹、标签、备注和收藏。
4. 使用卡片右上角的状态按钮归档/取消归档；如果 app-server 不可用，该按钮会禁用并解释原因。
5. 右键普通项目或文件夹可重命名。项目重命名是 ThreadShelf 显示别名，不会修改 Codex 项目或磁盘目录。

## 日常工作流

### 整理任务

- 点击任务标题选择任务；拖动专用 `::` 手柄到目标文件夹。
- 在右侧属性面板设置收藏、文件夹、标签和备注。标签、收藏和拖拽会立即保存；文件夹和备注在 Enter 或失焦时保存。
- 使用搜索框组合项目、文件夹和标签筛选；“未归档/已归档”视图会在状态切换后立即更新。

### 打开、新建和继续 Codex 任务

- 右侧“工作区”显示为链接；点击后会在 Windows 文件资源管理器中打开真实目录。路径缺失或不存在时链接会禁用并解释原因。
- 卡片和右侧属性面板都只显示一个“继续”入口。检测到 Codex 桌面版时，使用 `codex://threads/<id>`；否则在可见终端中以结构化参数执行 `codex resume -C <workspace> <session-id>`。
- 右键普通项目并选择“新建任务”使用同一策略：桌面版通过 `codex://threads/new?path=<workspace>` 打开，只有 CLI 时才执行 `codex -C <workspace>`。
- 项目显示别名从不作为文件系统路径使用。
- 桌面版和 CLI 均不可用，或 workspace/session ID 无效时，入口会禁用并显示本地化原因。CLI 回退有 Windows Terminal 时优先使用，否则直接启动可见的 Codex 控制台。
- 悬停 `+` 仅作为可选实验评估过；真实指针测试中保留固定行命中区与右键菜单的体验更稳定，因此当前不显示悬停按钮。

### 标签管理

打开“标签管理器”可创建、编辑、重命名或删除全局标签。删除标签会同时移除任务引用；执行前请确认目标标签。

## 数据、隐私和备份

默认情况下，路径位于 `%USERPROFILE%\.codex`；设置 `CODEX_HOME` 后使用该目录。

| 数据 | 路径 | ThreadShelf 行为 |
| --- | --- | --- |
| Codex 会话索引 | `session_index.jsonl` | fallback 时只读 |
| Codex 会话 | `sessions/`、`archived_sessions/` | fallback 时只读，绝不写入 |
| ThreadShelf 元数据 | `threadshelf/threadshelf.json` | 保存文件夹、标签、备注、收藏和项目别名 |
| ThreadShelf 偏好 | `threadshelf/preferences.json` | 保存手动语言选择 |

备份时复制 `threadshelf` 目录即可保留 ThreadShelf 自有数据。sidecar 可能包含私人备注和任务 ID，请像对待 Codex 历史一样保护它。ThreadShelf 不会把这些数据发送到网络。

## Codex provider 与 fallback

| 能力 | app-server 可用 | 本地 JSONL fallback |
| --- | --- | --- |
| 浏览、搜索和筛选任务 | 是 | 是 |
| 文件夹、标签、备注、收藏、项目别名 | 是（写 ThreadShelf sidecar） | 是（写 ThreadShelf sidecar） |
| 打开会话文件位置 | 是 | 是（文件存在时） |
| 归档、取消归档、任务标题重命名 | 是（Codex 原生操作） | 否，控件会禁用 |
| 新建/继续交互式任务 | Codex 桌面版优先；否则 CLI 可执行且 workspace 有效时可用 | 与 app-server provider 独立 |

ThreadShelf 会按以下顺序寻找 CLI：有效的 `THREADSHELF_CODEX_CLI`、Windows 常见安装位置、然后是 `PATH` 中的 `codex`。覆盖值必须指向真实存在的可执行文件。

```powershell
$env:THREADSHELF_CODEX_CLI = "$env:LOCALAPPDATA\Programs\OpenAI\Codex\bin\codex.exe"
dotnet run --project ThreadShelf.App\ThreadShelf.App.csproj -p:Platform=x64
```

## 演示数据与截图复现

下面的脚本只使用仓库内的假 app-server 和 `artifacts/demo-codex-home`，不会读取或修改真实 Codex 数据：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Start-ThreadShelfDemo.ps1 -Language zh-CN
```

脚本会构建应用、生成虚构项目/任务/标签/备注并启动 ThreadShelf。README 截图由这套 fixture 生成，可安全复现和更新。

## 高级用法：CLI 与 MCP

```powershell
dotnet run --project ThreadShelf.Cli -- threads list --json
dotnet run --project ThreadShelf.Cli -- threads search "follow up" --json
dotnet run --project ThreadShelf.Cli -- threads batch-update --file organization.json --yes --json
dotnet run --project ThreadShelf.Mcp
```

完整工具、参数、权限、成功/错误 envelope 和安全边界见 [AI/CLI/MCP 使用指南](docs/ai-interface.md)。正常自动化应优先使用 MCP/CLI，不要直接编辑 sidecar，更不要编辑 Codex JSONL。

## 常见问题

### 无法继续或新建 Codex 任务

确认 Codex 桌面版已注册 `codex:` 协议，或确认 `codex --version` 能运行并将 `THREADSHELF_CODEX_CLI` 指向真实的 `codex.exe`。两者都不可用时，ThreadShelf 仍可浏览任务和编辑 sidecar 元数据；依赖 `app-server` 的归档/标题重命名会独立禁用。

### 看不到任务

确认 `CODEX_HOME` 是否指向正确目录，并检查 `session_index.jsonl`、`sessions` 或 `archived_sessions` 是否存在。清除搜索框，切回“所有项目/所有任务”，然后点击“刷新”。

### 归档或重命名按钮不可用

这些是 app-server 原生写操作。查看界面底部状态提示，确认 CLI 可启动 `codex app-server`。ThreadShelf 不会用本地假状态掩盖失败。

### 项目改名没有出现在 Codex 桌面版

当前公开 app-server 协议没有项目重命名方法，因此 ThreadShelf 使用显示别名。它不会改变 Codex 桌面版项目名、CLI workspace 或磁盘目录。

### 数据在哪里

查看窗口左下角显示的 sidecar 路径。语言偏好位于同目录的 `preferences.json`。

## 卸载

1. 关闭 ThreadShelf，删除克隆目录或已发布的程序目录。
2. 如果也要删除 ThreadShelf 元数据，先备份，然后删除 `%USERPROFILE%\.codex\threadshelf`（或 `$env:CODEX_HOME\threadshelf`）。
3. 不要删除 `sessions`、`archived_sessions` 或 `session_index.jsonl`；它们属于 Codex。

## 开发验证

代码按职责组织：桌面入口与顶层组合位于 `ThreadShelf.App/Program.cs` 和 `App.cs`，页面状态在 `State/ThreadShelfController.cs`，各 UI feature 在 `Components/`；Core 的共享用例、sidecar、provider 与 native/system actions 分别位于 `ThreadShelfService.cs`、`Persistence/`、`Sources/` 和 `Native/`；CLI 与 MCP 的 bootstrap、dispatch/catalog 和 handler 也保持独立文件。新增 MCP 工具时通过 `ThreadShelf.Mcp/ToolCatalog.cs` 的 descriptor registry 同时绑定 schema 与 handler。

```powershell
dotnet test tests\ThreadShelf.Tests\ThreadShelf.Tests.csproj
dotnet build ThreadShelf.App\ThreadShelf.App.csproj -p:Platform=x64
dotnet publish ThreadShelf.App\ThreadShelf.App.csproj -p:PublishProfile=WinX64NativeAot
powershell -ExecutionPolicy Bypass -File .\scripts\Test-ThreadShelfUi.ps1
powershell -ExecutionPolicy Bypass -File .\.codex\skills\thread-shelf-reactor\scripts\Test-ThreadShelfMcp.ps1
```

项目采用 [MIT License](LICENSE)。
