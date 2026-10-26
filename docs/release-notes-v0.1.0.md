# The Forest Cheat Panel v0.1.0

《森林》(The Forest) 的游戏内 F7 修改器面板。首个公开发布版本。

## 安装

1. 先安装 **BepInEx 5.4.23+ (x64)** 到游戏根目录
   - 装好后游戏目录会出现 `winhttp.dll` 和 `BepInEx\core`
2. 下载本 Release 里的 `ForestCheat.dll`
3. 放到 `<游戏目录>\BepInEx\plugins\ForestCheat.dll`
4. 启动游戏 → **启动器窗口点 Play** → 进入存档 → 按 **F7**

## 功能

- **上帝模式**：血量/护甲拉满、不流血、不感冒、不掉体温、落地无伤
- **状态锁定**：体力、精力、饱食、不渴、不饿、电池电量
- **移动加速**：速度倍数、跳跃倍数可调
- **坐标系统**：实时显示 X/Y/Z，输入坐标直接传送
- **坐标收藏夹**：数量不限、自定义命名、自动保存
- **物品生成**：读取游戏物品数据库（231 种），带**中文名显示**与中英混搜，
  支持输入中文反查、「全部物品各给 N 个」批量、物品清单导出

## 说明

- 全部通过反射访问 `LocalPlayer` / `PlayerStats` / `PlayerInventory` / `ItemDatabase`，
  **不修改、不反编译游戏文件**
- 编译需使用游戏自带的 .NET 3.5 程序集（`/nostdlib+`），否则 `AddComponent` 会静默失败
- 数据文件位于 `BepInEx\config\`：`坐标收藏.txt`、`物品清单.txt`、`物品中文对照.txt`
- 大量刷物品可能让存档变大，建议先备份
  `%USERPROFILE%\AppData\LocalLow\SKS\TheForest\`

## 兼容性

- 游戏版本：The Forest（Steam 正式版，Unity 5.6.5）测试通过
- 依赖：BepInEx 5.4.23.2 (win_x64)
- 若 F7 无反应：确认启动器点了 Play，再查 `BepInEx\forestcheat_diag.txt`
