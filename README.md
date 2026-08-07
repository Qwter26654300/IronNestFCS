# IronNestFCS Community Patch 1.2.2

> **基于 [gxpppp/IronNestFCS](https://github.com/gxpppp/IronNestFCS) 的社区修改版**
> 上游原版：[svr2kos2/IronNestFCS](https://github.com/svr2kos2/IronNestFCS) · MIT License

[Demo Video](https://www.bilibili.com/video/BV1xc7F6WEET/)

[Iron Nest: Heavy Turret Simulator](https://store.steampowered.com/app/4300500/) 的 [MelonLoader](https://melonwiki.xyz/) Mod，为游戏中的重型炮塔加入一套自动化**火控系统（Fire Control System, FCS）**：自动扫描或点选目标，解算弹道，采购/装填炮弹，调整方向机与高低机，并完成确认与击发流程。

> 基于 Iron Nest: Heavy Turret Simulator 开发，使用 IL2CPP + MelonLoader。  
> 1.2.2 延续 1.2.0 的“动态候选池 + 真实击发预览 + 方向机行进链 + 弹药状态恢复”速射调度模型，并补充了目标死亡准备中止、成弹异常阻塞恢复和采购卡动态识别。

## 相比 1.2.1 的变化

- **目标死亡后停止准备**：任务进入采购、补药、诸元计算、装填前会再次核实当前实体是否仍然存活；如果目标已被摧毁，FCS
  会释放任务，不再继续兑换、装填或开火。
- **成弹异常阻塞恢复**：如果炮膛已有炮弹和药包，但游戏状态长时间没有进入 `CanFire=true`，FCS
  会把该炮位标记为资源阻塞并释放方向机，避免一门炮拖住另一门炮。
- **资源阻塞自动接回**：资源阻塞炮位后续如果变成可击发状态，进度监控会自动接回当前任务；按 `Numpad 0` 清除 halt
  时也会先检查炮膛状态，不会盲目重启。
- **采购卡动态识别**：采购台不再只依赖手写弹种字段，会自动识别当前关卡实际存在的 `*Shell` 购买卡；常用弹种正常记录，冷门弹种如果可购买会在日志中提示。
- **低频诊断日志**：成弹等待和资源阻塞会打印 `chamber / actualPowder / canFire / hasFired` 等关键状态，方便定位游戏内装填状态不同步问题。

## 相比 1.1.x 的主要变化

- **动态候选池**：雷达、地图标点和快捷键产生的目标不再按固定 FIFO 队列执行；每次派发前都会按当前战场、炮位状态和方向机角度重新排序。
- **移动目标刷新**：雷达重新扫描后，会按同一实体的 `EntityLocation` 刷新候选池中尚未分配的目标角度、距离和坐标；正在执行的左右炮任务不会被中途改写。
- **真实击发预览**：左上角 `Queue` 显示的是当前 FCS 真实预计击发顺序，最多显示前 20 个目标；如果已装弹炮位会先打兼容目标，预览里也会同步体现。
- **手动标点优先**：切到手动标点后，玩家手动选择的 T1-T4 会标记为手动优先，压过普通扫荡候选，不再需要 F9 清空队列。
- **恢复自动即时扫描**：`Numpad 5` 从手动标点切回自动标点时，会立刻强制雷达扫描一次；`Numpad 0` 开启持续扫荡时也会先刷新当前敌方实体位置。
- **方向机行进链**：同优先级目标会沿当前方向机运动方向排序，减少大角度来回摆动；只有更高价值或同档更高星目标才允许明显打断当前方向链。
- **已装弹/已装药恢复**：炮膛已有弹、药包已推进、待击发或药包已在架子上时，系统优先寻找兼容且就近的目标，把当前这发尽快打出去。
- **资源不足停火**：没钱买炮弹或没钱补足药包时，自动扫荡/自动开炮会停止，避免“买失败 -> 继续诸元计算 -> 再买失败”的空转。
- **目标死亡保护**：当前任务绑定的实体如果在准备阶段被摧毁，会在采购、补药、装填和击发前停止当前任务，避免对已死亡目标继续消耗资源。
- **动态采购卡**：采购台会读取当前关卡实际存在的炮弹购买卡；没有对应购买卡时不会假定该弹种可购买。
- **双炮共享方向机保护**：待击发、瞄准中、等待装填的炮位都会参与方向机优先级；下一发预转不会越过另一门已分配且更紧急的炮。
- **暂停/失焦保护**：游戏暂停、窗口失焦或日志窗口切换时，FCS 会暂停点击、测算、采购、装填、开火和部分超时计时。
- **目标存活清理**：候选池会清理已摧毁、已不存在、已分配或重复签名的目标，尽量避免对失效目标继续派发。

## 功能

- **战术雷达**：每 3 秒自动扫描战场，通过实体 Icon/Role
  位掩码精准识别敌方阵营（基于 [Iron Nest DB](https://ironnestdb.com/enemies)
  数据），自动排除平民、警察、友军、参照点等非敌对目标，并刷新候选池中同一实体的最新位置。
- **持续扫荡模式**：`Numpad 0` / `Ctrl+0` 切换。开启后会先强制扫描一次，新出现的敌对目标会进入候选池，面板显示 `[Sweep ON]`。
- **双重标点模式**：`Numpad 5` / `Ctrl+5` 切换自动/手动标点。自动模式由雷达放置
  T1-T4；手动模式保留玩家拖动，并让手动选择目标优先于普通扫荡候选。再次切回自动时会立即扫描并校正移动后的实体位置。
- **优先打击**：高星目标、FDC、炮兵优先，其次装甲/工事/坦克，再到普通敌对目标。同档内再按星级和方向机行进链优化速射效率。
- **已装弹目标重匹配**：如果炮膛中已有弹和药包，FCS 会优先寻找弹种与装药兼容的目标，尽量避免卡膛、浪费或错误跳到低价值目标。
- **双炮管任务调度**：左右炮并行处理，自动分配空闲炮位；共享方向机由调度器统一保护。
- **自动弹道解算**：读取目标方向角与距离，自动设定装药、弹种并解算仰角。
- **多弹种支持**：AP / HCHE / HE / STAR / SMK，可通过控制台旁按钮选择当前弹种；APHE / ATMC 等冷门弹种也可通过冷门弹种选择器切换。弹仓缺弹时，FCS
  会按当前关卡实际存在的购买卡自动采购。
- **自动击发（可选）**：面板 `Auto Fire` 控制是否自动完成最后击发动作。
- **最大装药（可选）**：面板 `Max Charge` 可强制使用 6 号装药。
- **炮管强制重置**：`Numpad 7/8/9` / `Ctrl+7/8/9` 可重置左炮、右炮或双炮。
- **状态面板**：IMGUI 窗口实时显示两管炮任务、目标参数、候选池、真实击发预览、扫荡状态和资源阻塞原因。
- **热重载开发**：火控逻辑独立成可卸载程序集，开发时改完代码按 **F9** 可重新加载。
- **自定义唱片机**：附带独立 `IronNestFCS.CustomRecords` Mod，扫描 `UserData/CustomRecords/` 下音频文件并替换游戏内 RecordDisk。

## 键盘快捷键

| 按键           | 笔记本替代      | 功能                                   |
|--------------|------------|--------------------------------------|
| `F9`         | -          | 热重载火控逻辑（开发用）                         |
| `Numpad 0`   | `Ctrl+0`   | 切换持续扫荡模式，开启时强制扫描，并清除资源 halt 状态       |
| `Numpad 1-4` | `Ctrl+1-4` | 击发 T1-T4 目标                          |
| `Numpad 5`   | `Ctrl+5`   | 切换自动/手动标点；切回自动时强制扫描，手动模式下 T1-T4 任务优先 |
| `Numpad +`   | -          | 实验性：拧开所有蒸汽泄漏点附近阀门                    |
| `Numpad -`   | -          | 实验性：拧紧所有蒸汽泄漏点附近阀门                    |
| `Numpad 7`   | `Ctrl+7`   | 强制重置左炮                               |
| `Numpad 8`   | `Ctrl+8`   | 强制重置右炮                               |
| `Numpad 9`   | `Ctrl+9`   | 强制重置双炮                               |

> 蒸汽阀门快捷键通过场景中 `steam leak` 附近最近的 `DialInteractable` 推断阀门，属于实验功能。

## 目标优先级

自动扫荡和已装弹恢复都会尽量遵守同一套目标价值判断：

| 优先值 | 目标类型 |
| --- | --- |
| 4 | ★≥3 目标、FDC、炮兵 |
| 3 | ★≥1 目标、装甲/工事/坦克 |
| 2 | Hostile / Target 等普通敌对目标 |
| 1 | 其他可识别目标 |
| 0 | 友方或不应打击目标 |

普通派发顺序：

```text
最高目标优先级
  -> 同档星级
  -> 同档方向机行进链
  -> 距离兜底
```

如果某门炮已经有物理弹药状态，例如 `CanFire=true`、炮弹已入膛、药包已推进或药包已放在架子上，这门炮会优先处理当前这发：

```text
同弹种
  -> 装药数量兼容
  -> 方向机最近
  -> 目标价值
  -> 星级
```

这意味着：空闲炮位会优先打高价值目标；已经快能开火的炮位会优先找兼容目标把当前这发打出去。

## 方向机调度

方向机是左右炮共享资源。1.2.2 中，方向机调度遵循以下原则：

- 待击发高于瞄准中，瞄准中高于等待装填。
- 已装弹/已装药的炮位优先于普通预转。
- 同档目标沿当前方向链排序，减少来回摆动。
- 新目标只有价值更高，或同价值但星级更高，才明显打断当前方向链。
- 近角目标允许接管；角度差很小的任务不会反复抢占。
- 下一发预转不会越过另一门已分配且更紧急的炮。

例如当前方向机约 `44`，同为高价值目标：

```text
已有目标：70、105
新目标：50、100
排序结果：50 -> 70 -> 100 -> 105
```

低价值目标不会插到高价值链条前面。高价值链打空后，低价值目标才作为填充候选。

## 资源不足处理

如果买炮弹失败，或者购买后炮弹没有进入弹仓，FCS 会停止自动流程并保留当前候选状态，等待玩家补资源后继续。

如果药包不够：

- **可用药包为 0 且没钱购买**：停止自动扫荡/自动开炮；如果炮弹已经入膛，炮位进入 `ResourceBlocked`，不让普通超时把状态打乱。
- **可用药包为 1 包或以上**：尝试寻找同弹种且所需药包数不超过库存的目标，重新计算诸元，把当前这发尽快打出去。
- **成弹状态不同步**：如果炮膛和装药读数都满足要求，但游戏迟迟没有变成可击发，FCS 会停放当前炮位并释放方向机；后续状态恢复为可击发时会自动接回。
- **补充资源后**：按 `Numpad 0` / `Ctrl+0` 重新开启扫荡，会清除 halt 状态并恢复资源阻塞炮位。

## 敌人阵营识别

雷达通过 `MapEntity.Icon` 和 `MapEntity.Role` 枚举位掩码做阵营判断：

| 阵营 | Role 标志位 | 行为 | 典型实体 |
| --- | --- | --- | --- |
| **Hostile** | Enemy (1) | 打击 | hostilebunker, hostileinfatry, hostiletank |
| **Target** | Target (32) + Enemy (1) | 打击 | target1-3 (FDC) |
| **Artillery** | Artillery (128) | 优先打击 | hostileartillery1-4 |
| **FDC** | Target + icon="Fire Direction" | 优先打击 | target1-3 |
| **Armored** | Fortification (65536) / Tank (262144) | 次级优先 | hostilebunker, hostiletank |
| **Ally** | Ally (2) | 不打 | allyhospital, allyinfantry, allytank, spotter |
| **Reference** | Reference (33554432) | 不打 | alpha, RefA, RefB |
| **Civilian/Neutral** | 名字含 police/prop/civ/smoke/hospital | 不打 | police, prop, civ, hospital |

> 数据来源：[Iron Nest DB — Enemies](https://ironnestdb.com/enemies)

## 架构

工程拆分为四个程序集，核心是为**热重载**服务的宿主 / 逻辑分离设计：

| 项目 | 角色 | 说明 |
| --- | --- | --- |
| `IronNestFCS` | 宿主 Mod | 稳定加载、永不重载。负责首次加载 Logic、监听 F9 触发热重载、监控制台修改、转发生命周期回调。 |
| `IronNestFCS.Abstractions` | 契约 | 仅含 `IFcsModule` 接口。只加载一份，是唯一能安全跨 `AssemblyLoadContext` 边界传递的类型。 |
| `IronNestFCS.Logic` | 火控逻辑 | 弹道解算、任务调度、炮塔/炮管操控、UI、雷达扫描。被装进可回收的 ALC，按 F9 卸载并重载。 |
| `IronNestFCS.CustomRecords` | 独立 Mod | 与火控无关，扫描自定义音频文件并替换游戏内唱片机的音轨与贴图。 |

### Logic 内部结构

```text
IronNestFCS.Logic/
├── FcsModule.cs          # 程序集入口，组装 FSC + FcsWindow + TacticalRadar
├── FSC.cs                # 火控核心：候选池调度、协程编排、方向机预约、资源阻塞
├── FcsWindow.cs          # IMGUI 状态面板
├── TacticalRadar.cs      # 战术雷达：敌我识别、存活判定、双模标点
├── TargetPriority.cs     # 目标价值与星级判断
├── FcsSceneInteractor.cs # 场景交互、点击注册、键盘快捷键、暂停/失焦保护
├── ClickRaycaster.cs     # 自定义射线点击检测
└── FCS/
    ├── ArtilleryTask.cs       # 任务数据类
    ├── BallisticCalculator.cs # 弹道计算器硬件抽象
    ├── GunSystem.cs           # 单管炮抽象：弹仓、装填、装药、高低机
    ├── Turret.cs              # 炮塔方向机抽象
    ├── TriggerConsole.cs      # 击发确认台抽象
    ├── PurchaseDeck.cs        # 采购台抽象
    ├── MapTable.cs            # 地图桌抽象
    └── CoroutineLock.cs       # 协程互斥锁
```

### 热重载关键设计

Logic 程序集从内存字节加载，不锁住磁盘 dll，并装进 `isCollectible` 的 `AssemblyLoadContext`。重载时先 `Shutdown`，停止协程、撤销 Harmony 补丁、清空 IL2CPP 引用，再卸载旧 ALC 并加载新版本。

> 注意：按 F9 后 `autoSweep` 会重置为关，需再按 `Numpad 0` 开启。

关键约束：

- 不要在 Logic 中注册新的 IL2CPP 类型。
- 所有对 IL2CPP 对象的引用在 `Shutdown` 时清空。
- 每次实例用独立 Harmony 实例；`Shutdown` 时 `UnpatchSelf`。
- 协程必须登记到 `_runningCoroutines`，卸载时统一停止。

## 构建与安装

### 前置条件

- 已安装 **.NET 6 SDK**（见 [global.json](global.json)）。
- 游戏本体，并已为其安装 **MelonLoader**（IL2CPP）。

### 配置游戏路径

本项目只需要配置一次游戏目录，不用分别修改每个 `.csproj`。

打开根目录的 [Directory.Build.props](Directory.Build.props)，找到这一行：

```xml

<GameDir>D:\Steam\steamapps\common\Iron Nest Heavy Turret Simulator</GameDir>
```

如果你的游戏不在 D 盘，把中间的路径改成你自己的游戏安装目录即可。不要加到 `Mods`、`UserData` 或 `UserLibs`，只填游戏根目录：

```xml

<GameDir>E:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator</GameDir>
```

不知道路径在哪里时，可以在 Steam 里右键游戏：

```text
管理 -> 浏览本地文件
```

打开的文件夹就是 `GameDir`。复制资源管理器地址栏里的路径，粘到 `Directory.Build.props` 里。

构建前请确认该目录真实存在，并且已经给游戏安装过 MelonLoader。项目会从 `$(GameDir)\MelonLoader\` 下读取
MelonLoader、Il2CppInterop、Unity 和游戏程序集引用；如果路径写错或还没安装
MelonLoader，构建时会出现 `MelonLoader`、`UnityEngine`、`Assembly-CSharp` 找不到的错误。

> 普通使用者只需要改 `Directory.Build.props`。不要手动修改 `IronNestFCS.csproj`、`IronNestFCS.Logic.csproj`、`IronNestFCS.CustomRecords.csproj` 或 `IronNestFCS.Abstractions.csproj` 里的输出路径。

### 构建

```bash
dotnet build IronNestFCS.sln -c Release
```

如果不想修改文件，也可以在构建时临时指定游戏路径：

```bash
dotnet build IronNestFCS.sln -c Release -p:GameDir="D:\Steam\steamapps\common\Iron Nest Heavy Turret Simulator"
```

各程序集的输出位置：

- **宿主 Mod**（`IronNestFCS.dll`）：输出到游戏的 `Mods/` 目录。
- **火控逻辑**（`IronNestFCS.Logic.dll`）：输出到 `UserData/IronNestFCS/`，由宿主在运行时反射加载。
- **契约**（`IronNestFCS.Abstractions.dll`）：输出到 `UserLibs/`。
- **自定义唱片机**（`IronNestFCS.CustomRecords.dll`）：输出到 `Mods/`。

构建成功后不需要手动复制 dll。项目会自动把文件放到 `Directory.Build.props` 里配置的游戏目录下。改完代码后重新构建，切回游戏按 F9 即可加载新逻辑。

### 第三方依赖

| 依赖 | 用途 | 说明 |
| --- | --- | --- |
| [CSCore](https://github.com/filoe/cscore) | 音频解码 (mp3/wav/flac) | 仅 CustomRecords 使用，纯托管库 |
| [TagLibSharp](https://github.com/mono/taglib-sharp) | 音频标签/封面读取 | 仅 CustomRecords 使用，纯托管库 |

## 使用

1. 启动已安装 MelonLoader 与本 Mod 的游戏。
2. 进入包含炮塔与地图桌的关卡场景。若火控面板提示 `Waiting for scene...`，按 **F9** 在当前场景重新绑定。
3. 在控制台旁选择弹种（默认 AP），按需开启 `Auto Fire` 和 `Max Charge`。
4. 按 **Numpad 5** 切换标点模式：`Auto` 由雷达自动标记，`Manual` 保留玩家手动拖动。手动模式下，点击 T1-T4 或按 `Numpad 1-4`
   加入的任务会优先于普通扫荡候选；切回 `Auto` 时会立刻重新扫描并校正候选池里的移动目标位置。
5. 按 **Numpad 0** 开启持续扫荡，开启时会先强制扫描一次，面板显示 `[Sweep ON]`。
6. 也可以点击地图右侧目标按钮（T1-T4）或按 `Numpad 1-4` 下达手动打击任务。
7. 左上角面板会显示两管炮进度、候选池数量、真实击发预览和资源 halt 原因。
8. 炮管卡住时按 `Numpad 7/8/9` 强制重置对应炮管。

### 开发热重载

修改 `IronNestFCS.Logic` 内的代码后，重新构建该项目，切回游戏按 **F9** 即可加载新逻辑，无需重启游戏。

> 注意：F9 热重载后需要重新按 `Numpad 0` 开启扫荡。

## 常见问题

| 问题                            | 原因                    | 解决                                                               |
|-------------------------------|-----------------------|------------------------------------------------------------------|
| 构建报 `MSB3270: MSIL/AMD64 不匹配` | 缺少 x64 配置             | 在 csproj 添加 `<PlatformTarget>x64</PlatformTarget>`               |
| 装药/装弹卡住                       | 协程时序或按钮状态异常           | 按 `Numpad 7/8/9` 重置对应炮管                                          |
| 笔记本按键无反应                      | 无小键盘                  | 用 `Ctrl+数字键` 替代                                                  |
| F9 后不扫荡                       | 热重载会重新初始化 Logic       | 再按 `Numpad 0` 开启扫荡                                               |
| 没钱后队列不继续                      | FCS 进入资源 halt 状态      | 补充资源后按 `Numpad 0` 恢复                                             |
| 目标刚被打死后仍准备下一发                 | 旧版本会在长时间采购/诸元计算后才核实存活 | 1.2.2 会在采购、补药、装填、击发前再次核实当前实体，已死亡则停止当前任务                          |
| 冷门弹种无法自动购买                    | 当前关卡可能没有对应购买卡         | 1.2.2 会动态识别采购台实际存在的 `*Shell` 卡；日志会提示可购买的常用/冷门弹种                  |
| 铁巢移动后角度不对                     | 旧候选任务仍保存移动前的角度/距离     | 按 `Numpad 5` 切回 Auto 或按 `Numpad 0` 开启扫荡会强制扫描并刷新候选池；正在执行的左右炮任务不会改 |
| 目标列表和击发目标不完全一致                | 已装弹炮位会优先找兼容目标         | 以左上角 `Queue` 的真实预览为准                                             |

## 贡献

欢迎提交 Issue 和 Pull Request。

- 发现 Bug、有功能建议或疑问，请[提交 Issue](../../issues)。
- 改进代码请[提交 Pull Request](../../pulls)。改动火控逻辑时请留意 `FSC.cs` 中关于热重载与协程的约定。

## 许可

本项目基于 MIT 许可证开源。详见 [LICENSE](LICENSE)。

## 免责声明

本项目为非官方的第三方 Mod，与游戏开发商无关。仅供学习与单机娱乐使用，使用风险自负。
