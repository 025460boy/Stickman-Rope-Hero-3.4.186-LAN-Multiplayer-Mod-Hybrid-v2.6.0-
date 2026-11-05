# 火柴人绳索英雄 3.4.186 · 局域网联机（混血版 v2.6.0）

不再依赖 BepInEx：联机代码是一个独立程序集 `SRHCoop.dll`，直接放进游戏 APK 的
`assets/bin/Data/Managed/`，靠一处 IL 引导调用启动。运行时没有任何 native 注入、
没有 DEX 补丁、没有 BepInEx 加载链。

同时把联机模型从「各自结算」升级为**主机权威**，并重做了界面。

![开机画面](docs/screenshot-boot-mascot.png) ![联机面板](docs/screenshot-panel-v2.png)

> **v2.3.0 协议升级到 v4，与旧版本不互通**：两台设备必须装同一个包。

---

## 1. 安装与使用

1. 卸载原版（签名不同）。
2. 安装 `StickmanRopeHero_3.4.186_Coop.apk`。
3. 两台设备连同一个 Wi-Fi。
4. 进游戏世界后自动联机，左上角面板显示 `状态：主机` 或 `状态：客户端`。

> 本构建会**跳过隐私政策页**（配置项 `SkipPrivacyScreen`，默认 true）。
> 原因：该页的 ACCEPT 按钮在部分模拟器环境里点不动（原版 APK 同样如此），
> 跳过它会自动写入同样的偏好并进入 Menu。

## 2. 面板

界面是自绘的圆角面板（程序生成圆角贴图，不依赖任何素材）：标题栏 + 强调色下划线、
角色徽标（主机蓝 / 客户端绿）、主次按钮分色、玩家卡片带血条。

**悬浮球**：收起面板后，屏幕上是一个小吉祥物按钮（约占屏幕短边 17%），右下角有个
状态点（绿=已联机）。**可以拖到任意位置**，点一下展开面板。面板本身也能**按住标题栏拖动**。
两个位置都会记进配置（`FloatX/FloatY/PanelX/PanelY`，归一化坐标），下次启动保持。

开机时会在 **Naxeex 商标下方**显示一个小吉祥物（黑底，3 秒后淡出）——见上方第一张图。

![悬浮球](docs/screenshot-float-button.png)

| 按钮 | 作用 |
| --- | --- |
| `开始联机` / `关闭联机` | 手动开关（蓝底为主按钮） |
| `血条：显示/隐藏` | 远程玩家头顶血条与名字 |
| `测试机器人` | 单人自测：本地假角色 |
| `测试受击` | 单人自测：走一遍主机判定 |
| `回环联机测试` | 单机验证真实网络链路 |
| `扫描` | **扫本机 /24 网段**（254 个地址），对方单播回应即可建连，广播被路由器拦截时用它 |
| `对方 IP` + `直连` | 手动指定对方 IP 直连 |
| `×` | 收起面板（收起后左上角「联机」展开） |

## 3. 主机权威同步（相对旧版的核心改进）

旧版是「人人广播、各自判定」：我打你，我这边算出命中就发事件，你那边再自己判断，
网络一抖就出现「我打中了但你没掉血」。

现在：

* 每个玩家仍然只权威地广播自己的位置 / 朝向 / 动画 / 血量（15 Hz）；
* **攻击只发「意图」**给主机（起点、方向、范围、伤害）；
* **只有主机判定是否命中**：钳制伤害与射程、按攻击者做 0.18s 冷却、用自己持有的
  位置做距离 + 锥角计算（近战 70°、远程 25°）；
* 主机广播**唯一一份伤害判决**，受害者本地执行 `OnHit`，其他人只做血条预估；
* 主机角色由 **peer id 最小者**自动担任，主机掉线后自动迁移，无需协商；
* 每条伤害判决都带**判决者 id**，接收方只认「当前最小 id 的主机」发出的判决，
  避免两边互相看不见时各自判一次造成的双份/错判。

**连接兜底**（v2.2）：广播 → `/24` 扫描 → 手动 IP 直连，三级递进。
不少路由器会过滤 `255.255.255.255`，扫描是纯单播，成功率更高。

