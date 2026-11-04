# Stickman-Rope-Hero-3.4.186-LAN-Multiplayer-Mod-Hybrid-v2.6.0-
# Stickman Rope Hero 3.4.186 · LAN Multiplayer Mod (Hybrid v2.6.0)
> 火柴人绳索英雄 局域网联机混血版 v2.6.0

No longer depends on BepInEx. The multiplayer logic lives in a standalone assembly SRHCoop.dll. Drop it directly into game APK path assets/bin/Data/Managed/ and bootstrap via minimal IL edits.
There are **no native injections, no DEX patches, no BepInEx loading chain** at runtime.

This rewrite changes networking model from peer‑local resolution to **host‑authoritative**, plus brand‑new in‑game overlay UI.

> ⚠️ v2.3.0 upgraded network protocol to v4, **incompatible with older versions**. Both devices must use identical modded APK.

---

## 1. Installation & Usage
1. Uninstall original game (different signature).
2. Install StickmanRopeHero_3.4.186_Coop.apk.
3. Connect both devices to **same Wi‑Fi LAN**.
4. After entering game world, multiplayer will attempt to connect automatically. Top‑left overlay shows status: Host or Client.

> This build skips privacy policy screen (SkipPrivacyScreen defaults to true).
> Some emulators cannot click the ACCEPT button on original privacy page. Skipping writes equivalent preferences and jumps straight to main menu.

## 2. In‑Game Overlay Panel
Fully self‑drawn rounded UI (runtime generated textures, no external assets): title bar with accent underline, player badge (Host‑blue / Client‑green), color‑coded buttons, player cards with health bars.

**Floating Button**: When panel is collapsed, a draggable mascot button stays on screen (~17% of screen short edge). Small status dot at bottom‑right: green = connected. Tap to restore panel.
Panel itself can also be dragged by holding title bar.
Both floating‑button and panel positions persist in config (FloatX/FloatY/PanelX/PanelY, normalized coordinates).

A small mascot renders below original Naxeex splash logo on boot (black background, fades out after 3 seconds).

| Button | Function |
|---|---|
| Start Multiplayer / Stop Multiplayer | Manual toggle (blue primary button) |
| Healthbar: Show / Hide | Toggle overhead name & health for remote players |
| Test Bot | Single‑player self‑test: spawn local dummy peer |
| Test Hit | Single‑player self‑test: run full host hit‑resolution pipeline |
| Loopback Multiplayer Test | Validate complete network stack locally |
| Scan | Scan /24 subnet (254 addresses). Use when router blocks UDP broadcast. Responder replies via unicast to establish connection. |
| Target IP + Direct Connect | Manually input peer IP for direct connection |
| × | Collapse panel (restore via top‑left "Multiplayer" hint) |

## 3. Host‑Authoritative Synchronization (Core Rework)
Old design: every peer broadcasts events and resolves hits locally. Network jitter frequently causes "I hit you but no damage".

New workflow:
- Each player remains authoritative for broadcasting own position / rotation / animation / health (15 Hz).
- Attacks only send **attack intent** to host: origin, direction, range, damage value.
- **Only host resolves hit detection**: clamps damage & range, enforces attacker 0.18 s cooldown. Performs distance & cone check (melee 70°, ranged 25°).
- Host broadcasts final damage verdict. Victim locally executes OnHit. Other peers only render predicted health bar changes.
- Host is automatically selected as peer with **smallest peer‑id**. Host migration happens automatically when host disconnects, no manual negotiation.
- Every damage verdict carries judge‑id. Receivers only accept verdicts from current smallest‑id host, prevents duplicate / wrong damage from desynchronized peers.

### Connection Fallback Logic (v2.2)
Three‑layer connection attempt order:
1. UDP broadcast
2. /24 subnet scan (unicast, higher success against broadcast‑blocking routers)
3. Manual IP direct connect

