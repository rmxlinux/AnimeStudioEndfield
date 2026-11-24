# AnimeStudio 终末地 Humanoid 修复 v5 — 2026-09-28

修复了 Humanoid 通道错位造成的抬臂遮头、左右腿姿态不一致和足踝异常，也修复了 `A_actor_typhoea_interact_bow_attack_01` 的 ACL transform mask 报错。模型仍使用自己的原始 Avatar、骨架及绑定姿态；不使用固定的 90° 补偿。

## 使用

- 本包有 `net9.0-windows` 和 `net8.0-windows` 两个版本，各含 GUI、CLI 和原生库。选择与本机 .NET Desktop Runtime 对应的版本，运行目录下的 `AnimeStudio.GUI.exe`。本机已验证两个 CLI 都能启动。
- 保持 exe 与同目录下的 `bin` 一起移动；不要只复制 exe。
- Game 选择 `ArknightsEndfield`。导出完整人体 FBX 时同时加载模型、Avatar、prefab 和动作所在 bundle，再导出选定模型及动作。只加载动画无法恢复模型相关的人体姿态。
- Humanoid FBX 烘焙需要 Unity Editor，本机验证版本为 `2021.3.39f1c1`。可通过环境变量 `ANIMESTUDIO_UNITY_EDITOR` 指定 Unity.exe。普通 `.anim` / `.curves.json` 导出无需 Unity。首次烘焙后会缓存。
- CLI 示例：`AnimeStudio.CLI.exe <含模型及动作的目录> <输出目录> --game ArknightsEndfield --fbx-model chr_0028_wulfa_postmodel --filter sit_start`。

## 修复依据

游戏的 HumanTrait 身体 muscle 序列为 61 项，标准 Unity 为 55 项，后面都接 40 项手指。游戏新增左右脚 Foot Twist Roll、左右脚趾 Toes Left-Right 和 Toes Twist Roll。旧代码按标准顺序直接读取，让右腿、双臂和手指发生串位。现在先按游戏序列映射，再交给标准 Unity HumanPoseHandler 烘焙。

原始 Animator 属性 42..102 为 61 个身体 muscle，103..142 为 40 个手指，143 才是 TDoF 起点。六个扩展属性为 70、72、73、81、83、84；137..142 仍是手指。`.anim` 为扩展轴使用独立名称，JSON 保留原始属性编号和值。

有序名称表来自本机 `D:Endfield GameUnityPlayer.dll`，文件偏移 `0x1add070`，SHA256 为 `bee7be52370adddd67ba61e4937ca51b7f272656841d187e95e505496da798d1`。详见包内 `validation/endfield_humanoid_layout_v5.json`。`UnityEngine.HumanTrait.get_MuscleName` 的反编译代码是 icall 入口，不能仅按标准 Unity 文档推定顺序。

ACL 掩码由三个紧密相连的位平面组成，最后可以有零填充。报错动作有 362 条 QVV track，每个位平面 46 字节，共 138 字节数据，序列化数组为 144 字节。修复保留位平面紧密排列，接受最多 15 字节的全零尾部填充；继续检查 track 数量、绑定数量和路径一致性。

## 验证

- 报错的 typhoea 动作通过 GUI 预览所调用的 `clip.Convert()`，并通过最终打包 CLI 导出 `.anim` 和 `.curves.json`：875 条总轨道、362 条旋转、151 条标量、143 条 Humanoid 属性，名称没有碰撞，六个扩展轴保留。
- 掩码紧密数据与允许的零填充通过；截断、非零尾部、过长尾部被拒绝。
- Wulfa `sit_start` 经真实 ModelConverter 烘焙，186 个采样 × 23 根人体骨骼与已确认正确的 Blender 恢复源数据一致。最终包成功导出完整模型 FBX。
- 最终 CLI 导出的 FBX 重新导入 Blender 后有 76 个对象及 76 个 action；对照导出前骨骼局部变换，抽样最大角度误差约 0.040°，位置误差约 0.0000025 米。
- 配套 v5 Blender 文件的 11 个动作，每个均绑定 76 个部件；强制只用文件内嵌数据验证成功，重复绑定不会产生新的 action。抽样矩阵最大角度误差约 0.056°，位置误差小于 0.000005 米。这一数值说明数据传输一致性，不代表与游戏运行时 IK 的误差。
- 构建含 net8/net9 GUI 和 CLI，使用 `build-endfield-acl.ps1` 完成，0 编译错误；项目原有与反射字段等编译警告仍在构建日志中。

## 当前边界

六个游戏扩展脚部轴没有标准 Unity HumanPose 槽位，目前未应用于 FBX/Blender 人体烘焙。非零轴会在 AnimeStudio 日志中明确警告；`.anim` 和 `.curves.json` 保留原始数据。配套 Blender 数据记录每条轴的最大原始值。这版解决了主要通道错位问题，但不宣称完整还原游戏的脚趾细节。

游戏运行时的地形 IK、目标约束、Avatar retarget 和 corrective bone 处理也不等同于动画资源的单独播放。非零 Motion root-motion 或不支持的 TDoF 在人体 FBX 导出时会明确拒绝，原始标量仍可导出。

## 相关源码入口

游戏源码位于 `D:终末地源码source`：

- `UnityEngine/HumanTrait/get_MuscleName.c`：定位原生 muscle 名称及顺序的入口。
- `Beyond/Gameplay/View/ComplexAnimatorComponent/_SetupAnimation.c`：动画系统初始化、mask、retarget/profile 的接线。
- `Beyond/Gameplay/View/ComplexAnimatorComponent/_SetupRetargetAnim.c`：Avatar 和重定向比例设置。
- `Beyond/Gameplay/View/Animation/CharacterLimbIKBrain/_ApplySlotToSolver.c`：肢体 goal 位置和旋转权重。
- `Beyond/Gameplay/View/Animation/IK/IKRiggingBase/LateTick.c`：后处理 IK 的时序。
- `Beyond/Gameplay/Animations/RiggingExtension/HGTwoBoneIKConstraintJob/ProcessAnimation.c`：双骨 IK 作业。

修复代码位于本地 AnimeStudio 源码树的 `AnimeStudio.Utility/ACL/EndfieldHumanoidLayout.cs`、`EndfieldHumanoidBaker.cs`、`EndfieldClipDecoder.cs` 及 `AnimeStudio.Utility/YAML/AnimationClipConverter.cs`。构建命令：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File build-endfield-acl.ps1 -OutputRoot <不存在的新目录>`。
