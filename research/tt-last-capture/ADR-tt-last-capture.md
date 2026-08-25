------

# ADR — `tt last` Capture Architecture

**Status:** Accepted
**Date:** 2026-08-25
**Decision Owner:** Product Owner
**Scope:** Terminal Translator `tt last` 的终端输出捕获、边界识别、本地保留与读取架构

## 1. Context

Terminal Translator Phase 1 使用：

```text
tt start
→ hosted Windows PowerShell
→ ConPTY
→ program pane + translation pane
```

这种架构能够实时观察终端输出，但要求用户预先进入由 Terminal Translator 托管的 PowerShell 会话。

新的产品目标是允许用户继续正常使用普通 Windows Terminal + Windows PowerShell 5.1：

```powershell
PS> git status
...
PS> tt last
```

`tt last` 必须在命令已经执行完成之后，可靠获得当前 PowerShell session 中**严格意义上的上一条命令及其输出**，而不要求用户每次预先进入 `tt start`。

因此需要一种独立于 Phase-1 ConPTY host 的 capture architecture。

------

## 2. Architecture Investigation

研究阶段比较了多种方案，包括：

- PowerShell Transcript；
- Windows Console Buffer / `CONOUT$`；
- PowerShell / PSReadLine metadata；
- Windows Terminal shell integration / OSC 133；
- `Out-Default` / pipeline wrapper；
- resident/background recorder；
- 多种 hybrid。

DeepSeek 最初将 enlarged Console Buffer hybrid 作为 Primary candidate。

Grok 对该结论进行了对抗式证据审查，并发现旧 Console Buffer probe 的 Win32 `COORD` 参数可能存在 ABI / packing 错误。

Codex 随后进行了独立验证，确认：

> 旧 DeepSeek multi-row probe 的 `COORD` 声明错误，旧 multi-row evidence 无效。

之后在真实 Windows Terminal 1.24 + Windows PowerShell 5.1 中完成 V1–V5 head-to-head verification。

最终实验结果为：

```text
V1 CONOUT$ mixed-output coverage     PARTIALLY CONFIRMED
V2 enlarged 1000-line retention     CONFIRMED
V3 previous-command boundary        CONFIRMED WITH LIMITATION
V4 Transcript fidelity              CONFIRMED WITH LIMITATION
V5 resize/reflow loss               CONFIRMED
```

Transcript 在测试环境中完整保留了 PowerShell output、`Write-Host`、native stdout/stderr、PowerShell errors、Git output/error、Unicode，以及完整 1000 行输出；命令完成后的 0ms / 50ms / 200ms 读取结果一致，并且 `Clear-Host` 后历史仍然存在。Console Buffer 则出现默认容量截断、resize/reflow 不可逆历史丢失、`Clear-Host` 丢失以及 supplementary-plane Unicode degradation。

------

# 3. Decision

## 3.1 Primary Capture Architecture

`tt last` **采用 bounded PowerShell Transcript + session/command metadata 作为唯一正式 Capture Architecture。**

Console Buffer architecture **不进入产品，也不作为 fallback 保留。**

正式数据流为：

```text
Windows PowerShell 5.1
        │
        │ normal command execution
        ▼
bounded local Transcript
        +
session / command metadata
        │
        ▼
      tt last
        │
        ├─ identify current shell session
        ├─ identify previous completed command
        ├─ extract corresponding output
        ├─ validate capture integrity
        ├─ apply transmission-size policy
        ├─ SecretDetector
        ▼
   external AI provider
        │
        ▼
translation
+
one short recommendation
```

`tt last` 不重新托管 PowerShell，不启动新的 ConPTY host，也不依赖 Phase-1 live translation pipeline 才能获得输出。

------

# 4. Capture Lifetime and Capacity

Capture 只属于**当前仍然存在的 PowerShell session**。

不同 shell session 必须具有独立的 session identity，不允许按照“最新修改文件”等模糊规则猜测当前 session。

每个 session 中**已完成命令的 retained Capture Store** 具有固定硬上限：

```text
10,180,000 bytes
```

该值是精确字节数，不解释为 MiB。它不是当前命令执行期间原生 PowerShell Transcript
staging file 的实时物理文件上限；活动 staging file 可以暂时超过该值。

当前命令完成并到达可信的 command/prompt boundary 后，Terminal Translator 必须尽快将该命令
转化为 retained command record，并使 retained Capture Store 恢复到不超过 10,180,000 bytes。

达到容量上限以后采用 rolling retention：

```text
remove oldest complete command records
→ preserve newest complete records
→ continue capturing
```