### Animation Sync Improvements (v2.3, critical fix)
v2.2 only synced Animator state hash. This game uses blend‑tree locomotion: walk / run / idle stay inside same animator state and mix via parameters. Remote players appeared frozen for movement.

v2.3 syncs **18 animation parameters** at 15 Hz (+~40 bytes per packet):

| Type | Parameters |
|---|---|
| float | Forward Turn StrafeDir Jump MeleeWeaponAttackSpeed |
| bool | Sprint Strafe Crouch OnGround IsMoving IsFlying Gliding NearWall IsOnWater DoMelee RangedWeaponShoot RangedWeaponRecharge |
| int | Melee MeleeWeaponType RangedWeaponType |

Remote side applies values with SetFloat/SetBool/SetInteger; float values use interpolation damping. Walk, run, crouch, jump, aim, reload are correctly replicated. Death triggers extra Die trigger.

### Weapon Sync (v2.3)
Network state packet includes active weapon GameObject name e.g. Fists, Pistol. Remote clone activates matching child weapon and de‑activates previous one. Equip / switch weapons are visible across peers.

Additional features: RTT display, 5‑second timeout cleanup for stale peers, death‑state sync (overhead "DEAD" label), configurable fire rate. Outbound broadcast IP obtained via UDP socket probing, no DNS lookup to avoid Android main‑thread blocking.

### NPC Synchronization (v2.5)
Original NPCs spawn locally with random seed on each device. Deterministic simulation is impractical. Implemented **union proxy replication**:
- Every client broadcasts up to 10 nearby BaseStatusNPC within 55 m every 100 ms: position, rotation, health, animator parameters.
- Received foreign NPC data spawn lightweight local proxy clone. Proxy uses cached template from first local encounter of that NPC type. Proxy strips all game logic components, retains only render model + Animator.
- Proxy auto‑destroyed after 1.2 s without state update.

Result: Both players observe union set of NPCs from each other’s world.

#### NPC Damage Relay (v2.5)
Proxy objects have no game logic but remember originating peer.
1. When you hit proxy locally: collect proxies inside attack cone, send NpcDamage(npcId, damage, attacker, hitPoint) to proxy’s owner peer.
2. Owner peer locates real in‑world NPC by instanceID and executes OnHit.
3. Health change / death propagates back via standard NPC state sync; proxy disappears afterwards.

You and teammate can damage same NPC together. Sample log snippet:
[NPC] relay test, real npc hp before=200
npc damage relayed: Soldier_Stickman NPC(Clone) 200 -> 188

Current NPC sync limitations:
- Union replication roughly doubles NPC population density vs single‑player.
- Only humanoid NPC synced; vehicles not supported.
- Proxy template requires local prior encounter of NPC type, else skip replication (log entry: npc type not seen locally).
- Proxies have no colliders: cannot be pushed, proxies will not attack you (AI runs only on origin peer).

## 4. Configuration File
File path:
/sdcard/Android/data/com.mgc.stickman.rope.hero/files/SRHCoop.cfg

| Key | Default | Description |
|---|---|---|
| Port | 27811 | UDP listen port |
| AutoStart | true | Auto‑start LAN multiplayer after entering world |
| MeleeDamage / RangedDamage | 18 / 12 | Damage value applied against remote players |
| MeleeRange / RangedRange | 3.2 / 60 | Hit detection range thresholds |
| SendRateHz | 15 | State broadcast update frequency |
| UiScale | 1.2 | Multiplayer overlay UI scaling factor |
| SkipPrivacyScreen | true | Bypass original privacy agreement screen |
| BootSplashSeconds | 3.0 | Mascot boot overlay duration (0.5‑12 seconds) |
| ConnectIp | empty | Pre‑set target IP for direct‑connect (written automatically via UI button) |
| SyncNpcs | true | Toggle NPC proxy synchronization (also available in‑panel) |

Log file: same directory SRHCoop.log. Alternatively view logs via adb logcat -s Unity.