### 动作同步（v2.3，重要修正）

v2.2 只同步了 Animator 的 **state hash**，但这款游戏的移动是 **blend tree** 驱动的：
走路/跑步/站立都停在同一个状态里，靠参数混合。所以之前只能看到「挥拳」这种会切状态的
动作，走路完全不动。

v2.3 改成同步 **18 个动画参数**（15 Hz，每包约 +40 字节）：

| 类型 | 参数 |
| --- | --- |
| float | `Forward` `Turn` `StrafeDir` `Jump` `MeleeWeaponAttackSpeed` |
| bool | `Sprint` `Strafe` `Crouch` `OnGround` `IsMoving` `IsFlying` `Gliding` `NearWall` `IsOnWater` `DoMelee` `RangedWeaponShoot` `RangedWeaponRecharge` |
| int | `Melee` `MeleeWeaponType` `RangedWeaponType` |

远端接收后每帧 `SetFloat/SetBool/SetInteger`（float 带阻尼），走路、跑步、蹲、跳、
瞄准、换弹都会跟本地一致；死亡额外补一次 `Die` trigger。

### 武器同步（v2.3）

状态包带上当前武器 GameObject 名（如 `Fists`、`Pistol`）。远端在克隆体里按名字
找到对应武器子物体并激活、关掉上一把 —— 对方掏枪 / 换枪你这边会同步显示。

![走路同步](docs/screenshot-walk-sync.png)

附带：RTT 显示、5 秒超时清理、死亡状态同步（头顶显示「阵亡」）、
发射率可配、广播地址用「连接 UDP 探测出口 IP」获取（不做 DNS，避免 Android 上卡主线程）。

### NPC 同步（v2.5）

游戏的路人 / 敌人是**各端本地随机生成**的，两个设备永远对不上。做不了确定性模拟，
所以采用**并集复制**：

* 每个客户端每 100ms 把**自己 55 米内最多 10 个** `BaseStatusNPC`（位置、朝向、血量、动画参数）广播出去；
* 收到别人发来的 NPC，就在本地渲染一个**代理**（用首次遇到该类型时缓存的模板：克隆一份、
  剥掉所有逻辑组件，只留模型 + Animator），按网络数据平移 / 转动 / 播动作；
* 1.2 秒收不到更新就销毁代理。

于是两边看到的是**同一批 NPC 的并集**——你看到的每个路人，队友也看得到。

![NPC 代理](docs/screenshot-npc-proxy.png)

#### 伤害中继（v2.5）

代理本身是死的，但它**记得主人是谁**。你打中代理时：

1. 本地把攻击锥内的代理挑出来，给每个代理的**所属玩家**发一条
   `NpcDamage(npcId, 伤害, 攻击者, 命中点)`；
2. 主人在自己世界里用 `instanceID` 找到那个**真 NPC**，执行 `OnHit`；
3. 掉血 / 死亡通过常规 NPC 状态包回流，代理随后消失。

所以现在**可以一起打同一个敌人**了。实测日志：

```
[NPC] relay test, real npc hp before=200
npc damage relayed: Soldier_Stickman NPC(Clone) 200 -> 188
```

**当前限制**：

* 并集模型意味着人群密度大约是单机的两倍；
* 只同步人形 NPC，不同步载具；
* 代理的类型必须本地见过一次才有模板，否则跳过（日志里会记 `npc type not seen locally`）；
* 代理没有碰撞体，不会被推挤，也不会攻击你（AI 在主人那边跑）。

## 4. 配置文件

`/sdcard/Android/data/com.mgc.stickman.rope.hero/files/SRHCoop.cfg`

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `Port` | 27811 | UDP 端口 |
| `AutoStart` | true | 进世界后自动联机 |
| `MeleeDamage` / `RangedDamage` | 18 / 12 | 对远程玩家的伤害 |
| `MeleeRange` / `RangedRange` | 3.2 / 60 | 判定距离 |
| `SendRateHz` | 15 | 状态包频率 |
| `UiScale` | 1.2 | 面板缩放 |
| `SkipPrivacyScreen` | true | 跳过隐私页 |
| `BootSplashSeconds` | 3.0 | 开机画面（商标 + 吉祥物）停留秒数，0.5–12 |
| `ConnectIp` | 空 | 直连目标 IP（面板里点「直连」会自动写入） |
| `SyncNpcs` | true | 同步附近 NPC（面板里有同名开关） |

