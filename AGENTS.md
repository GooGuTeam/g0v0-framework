# AGENTS.md

面向在本仓库工作的 AI 编码代理（以及人类贡献者）。
本文件只写**在这个 fork 里必须知道、且从上游 osu!framework 资料看不出来**的内容；框架通用架构请直接读代码。

---

## 1. 这是什么项目

- 本仓库是 [ppy/osu-framework](https://github.com/ppy/osu-framework) 的 fork，由 **GooGuTeam** 维护，产品名 **g0v0!framework**；NuGet 包名 `g0v0.Framework`（`osu.Framework/osu.Framework.csproj` 的 `PackageId`）。
- 相比上游的主要改动（fork 基线提交，判断差异是否有意时先查它们）：
  - 加载错误边界：`osu.Framework/Development/LoadErrorHandling.cs`、`osu.Framework/Graphics/LoadErrorPlaceholder.cs`（新增）、`FrameworkConfigManager.cs`、`GameHost.cs` 及 `Drawable` / `CompositeDrawable` 系列（PR #1 `feat/load-error-boundary`）；
  - 包名与发布管线切换：`16b556b17`；
  - 许可拆分与版权头重写：`84c75e2d8`（已记入 `.git-blame-ignore-revs`）。
- 许可：MIT，根目录两份 —— `LICENCE`（本 fork 新增部分，GooGuTeam）与 `LICENCE-OSU`（上游部分，ppy Pty Ltd）。NuGet 元数据集中在 `Directory.Build.props`。
- **只有主包改名 `g0v0.Framework`**；`ppy.osu.Framework.{Android,iOS,Templates,NativeLibs,SourceGeneration}` 保持原名，不要「顺手统一」。

### Git 布局

| 远端 / 分支 | 说明 |
|---|---|
| `origin` | `https://github.com/GooGuTeam/g0v0-framework.git` |
| `upstream` | **本地未配置**；同步前先 `git remote add upstream https://github.com/ppy/osu-framework.git`（只读） |
| **`master`** | **默认开发分支**；上游对应分支也是 `master` |
| 标签 | 发布标签形如 `v2026.1010.0-g0v0`，由 `.github/workflows/release.yml` 处理（细节读该文件）；当前本地无任何标签，首次 `git fetch upstream` 后上游历史标签会大量涌入，推送规则见第 8 节 |

---

## 2. 环境与常用命令

需要 .NET SDK **10.0.100+**（`global.json` 固定 10.0.100，`rollForward: latestFeature`，`allowPrerelease: false`）；`LangVersion` 14.0、`Nullable` 已开启（`Directory.Build.props`）。

```bash
dotnet restore osu-framework.Desktop.slnf
dotnet build -c Debug -warnaserror osu-framework.Desktop.slnf                                   # 与 CI 一致
dotnet build -c Debug -warnaserror osu-framework.Desktop.slnf -p:EnforceCodeStyleInBuild=true   # 额外开启代码风格分析器

./build.sh                 # Cake 构建（build/build.cake；Windows 用 ./build.ps1）；日常编译用上面的 dotnet build 即可
bash InspectCode.sh        # CodeFileSanity + InspectCode + nvika（必须先构建；Windows 用 ./InspectCode.ps1）

# 测试：对构建产物跑（--no-build），可加 --filter 选子集
dotnet test osu.Framework.Tests/bin/Debug/**/osu.Framework.Tests.dll --no-build
dotnet test osu.Framework.Tests/bin/Debug/**/osu.Framework.Tests.dll --no-build --filter "FullyQualifiedName~TestSceneName"
```

- 方案过滤器：`osu-framework.Desktop.slnf`（桌面 + 测试）、`osu-framework.Android.slnf`、`osu-framework.iOS.slnf`。**不要直接构建 `osu-framework.sln`**。
- `bin/`、`obj/` 已在 `.gitignore` 中，不要提交。

---

## 3. AI 开发指北

### 动手前

1. 判断目标代码是否被 fork 改过：`git log --follow <file>`，对照第 1 节的三个基线提交；拿不准是不是「fork 有意为之」时**先查历史再动手**，别凭感觉改回上游样子。
2. 改公共 API（`osu.Framework` 对外表面）前，先看上游对应实现与既有测试，确认改动方式与仓库风格一致。
3. 涉及加载错误边界相关类型（`LoadErrorHandling` / `LoadErrorPlaceholder` / `GameHost` 的集成点）时，注意它们是本 fork 的核心增量，改动影响面比看起来大。

### 改动中

- `.csproj` / `.props` / `.targets` 是 **UTF-8 BOM + CRLF + 2 空格**（`.editorconfig` 强制）；部分编辑工具会静默剥掉 BOM，改完核对文件头（`head -c 3 | xxd` 应为 `efbbbf`），丢了就补回。
- 新建 `.cs` 文件立即按第 5 节加上正确类别的版权头，别留到最后。
- 不碰生成目录与 `CodeAnalysis/`、`.github/workflows/`（除非任务明确要求）。

### 提交前（检查清单，按顺序）

1. `dotnet build -c Debug -warnaserror osu-framework.Desktop.slnf` —— 编译 warning 即失败；
2. 涉及公共 API、可空性、分析器规则、性能写法时：`bash InspectCode.sh`（见第 4 节的约束细节）；
3. 跑与改动相关的测试（第 6 节），不要动辄全量；
4. 核对版权头分类是否需要升降级（第 5 节「分类是动态的」）；
5. `git diff` 里不应出现无关格式变动（行尾、BOM、无关文件）。

---

## 4. 代码检查约束（CI 会拦，`.github/workflows/ci.yml` 的 Code Quality job）

| 检查 | 触发方式 | 约束 |
|---|---|---|
| 编译 warning | `dotnet build -warnaserror` | 任何编译 warning 都会 fail |
| 代码风格 | 同上 + `-p:EnforceCodeStyleInBuild=true` | `.editorconfig` 风格规则在构建期生效 |
| 禁用 API | `Microsoft.CodeAnalysis.BannedApiAnalyzers` + `CodeAnalysis/BannedSymbols.txt` | 用了名单内 API 直接构建报错（RS0030） |
| 分析器全局配置 | `CodeAnalysis/osu-framework.globalconfig` | 各规则严重度按该文件 |
| CodeFileSanity | `dotnet codefilesanity`（无参数扫全仓库） | 见下方详细说明 |
| InspectCode | `dotnet jb inspectcode osu-framework.Desktop.slnf` + `dotnet nvika parsereport --treatwarningsaserrors` | warning 等同 error，脚本退出码非 0 即失败 |

### CodeFileSanity 语义（实测，容易踩坑）

- 它按根目录 `osu-framework.licenseheader` 的**单一**头部定义对每个文件做**两行精确匹配**，另查「文件名与文件内类型名一致」等规范；任何输出行即 exit 255。
- 本仓库把 licenseheader 定义设为**第 3 类文本**（ppy 头 + `See the LICENCE-OSU file...`），覆盖绝大多数未改动上游文件；因此**第 1 / 2 类文件（GooGuTeam 新建、改过的上游文件）会被它报 `License header missing`，这是预期噪音，不是错误**。
- `ci.yml` 的 CodeFileSanity 步骤把 `License header missing` 行过滤为不致命（三类方案无法用单一模板表达），**其余任何 finding 仍然致命**。这意味着：漏加头的真正疏漏 CI 拦不住，版权头正确性靠按第 5 节口径人工维护。
- `.cfsignore` 在当前固定版本（0.0.41）**不生效**，别指望它。

### InspectCode 脚本行为

- `InspectCode.sh` / `InspectCode.ps1` 会**自动先跑 CodeFileSanity** 再跑 InspectCode；本地跑一次 `bash InspectCode.sh` 即等价于 CI 的两项 sanity 检查。
- InspectCode 用 `--no-build` 跑，**必须先构建**，否则找不到程序集；工具版本固定在 `.config/dotnet-tools.json`（`dotnet tool restore` 自动装，首次需联网）。

---

## 5. 版权头（按文件来源三种）

```csharp
// 1) GooGuTeam 全新创建的文件
// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// 2) 修改了上游文件
// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

// 3) 未改动的上游文件（版权行保留 ppy 原文，第二行指向 LICENCE-OSU）
// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE-OSU file in the repository root for full licence text.
```

- 全仓库统一**英式拼写**（`Licence` / `LICENCE`），与上游 ppy/osu 一致；不要写成美式 `License` / `LICENSE`。
- 根目录许可文件是 `LICENCE`（GooGuTeam 部分）与 `LICENCE-OSU`（上游部分），头里的引用必须与文件名一致：GooGuTeam 自己的文件指 `LICENCE`，纯上游文件指 `LICENCE-OSU`，两者都涉及就都写。
- 第 3 类与上游字面上只差第二行的文件名，这是有意为之；同步时若该行产生冲突，按本文件的口径解决。
- 分类是**动态的**：首次修改一个第 3 类文件时，头部要同步升到第 2 类（版权行补 `& GooGuTeam`，第二行改为 `LICENCE & LICENCE-OSU`）；同步上游引入的新文件落地为第 3 类，只有随后被我们修改才升级。
- `osu-framework.licenseheader` 是 IDE 自动插头模板兼 CodeFileSanity 的匹配基准，内容为**第 3 类**文本；新建 GooGuTeam 文件时模板插出来的头要手动改成第 1 类（见第 4 节的 CFS 说明）。
- ppy 的版权署名是 MIT 的要求，不是商标问题，**不要删**。
- 大范围调整版权头的提交要记入 `.git-blame-ignore-revs`（`84c75e2d8` 已在列）。

---

## 6. 测试约束

- 测试项目：`osu.Framework.Tests`（主测试库）、`osu.Framework.SourceGeneration.Tests`。新测试放进对应项目，**测试代码同样受第 4 节全部检查约束**（版权头、命名、分析器）。
- 跑法是**对构建产物**跑：先 `dotnet build`，再 `dotnet test <产物 .Tests.dll> --no-build`，可加 `--filter`（如 `FullyQualifiedName~<TestScene 名>`）只跑子集。
- `osu.Framework.Tests` 的部分网络用例依赖本地 **httpbin**：`go install github.com/mccutchen/go-httpbin/v2/cmd/go-httpbin@latest` 后台运行后再跑全量，否则相关用例会挂。
- CI 的 Test job 跑 Windows / macOS / Linux × Debug（Linux 另有 Release）矩阵；本地至少过一遍 Linux Debug 再交。

---

## 7. 同步上游（例行操作，最容易踩坑）

1. `git fetch upstream`，把 `upstream/master` 合并进 `master`；解冲突时版权头按第 5 节口径处理（上游侧通常是第 3 类，别把 GooGuTeam 署名弄丢）。
2. 合并后检查 `.github/workflows/*.yml` 里的 `dotnet-version` 是否与 `global.json`、csproj 的 `TargetFramework` 对齐 —— 不对齐会直接让 CI 构建失败。
3. 上游若改了 `Directory.Build.props` 的 NuGet 元数据或 `osu-framework.licenseheader`，冲突时以 GooGuTeam 侧为准（那是 fork 改名的核心；licenseheader 保持第 3 类文本）。
4. 同步**前**先打一个基线标签（如 `sync-baseline-YYYYMMDD`）再动手，方便 diff 与对账。

---

## 8. 提交与协作

- 提交信息沿用上游风格：祈使句、首字母大写、句末不加句号，例如 `Add framework config for error handler`；来自上游的提交保持原样并保留 `(#PR号)`，便于对账。
- 新分支从 `master` 切出，PR 提到 `GooGuTeam/g0v0-framework`。
- 大范围重格式化 / 重写版权头的提交要记入 `.git-blame-ignore-revs`，避免污染 blame。
- **推送标签时只推 `-g0v0` 的发布标签，绝不要 `git push --tags`** —— fetch 过上游后本地会有大量上游历史标签，一条命令就会全部泄到 `origin`。始终用显式标签名：`git push origin v2026.1010.0-g0v0`。
- 打标签、推送远端、发布这类对外可见且不可逆的动作，先问人。