## 5. Implementation: Hybrid IL Injection
Modified APK structure:
Original APK
 ├─ assets/bin/Data/Managed/SRHCoop.dll      ← Added, loaded automatically by Unity alongside other assemblies
 └─ assets/bin/Data/Managed/Assembly‑CSharp.dll  ← Minimal IL edits: insert one call inside two Awake methods
        PrivacyPolicyManager.Awake  → CoopBootstrap.Init()
        GameManager.Awake           → CoopBootstrap.Init()

CoopBootstrap.Init() runs exactly once:
1. Create new GameObject "SRHCoop"
2. DontDestroyOnLoad(gameObject)
3. Attach component CoopBehaviour

IL patching implemented with Mono.Cecil (tools/IlPatch). Only two call instructions inserted; stack balanced, original game logic untouched.

> Why not [RuntimeInitializeOnLoadMethod]?
> Unity pre‑compiles that callback list during player build time. Assemblies added post‑build inside Managed folder will not be scanned for those attributes, confirmed by runtime logging. Hence IL bootstrap approach.

Comparison vs old BepInEx mod: removed libbe5hook.so GOT hook, BepInEx core assemblies, DEX patch, in‑memory assembly loader, mini chainloader. Bootstrap steps reduced from 6 → 2.

## 6. Test Status
Test environment: MuMu Player 12 (Android 12, ARM binary translation).

| Item | Status |
|---|---|
| Hybrid IL bootstrap | ✅ bootstrap ok (hybrid, no BepInEx) |
| Chinese font / overlay panel render | ✅ Auto‑fallback Noto Sans CJK SC |
| Auto‑connect on world load | ✅ LAN started on port 27811, Host status |
| Remote player clone + overhead healthbar | ✅ Model render, name, health display |
| Host‑authoritative hit & damage dispatch | ✅ hit by zzzz0e8c for 10 (hp 300→290) |
| Local loopback multiplayer test | ✅ State sync, dummy peer spawn, attack intent, host resolve, hit reaction, stale timeout cleanup |
| Boot mascot splash overlay | ✅ Renders below Naxeex logo, fades after 3s |
| Rounded overlay panel & player cards | ✅ See screenshot |
| Physical two‑android‑device multiplayer | ❌ Only one emulator instance available in test environment, unverified |

## 7. Rebuild Instructions
PowerShell build script:
.\build_hybrid.ps1 -IlPatch -Apk "F:\QQdownland\Stickman Rope Hero_3.4.186.apk"

Output artifact: dist\StickmanRopeHero_3.4.186_Coop.apk.
Omit -IlPatch flag: only inject SRHCoop.dll without IL patch; multiplayer will not bootstrap (for comparison debugging).

## 8. Known Limitations
- Positions are peer‑authoritative, no input prediction / rollback. Remote actors exhibit interpolation lag under high‑latency network.
- Only player character replicated. Vehicles, rope grabbing, quests, in‑vehicle state are **not synced**.
- Animation sync uses state hash + normalizedTime; minor pop may occur during state transition moments.
- APK keeps original game ABIs (armeabi‑v7a + x86). Original game does **not ship arm64 native libraries**, so pure‑arm64 devices cannot install this APK — this is original game constraint, not mod issue. Modern x86 emulators run fine.
- Devices must reside on same LAN subnet. Router AP‑Isolation must be disabled. Use subnet‑scan or manual direct‑connect when broadcast UDP is filtered.
- Protocol v4 incompatible with older v3 builds; both peers must run identical mod APK.

## License & Notice
> This project is a community modification for personal study.
> Not affiliated with Naxeex. Do not distribute for commercial purposes.
>
> # 火柴人绳索英雄 3.4.186 · 局域网联机（混血版 v2.6.0）
本模组不再依赖 BepInEx。联机代码作为独立程序集 SRHCoop.dll，直接放入游戏 APK 的 assets/bin/Data/Managed/ 目录，通过少量 IL 修改完成启动引导。
运行过程**无原生注入、无DEX补丁、不使用BepInEx加载链路**。