日志：同目录 `SRHCoop.log`（也可 `adb logcat -s Unity` 看）。

## 5. 实现方式（混血注入）

```
原版 APK
 ├─ assets/bin/Data/Managed/SRHCoop.dll      ← 新增，Unity 会随其他脚本程序集一起加载
 └─ assets/bin/Data/Managed/Assembly-CSharp.dll  ← 仅在两个 Awake 开头各插一条 call
        PrivacyPolicyManager.Awake  → CoopBootstrap.Init()
        GameManager.Awake           → CoopBootstrap.Init()
```

* `CoopBootstrap.Init()` 只做一次：`new GameObject("SRHCoop")` + `DontDestroyOnLoad` + `AddComponent<CoopBehaviour>()`。
* IL 补丁用 Mono.Cecil 完成（`tools/IlPatch`），两条 `call`，栈平衡、不影响原逻辑。
* 为什么不用 `[RuntimeInitializeOnLoadMethod]`：Unity 在**构建期**就把这张回调表編译进播放器了，
  后塞进 `Managed/` 的程序集不会被扫描（实测确认，日志里没有输出），所以改用 IL 引导。

对比旧 BepInEx 方案：去掉了 `libbe5hook.so`（GOT hook）、`Be5Bootstrap.dll`、BepInEx core、
DEX 的 `System.loadLibrary("be5hook")` 补丁、内存加载程序集、迷你 chainloader——启动路径从 6 步降到 2 步。

## 6. 已验证 / 未验证

在 MuMu Player 12（Android 12 + ARM 转译）实测：

| 项目 | 结果 |
| --- | --- |
| 混血启动（IL 引导） | ✅ `bootstrap ok (hybrid, no BepInEx)` |
| 中文字体 / 面板 | ✅ 自动加载 Noto Sans CJK SC |
| 进入世界自动联机 | ✅ `LAN started on port 27811`，状态：主机 |
| 远程角色 + 血条 | ✅ 克隆模型、名字、血条均正常 |
| 主机判定 + 伤害下发 | ✅ `hit by zzzz0e8c for 10 (hp 300→290)` |
| 回环联机测试（完整收发链路） | ✅ 状态包 → 生成角色 → 攻击意图 → 主机判定 → 受击 → 超时移除 |
| 开机吉祥物（商标下方、黑底） | ✅ 仅在商标页显示，3 秒淡出 |
| 圆角面板 / 玩家卡片 / 血条 | ✅ 见上方第二张图 |
| **双真机对打** | ❌ 本环境只有一个模拟器实例，未验证 |

## 7. 重新构建

```powershell
.\build_hybrid.ps1 -IlPatch -Apk "F:\QQdownland\Stickman Rope Hero_3.4.186.apk"
```
产物 `dist\StickmanRopeHero_3.4.186_Coop.apk`。不加 `-IlPatch` 只加 DLL（联机不会启动，仅用于对照）。

## 8. 已知限制

* 位置仍是各端权威（没做输入预测/回滚），高延迟下远端角色会有插值滞后。
* 只同步角色本体：载具、抓取、任务、载具内状态不同步。
* 动画用 state hash + normalizedTime 同步，切换瞬间可能轻微跳变。
* APK **保留游戏原有的全部 ABI**（`armeabi-v7a` + `x86`）。注意游戏本身**没有 arm64 库**，
  所以纯 64 位设备仍然装不上——这是原包的限制，不是 mod 的。x86 模拟器现在可以原生跑了。
* 需要同网段且未开 AP 隔离；若路由器拦广播，用面板的「扫描」或「直连」。
* 协议 v3 与旧版本不兼容，两端必须同一个包。



