# Route C：Unity Hub 添加项目说明

## 项目入口

- Git 工作目录：`E:\帧同步_RouteC`
- Unity 项目目录：`E:\帧同步_RouteC\Project\Frame Synchronization`
- 开发分支：`delivery/route-c`
- Unity 版本：`2022.3.62f2`
- 首选场景：`Assets/Scenes/SampleScene.unity`

## 在 Unity Hub 中添加

1. 打开 Unity Hub，进入“项目”。
2. 选择“添加”或“从磁盘添加项目”。
3. 选择 `E:\帧同步_RouteC\Project\Frame Synchronization`。不要选择外层的 `E:\帧同步_RouteC`。
4. 确认使用 Unity `2022.3.62f2` 打开。
5. 第一次打开会重新生成 `Library` 并导入资源，等待导入和脚本编译完成。
6. 打开 `Assets/Scenes/SampleScene.unity`，检查 Console 后再进入 Play Mode。

## 注意事项

- 这是现有工程的独立 Git 工作树，不是重新创建的 Unity 项目，也不是新的 Git 仓库。
- 原学习目录 `E:\帧同步` 保持不变；Route C 的代码开发只在 `E:\帧同步_RouteC` 进行。
- 场景与 `.meta` 文件当前受仓库忽略规则影响，已从原工程同步到本目录。不要执行会清理忽略文件的 Git 命令，否则可能删除场景或破坏 Unity GUID 引用。
- 除非专门测试双客户端，否则不要同时让两个工程实例占用相同的网络端口。
- 禁止自动执行 `git push`；推送由帅老大手动完成。

## 首次打开验收

- Unity Hub 显示编辑器版本为 `2022.3.62f2`。
- `SampleScene.unity` 存在并能打开。
- 脚本完成编译，Console 没有新增编译错误。
- 未修改或切换原目录 `E:\帧同步` 的分支和文件。