本版本将网络模型从各端各自结算升级为**主机权威模式**，同时重做了游戏内悬浮UI面板。

> ⚠️ v2.3.0 将网络协议升级至 v4，**与旧版本无法互通**，两台设备必须安装完全相同的修改版APK。

---

## 1. 安装与使用
1. 卸载原版游戏（签名不一致）。
2. 安装 StickmanRopeHero_3.4.186_Coop.apk。
3. 两台设备连接**同一个局域网Wi‑Fi**。
4. 进入游戏世界后会自动尝试联机，左上角面板会显示状态：`主机` 或 `客户端`。

> 该构建会跳过隐私政策页面（配置项 SkipPrivacyScreen 默认开启）。
> 部分模拟器中原版游戏的同意按钮会出现无法点击的问题；跳过页面会写入同等配置，直接进入主菜单。

## 2. 游戏内悬浮面板
全部UI为代码绘制的圆角界面（运行时生成贴图，不需要额外素材）：带强调色下划线的标题栏、玩家标识（主机蓝色 / 客户端绿色）、分色按钮、附带血条的玩家卡片。

**悬浮球按钮**：收起面板后，屏幕会保留一个可拖动的吉祥物按钮（尺寸约为屏幕短边的17%）。右下角状态小圆点：绿色代表已联机。点击即可重新展开面板。
面板本身也可以按住标题栏进行拖动。
悬浮球与面板的位置会保存到配置文件（FloatX/FloatY/PanelX/PanelY，归一化坐标），下次启动保持原有位置。

开机时会在Naxeex商标下方显示小型吉祥物（黑色背景，3秒后淡出）。

| 按钮 | 功能说明 |
|---|---|
| 开始联机 / 关闭联机 | 手动开关联机（蓝色为主按钮） |
| 血条：显示/隐藏 | 切换其他玩家头顶名字与血条显示 |
| 测试机器人 | 单人自测：生成本地虚拟玩家 |
| 测试受击 | 单人自测：完整运行主机受击判定流程 |
| 回环联机测试 | 在本机验证整套网络收发链路 |
| 扫描 | 扫描/24网段共254个IP。路由器屏蔽UDP广播时使用，对方通过单播回复建立连接。 |
| 对方IP + 直连 | 手动填写对方IP进行直接连接 |
| × | 收起面板，可通过左上角提示重新打开 |

## 3. 主机权威同步（核心改动）
旧版逻辑：每个设备广播事件，本地自行判断命中。网络稍有波动就会出现“我打中，但对方不掉血”的情况。

新版工作流程：
- 每个玩家只负责广播自己的位置、朝向、动画、血量（15Hz频率）。
- 攻击仅向主机发送**攻击意图**：起点、方向、攻击范围、伤害数值。
- **只有主机执行命中判定**：限制伤害与射程，给攻击者增加0.18秒攻击冷却，计算距离与攻击锥角（近战70°，远程25°）。
- 主机广播最终伤害判决。受击方本地执行受击逻辑，其他客户端仅更新血条做视觉展示。
- 主机自动选择为 peer‑id 数值最小的客户端；主机掉线会自动迁移，无需手动操作。
- 每一条伤害判决附带判决者ID，接收端只接受当前最小ID主机发出的判决，避免两边各自判定造成重复伤害、错误掉血。

### 连接降级策略（v2.2）
三层连接尝试顺序：
1. UDP广播
2. /24网段单播扫描（路由器拦截广播时成功率更高）
3. 手动填写IP直连

### 动画同步改进（v2.3，重要修复）
v2.2版本仅同步动画器状态哈希。但本游戏移动由混合树驱动：走路、跑步、待机处于同一个动画状态，依靠参数混合实现，导致远端角色行走动作完全不动。

v2.3同步**18项动画参数**，更新频率15Hz，每个数据包增加约40字节数据：