普通累计容量超限时，不得从完整 command record 中间直接截断而破坏 boundary。

如果单条 completed command 自身超过 10,180,000 bytes，允许将它转换为 bounded HEAD + TAIL
representation。该 record 必须标记 local capture truncation，保持可信的 command identity 和
boundary，使总 retained Capture Store 仍不超过上限，并在 `tt last` / `tt ask last` 中明确告知
用户本地 Capture 本身已经截断；不得表现为拥有完整输出。

`tt last` 不跨 session 查找历史。

------

# 5. Cleanup

正常 PowerShell session 结束：

```text
delete capture immediately
```

异常崩溃、强制终止或无法正常清理：

```text
temporary residue may remain
→ next Terminal Translator initialization
→ clean stale capture before normal operation
```

Capture 不作为长期 shell history。

------

# 6. Storage Location and Access

Capture 应存放在当前用户的本地应用数据目录，例如：

```text
%LOCALAPPDATA%\TerminalTranslator\Capture\
```

而不是：

```text
Documents
Desktop
OneDrive
roaming profile
```

Capture 文件必须：

- current-user-only ACL；
- session-specific random identifier；
- 不进入 telemetry；
- 不进入普通 application log；
- 不自动进入 diagnostic/support bundle；
- 不同步；
- 不自动上传；
- 不提供用户级 `show` / `export` 功能。

本架构**不承诺抵御已经获得当前 Windows 用户权限、Administrator 或 SYSTEM 权限的恶意软件**。

------

# 7. Privacy Boundary

本地 Capture Store 可以原样保存当前终端已经产生的内容，包括其中可能存在的敏感数据。

敏感信息的硬安全边界位于：

```text
local capture
        ↓
content selected for external transmission
        ↓
SecretDetector
```

任何准备发送给外部 AI Provider 的 command/output 必须首先经过 SecretDetector。

检测到疑似 secret：

```text
do not send any part of that protected segment
```

不得：

- 自动上传；
- 让 provider 先看到再过滤；
- 因为用户执行了普通命令而主动外发；
- 因为 Capture 文件存在而产生 telemetry/sync。

Local persistence 与 External transmission 是两个独立 trust boundary。

------

# 8. Capture Enablement

Capture integration 在 Terminal Translator 安装/初始化时：

```text
explain purpose
+
request user choice
```

用户允许以后，普通 PowerShell session 自动使用 Capture。

不要求每次打开 PowerShell重新授权。

如果 Capture 被禁用：

```powershell
tt last
```

必须明确返回类似：

```text
tt last capture is disabled.
Enable it to capture future command output.
```

不得静默改用 Console Buffer 或其它 fallback。

------

# 9. Failure Isolation

Capture subsystem 绝不能成为正常 PowerShell execution 的 dependency。

即：

```text
Capture failure
≠
Shell failure
```

如果 Capture 写入、metadata、boundary 或其它内部机制失败：

- 用户当前 command 必须继续正常执行；
- 不得修改 command exit code；
- 不得阻塞 stdin/stdout；
- 不得终止 PowerShell；
- prompt 附近只提示一次 Capture unavailable；
- 恢复后可以提示一次 Capture restored。

例如：

```text
[tt] Capture unavailable. `tt last` will not work until capture recovers.
```

不得每个 prompt 重复刷屏。

------

# 10. Previous Command Semantics

`last` 严格表示：

> **当前 PowerShell session 中上一条已经完成的命令。**

例如：

```powershell
git status
cd ..
tt last
```

`tt last` 对应：

```text
cd ..
```

如果 `cd ..` 没有可翻译输出：

```text
Previous command has no translatable output.
```

不得偷偷继续向前寻找 `git status`。

如果当前 session 尚不存在上一条命令：

```text
No previous command output is available.
```

------

# 11. Command Boundary

正式 Capture 必须使用：

```text
session identity
+
command metadata
+
explicit reliable boundary
```

识别上一条命令。

不得仅依赖：

```text
^PS .*>
```

之类 prompt regex。

必须避免：

```powershell
tt last
```

自身成为被识别的“previous command”。

真实实验已验证 session GUID + sentinel/metadata 方案可以正确处理普通命令、空输出、continuation、自定义 prompt、session switch 和 capture-command self-pollution。

当前已知限制是：

> Windows PowerShell 5.1 尚未证明存在完全独立、通用的 command-start hook。

因此 V1 产品实现只承诺当前正式 integration 能建立的受控 command boundary。

------

# 12. `Clear-Host`

