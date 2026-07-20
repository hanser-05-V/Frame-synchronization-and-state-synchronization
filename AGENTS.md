# 帧同步项目 — Codex 配置

## LS 开发部门 — 会话持久化

当在本目录启动 `/Ls` 时，**不要创建新的时间戳会话文件夹**。
使用固定的持久化会话目录：

```
E:\LS_Claude_Agents\帧同步_20260717_1536\
```

### 启动流程

0. **先拉取双仓库再读档**（双机同步铁律，防止在旧状态上工作）：
   `git -C "E:\帧同步" pull` + `git -C "E:\LS_Claude_Agents" pull`
1. 读取 `E:\LS_Claude_Agents\帧同步_20260717_1536\任务墙.md` — 获取当前所有任务状态
2. 读取 `E:\LS_Claude_Agents\帧同步_20260717_1536\RESUME.md` — 获取当前进度
3. 读取 `E:\LS_Claude_Agents\帧同步_20260717_1536\路线规划_街篮帧同步最小实现.md` — 获取开发路线
4. 汇报给帅老大：当前阶段、任务墙状态、下一步操作

### 写入规则

- 任务墙更新 → `E:\LS_Claude_Agents\帧同步_20260717_1536\任务墙.md`
- 交接记录 → `E:\LS_Claude_Agents\帧同步_20260717_1536\交接记录\`
- 会话记录 → `E:\LS_Claude_Agents\帧同步_20260717_1536\agent_会话记录\`
- 进度更新 → `E:\LS_Claude_Agents\帧同步_20260717_1536\RESUME.md`
- 路线规划更新 → `E:\LS_Claude_Agents\帧同步_20260717_1536\路线规划_街篮帧同步最小实现.md`

### 双机同步铁律（公司 ↔ 家）

帅老大要求：**任一电脑随时能从 GitHub 拉到最新工作状态并继续推进。** 以下时机必须立即 `git commit`（本地保存），**git push 由帅老大手动执行**：

1. 任务墙状态变化（任务开始 / 完成 / 阻塞 / 拍板决策）
2. 新增交接文件、RESUME 进度更新
3. 代码修改通过审查后
4. 笔记写入后
5. 会话结束或帅老大说"暂停 / 下班"时（无论进行到哪一步）

| 本地路径 | GitHub 远程 |
|----------|------------|
| `E:\帧同步` | `hanser-05-V/Frame-synchronization-and-state-synchronization` |
| `E:\LS_Claude_Agents` | `hanser-05-V/Ls_Claude_Agents` |
| `E:\Frame`（参考源码，只读） | `hanser-05-V/Frame`（**必须保持 Private**） |

参考源码（Common/Framework/Script，街篮2）不入主仓（.gitignore 排除，防公开泄露）；
家中首次恢复：clone `Frame` 后把三个目录复制到 `E:\帧同步\` 下即可，后续基本不变。

推送前先 `git pull --rebase`；出现冲突立即停下汇报帅老大，**一律禁止 force push**。
**git push 由帅老大手动操作，禁止自动执行。**
若家中盘符不是 E:\，需同步修改本文件与 CLAUDE.md 中的写死路径。

### 禁止

- ❌ 禁止在本目录创建新的 `E:\LS_Claude_Agents\帧同步_{时间}\` 文件夹
- ❌ 禁止覆盖已有的任务墙（只能追加/更新状态）
- ❌ 禁止覆盖已有的 RESUME（只能更新进度）
- ❌ 禁止自动执行 `git push`（由帅老大手动操作）