| 类型 | 参数列表 |
|---|---|
| 浮点数 | Forward、Turn、StrafeDir、Jump、MeleeWeaponAttackSpeed |
| 布尔值 | Sprint、Strafe、Crouch、OnGround、IsMoving、IsFlying、Gliding、NearWall、IsOnWater、DoMelee、RangedWeaponShoot、RangedWeaponRecharge |
| 整数 | Melee、MeleeWeaponType、RangedWeaponType |

接收端调用 SetFloat/SetBool/SetInteger 应用参数；浮点参数做插值平滑。走路、奔跑、蹲伏、跳跃、瞄准、换弹均可正确同步。角色死亡会额外触发死亡动画触发器。

### 武器同步（v2.3）
状态数据包携带当前武器物体名称，例如拳头、手枪。远端玩家克隆体会激活对应子物体武器，关闭上一把武器；掏枪、切换武器可以跨设备正常显示。

附加功能：RTT网络延迟显示、5秒超时清理离线玩家、死亡状态同步（头顶显示“阵亡”）、发射速率可配置。广播出口IP通过UDP套接字探测获取，不使用DNS，避免安卓主线程卡顿。

### NPC同步（v2.5）
原版每个设备本地随机生成路人与敌人，两边NPC完全对不齐，无法做确定性模拟。本模组采用**并集代理复制方案**：
- 每个客户端每100ms广播自身55米范围内最多10个BaseStatusNPC的数据：位置、朝向、血量、动画参数。
- 收到其他设备的NPC数据，本地生成轻量化代理克隆体。代理使用第一次见到该NPC时缓存的模板，移除全部游戏逻辑组件，仅保留模型与动画器。
- 超过1.2秒没有收到更新就销毁该代理。

效果：双方可以看到彼此世界中NPC的合集。

#### NPC伤害中继（v2.5）
代理本身没有AI逻辑，但记录它属于哪一位玩家。
1. 当你击中本地代理：筛选攻击范围内的代理，向代理所属玩家发送NpcDamage数据包（NPC编号、伤害、攻击者、命中坐标）。
2. 所属玩家在自己的游戏世界内，通过实例ID找到真实NPC，执行受击。
3. 血量变化、死亡状态通过NPC状态包同步回其他客户端，代理随后消失。

多人可以共同攻击同一个敌人。日志示例：
[NPC] relay test, real npc hp before=200
npc damage relayed: Soldier_Stickman NPC(Clone) 200 -> 188

NPC同步现有局限：
- 并集模式会使NPC总数量大约为单机两倍。
- 仅同步人形NPC，载具不支持同步。
- 代理模板需要本机曾经见过该种NPC，否则不会生成代理（日志输出：npc type not seen locally）。
- 代理没有碰撞体，无法被推动，代理不会攻击玩家，AI只运行在原始生成的设备上。

## 4. 配置文件
文件路径：
/sdcard/Android/data/com.mgc.stickman.rope.hero/files/SRHCoop.cfg

| 配置键 | 默认值 | 说明 |
|---|---|---|
| Port | 27811 | UDP监听端口 |
| AutoStart | true | 进入游戏世界后自动开启局域网联机 |
| MeleeDamage / RangedDamage | 18 / 12 | 对其他玩家造成的近战、远程伤害 |
| MeleeRange / RangedRange | 3.2 / 60 | 命中判定距离 |
| SendRateHz | 15 | 状态包发送频率 |
| UiScale | 1.2 | 联机面板UI缩放 |
| SkipPrivacyScreen | true | 跳过隐私协议弹窗 |
| BootSplashSeconds | 3.0 | 开机吉祥物显示时长，范围0.5‑12秒 |
| ConnectIp | 空 | 预填写直连目标IP，面板点直连会自动写入此项 |
| SyncNpcs | true | 开启NPC代理同步，面板也提供开关 |