`Clear-Host`：

```text
clears presentation
≠
clears tt capture history
```

因此：

```powershell
some-command
Clear-Host
tt last
```

仍允许根据 Transcript semantics 获取上一条命令。

这里不采用 Console Buffer 的“画面已经消失，所以历史不存在”语义。

------

# 13. Output Translation

只要上一条命令存在可翻译英文，就允许翻译，不设置最小文本长度。

发送给模型的上下文允许包含：

```text
command text
+
command output
+
relevant exit-code metadata
```

command text 仅作为理解上下文：

> **不得翻译 command 本身。**

路径、URL、参数、代码、error code、identifier 等技术内容应尽量保持原样。

stdout 与 stderr 按 Transcript 中捕获到的用户实际观察顺序共同处理。

------

# 14. Mixed Language

如果上一条输出已经主要是中文且没有值得翻译的英文：

```text
No translatable English content was found.
```

不得为了产生回答而无条件发送模型。

中英混合输出采用：

```text
entire selected output as context
```

并要求模型：

```text
translate English natural language into Chinese
preserve existing Chinese
preserve technical tokens
```

------

# 15. Long Output and AI Input Limit

Capture capacity：

```text
10,180,000 bytes
```

该容量是已完成命令 retained Capture Store 的 hard limit；活动 PowerShell Transcript staging file
在命令执行期间可以暂时超过它，但必须在安全 command/prompt boundary 后尽快完成 retention，
使 retained Capture Store 恢复到不超过该值。

它与 AI request limit 是两个完全不同的限制。

不得假定能够将完整 10,180,000-byte Capture 一次发送给模型。

如果上一条输出低于 AI input limit：

```text
send complete selected output
```

如果本地 Capture 完整，但超过 AI input limit：

```text
retain complete local capture
        ↓
select HEAD + TAIL
        ↓
SecretDetector
        ↓
send selected context
        ↓
ask model for summarized translation
```

此时必须明确告诉用户：

> 原输出过长，本次结果是根据输出开头和结尾生成的总结翻译。

不得表现为“完整逐行翻译”。

AI input limit 的具体字节/token 数值在 Plan / Provider policy 中确定，不在本 ADR 固定。

必须区分两种不同状态并使用不同用户提示：

- **local capture truncation**：单条 completed command 超过本地 retained limit，中间内容已经不再
  保留；上下文只能来自本地保留的 HEAD + TAIL representation。
- **AI input truncation**：本地 Capture 完整，但 Provider input budget 不足，只向 Provider 发送
  HEAD + TAIL。

如果一次请求同时发生两种选择，两个状态都必须保留并分别披露；不得让用户误以为本地仍有完整
输出，或误以为 Provider 看过完整输出。

------

# 16. `tt last` Result UX

`tt last` 在当前 shell 直接打印结果。

不得为了 `tt last` 再打开 companion pane。

正常结构为：

```text
[翻译]
...

[建议]
...
```

不重复打印原始 command output，因为原始内容已经位于当前 terminal history。

每次正常完成的 `tt last` 最后提供**一条简短建议**：

- 大约 1–2 行；
- 可以包含具体 shell command；
- AI-generated command 只能显示；
- Terminal Translator 永远不得自动执行该命令。

Phase-1：

```text
tt start
```

仍然可以继续使用 companion pane，其 UX 不被本 ADR 改变。

------

# 17. Interrupted Commands

如果用户通过：

```text
Ctrl+C
```

中断命令：

只要 Capture 可以可靠确定已经产生的 output 和 termination boundary，`tt last` 可以翻译截至中断时已经捕获的内容。

模型上下文应包含“command was interrupted”的状态。

------

# 18. Async Output

V1 semantics：

> `tt last` 只认 command completion boundary 之前归属于该命令的输出。

prompt 返回以后产生的后台/job/async output 不自动重新归属给上一条 command。

首版不尝试建立复杂异步 provenance tracking。

------

# 19. Corruption / Incomplete Capture

如果：

- Capture 文件损坏；
- boundary 缺失；
- metadata 不一致；
- session identity 无法可信确认；
- previous output 无法完整、可靠恢复；

则：

```text
fail closed
```

不得猜测上一段文本。

用户必须立即收到明确提示，例如：

```text
[tt] Previous command output could not be recovered reliably.
```

不得将不可信文本发送给 Provider。

------

# 20. Supported Environment

V1 正式支持：

```text
Windows Terminal
+
Windows PowerShell 5.1
```

因为该组合已经完成真实环境验证。