日志保存在同目录 SRHCoop.log，也可以使用 adb logcat -s Unity 查看运行日志。

## 5. 实现原理：混血IL注入
修改后的APK文件结构：
原版APK
 ├─ assets/bin/Data/Managed/SRHCoop.dll      ← 新增，Unity会和其他脚本程序集一同加载
 └─ assets/bin/Data/Managed/Assembly‑CSharp.dll  ← 少量IL修改，在两个Awake函数开头各插入一条调用
        PrivacyPolicyManager.Awake  → CoopBootstrap.Init()
        GameManager.Awake           → CoopBootstrap.Init()

CoopBootstrap.Init() 只会执行一次：
1. 创建游戏物体 SRHCoop
2. DontDestroyOnLoad(gameObject) 防止切换场景被销毁
3. 挂载 CoopBehaviour 组件

IL补丁使用 Mono.Cecil 工具实现（tools/IlPatch），仅插入两条调用指令，栈平衡，完全不改动原有游戏逻辑。

> 为什么不用 [RuntimeInitializeOnLoadMethod]？
> Unity在打包播放器阶段就预编译该回调列表。打包完成后再塞入Managed文件夹的程序集，不会被扫描到该特性，实际运行测试已经证实。因此选择IL引导启动。

对比旧BepInEx版本：移除了libbe5hook.so钩子、BepInEx核心库、DEX补丁、内存加载程序集、微型加载链。启动流程由6步缩减为2步。

## 6. 测试情况
测试环境：MuMu模拟器12（Android12，ARM二进制转译）

| 测试项目 | 结果 |
|---|---|
| 混血IL引导启动 | ✅ bootstrap ok (hybrid, no BepInEx) |
| 中文字体与面板渲染 | ✅ 自动加载 Noto Sans CJK SC |
| 进入世界自动联机 | ✅ LAN started on port 27811，显示为主机 |
| 远端玩家模型+头顶血条 | ✅ 模型、名字、血条显示正常 |
| 主机权威判定与伤害下发 | ✅ hit by zzzz0e8c for 10 (hp 300→290) |
| 本机回环联机完整测试 | ✅ 状态同步、虚拟玩家生成、攻击意图、主机判定、受击反馈、超时清理全部正常 |
| 开机吉祥物画面 | ✅ 在NaxeexLogo下方渲染，3秒淡出 |
| 圆角面板、玩家卡片 | ✅ 功能正常 |
| 两台真实安卓设备联机 | ❌ 当前环境仅有一个模拟器实例，尚未验证 |

## 7. 重新构建方法
PowerShell构建脚本：
.\build_hybrid.ps1 -IlPatch -Apk "F:\QQdownland\Stickman Rope Hero_3.4.186.apk"

编译输出产物：dist\StickmanRopeHero_3.4.186_Coop.apk。
去掉 -IlPatch 参数：仅把dll塞进APK，不做IL补丁，联机模组不会启动，用于调试对比。

## 8. 已知限制
- 角色位置由各端自己上报，没有做输入预测、回滚机制。网络延迟高的时候，远端角色会出现插值滞后。
- 只同步玩家本体。载具、绳索抓取、任务、载具内部状态均不做同步。
- 动画同步依靠状态哈希+归一化时间；状态切换瞬间会有轻微画面跳变。
- APK保留原版游戏全部ABI（armeabi‑v7a + x86）。原版游戏本身没有arm64原生库，纯64位安卓设备无法安装APK，这是原游戏限制，不是模组问题；现代x86模拟器可以正常运行。
- 设备需要处于同一局域网，路由器需要关闭AP隔离。广播被拦截时，请使用扫描功能或者手动IP直连。
- v4协议和旧v3协议不兼容，两台设备必须使用完全一样的修改包。

## 许可与声明
> 本项目是社区修改模组，仅用于个人学习研究。
> 和Naxeex公司无任何关联，禁止用于商业分发。

## ⚠
> 感谢deepseek为我开发