PowerShell 7、传统 Console Host、其它终端、其它 shell 不自动成为 V1 compatibility promise。

它们可以以后独立验证。

------

# 21. TUI / Interactive Applications

V1 的 `tt last` 面向：

> **普通、能够完成并返回 prompt 的 command。**

不承诺完整支持：

- Vim；
- Codex interactive UI；
- full-screen TUI；
- REPL；
- 持续 redraw applications；
- 任意 ANSI/full-screen terminal state reconstruction。

这些场景未来可以独立设计。

------

# 22. External Provider Consent

Provider / destination / relevant transmission configuration 未发生变化时：

```text
user consents once
→ future explicit `tt last` requests may transmit eligible content
```

配置发生影响 trust boundary 的变化时必须重新授权。

执行：

```powershell
tt last
```

本身属于显式用户触发。

------

# 23. Related AI Command

Terminal Translator 同时规划独立的 stateless AI question command：

```powershell
tt ask "为什么这里报错？"
```

只发送用户输入的问题，不读取 Capture。

以及：

```powershell
tt ask last "这个报错为什么发生？"
```

读取当前 session 上一条 command/output 作为额外上下文。

两者第一版均为 stateless。

未来连续对话能力：

```powershell
tt chat
```

不属于本 ADR 范围。

AI 可以建议 shell commands，但 Terminal Translator 不自动执行 AI-generated command。

------

# 24. Alternatives Rejected

## Console Buffer / `CONOUT$`

**Rejected from product architecture.**

虽然实验确认提前扩大 buffer 后可以完整保留 1000 行，但存在：

- 默认 viewport capacity 严重截断；
- resize/reflow 可不可逆驱逐旧历史；
- `Clear-Host` 删除内容；
- supplementary Unicode degradation；
- retained screen state 与 logical command history 不完全等价。

因此不保留为 fallback。

## Default viewport-only Console Buffer

Rejected。

默认实验 1000 行仅留下最后 48 行。

## Out-Default / Tee / Pipeline wrappers

Rejected as primary。

会改变 shell/pipeline semantics，且不能可靠覆盖所有 native output。

## PSReadLine / Get-History alone

Rejected。

可以提供 command history，但不能提供完整 program output。

## Windows Terminal shell marks / exportBuffer

Rejected as product dependency。

本轮未找到受支持、稳定、供外部 `tt.exe` 调用的 pane-content query API。

## Resident recorder

Rejected as independent capture architecture。

如果最终仍依赖 Transcript，它只是生命周期选择，不是新的 content source。

------

# 25. Consequences

该决定带来的主要收益：

```text
普通 PowerShell UX
+
无需 tt start
+
可靠 long-output capture
+
完整 Unicode fidelity
+
Clear-Host independent history
+
native stdout/stderr coverage
+
可明确建立 command/session boundary
```

代价：

```text
需要本地暂存 command output
+
需要 PowerShell integration
+
需要严格 retention/cleanup
+
需要管理 crash residue
+
需要保护 capture ACL
+
TUI/full-screen fidelity 不属于首版保证
```

这些是接受的架构 trade-off。

------

# 26. Non-Goals

本 ADR 不决定：

- Provider 具体模型；
- AI input limit 的具体数值；
- Prompt wording；
- Transcript file rotation 的具体内部算法；
- class/interface 命名；
- Phase-1 live-mode 重构；
- PowerShell 7；
- TUI capture；
- crash-tail recovery guarantee；
- `tt chat`；
- AI-generated command execution。

------

# 27. Final Decision

**ACCEPTED**

Terminal Translator `tt last` 将采用：

> **Bounded PowerShell Transcript + session/command metadata**

作为唯一正式 Capture Architecture。

正式 Capture：

```text
current session only
completed-command retained hard cap = 10,180,000 bytes
active transcript staging may temporarily exceed the retained cap
safe command/prompt boundary → promptly restore retained store to <= hard cap
rolling eviction by complete command boundary
single oversized completed command → bounded HEAD + TAIL + local-truncation disclosure
normal session exit → delete
crash residue → clean on next initialization
current-user-only local storage
no user-facing export
no automatic sync/upload
```

任何内容发送外部 AI Provider 前必须经过 SecretDetector。

Console Buffer architecture：

> **Rejected and removed from the product design; no fallback retained.**

Architecture Investigation 至此结束。

下一阶段进入：

```text
ADR Accepted
      ↓
Product Semantics → formal requirements
      ↓
Spec Kit / specify
      ↓
Plan
      ↓
Tasks
      ↓
Implementation
```

------

